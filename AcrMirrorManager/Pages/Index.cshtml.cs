using AcrMirrorManager.Models;
using AcrMirrorManager.Options;
using AcrMirrorManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace AcrMirrorManager.Pages;

public class IndexModel : PageModel
{
    private static readonly TimeSpan RecentMirrorWindow = TimeSpan.FromHours(4);

    private readonly IAcrRegistryService _acrRegistry;
    private readonly IGitHubMirrorService _gitHubMirror;
    private readonly IRegistryV2RefreshService _registryRefresh;
    private readonly GitHubMirrorOptions _gitHubOptions;

    public IndexModel(
        IAcrRegistryService acrRegistry,
        IGitHubMirrorService gitHubMirror,
        IRegistryV2RefreshService registryRefresh,
        IOptions<GitHubMirrorOptions> gitHubOptions)
    {
        _acrRegistry = acrRegistry;
        _gitHubMirror = gitHubMirror;
        _registryRefresh = registryRefresh;
        _gitHubOptions = gitHubOptions.Value;
    }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? RepoId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? RepoName { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? RepoNamespace { get; set; }

    [BindProperty(SupportsGet = true)]
    public string StatusFilter { get; set; } = "all";

    [BindProperty(SupportsGet = true)]
    public bool Refresh { get; set; }

    [BindProperty]
    public string ImageLine { get; set; } = string.Empty;

    [BindProperty]
    public bool CommentOtherImages { get; set; }

    [BindProperty]
    public List<string> SelectedImages { get; set; } = [];

    public IReadOnlyList<AcrRepository> Repositories { get; private set; } = [];

    public IReadOnlyList<AcrTag> Tags { get; private set; } = [];

    public AcrRepository? SelectedRepository { get; private set; }

    public bool SupportsDelete => _acrRegistry.SupportsDelete;

    public string? DefaultCopyAddress
    {
        get
        {
            if (SelectedRepository is null || Tags.Count == 0)
            {
                return null;
            }

            var tag = Tags.FirstOrDefault(x => x.Tag.Equals("latest", StringComparison.OrdinalIgnoreCase))?.Tag
                ?? Tags[0].Tag;

            return BuildImageAddress(SelectedRepository, tag);
        }
    }

    public string? ErrorMessage { get; private set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? CommitUrl { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        CommentOtherImages = _gitHubOptions.CommentOthersByDefault;
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSubmitImageAsync(CancellationToken cancellationToken)
    {
        var imageLines = ParseImageLines(ImageLine);
        if (imageLines.Count == 0)
        {
            const string message = "镜像地址不能为空。";
            if (IsAjaxRequest())
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return new JsonResult(new { ok = false, message });
            }

            ErrorMessage = message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        try
        {
            var pendingRemovedImages = await _registryRefresh.GetPendingRemovedImagesAsync(cancellationToken);
            var result = imageLines.Count == 1
                ? await SubmitSingleImageAsync(imageLines[0], pendingRemovedImages, cancellationToken)
                : await SubmitMultipleImagesAsync(imageLines, pendingRemovedImages, cancellationToken);

            await _registryRefresh.ClearPendingRemovedImagesAsync(pendingRemovedImages, cancellationToken);
            foreach (var sourceImage in result.SourceImages)
            {
                await _registryRefresh.TrackSubmittedImageAsync(sourceImage, result.CommitSha, result.CommitUrl, cancellationToken);
            }

            SuccessMessage = result.SourceImages.Count == 1
                ? $"已提交 {result.SourceImages[0]}，预计仓库名：{result.ExpectedRepositories[0]}，分支：{result.Branch}，Commit：{ShortSha(result.CommitSha)}。"
                : $"已提交 {result.SourceImages.Count} 个镜像，分支：{result.Branch}，Commit：{ShortSha(result.CommitSha)}。";
            CommitUrl = result.CommitUrl;

            if (IsAjaxRequest())
            {
                var repositories = await _acrRegistry.ListRepositoriesAsync(null, false, cancellationToken);
                var expected = result.ExpectedRepositories.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var updatedRepositories = repositories
                    .Where(x => expected.Contains(x.Name))
                    .Select(ToRepositoryDto)
                    .ToList();

                return new JsonResult(new
                {
                    ok = true,
                    message = SuccessMessage,
                    commitUrl = CommitUrl,
                    repository = updatedRepositories.Count == 1 ? updatedRepositories[0] : null,
                    repositories = updatedRepositories
                });
            }
        }
        catch (Exception ex)
        {
            if (IsAjaxRequest())
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return new JsonResult(new { ok = false, message = ex.Message });
            }

            ErrorMessage = ex.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage();
    }

    private async Task<MirrorBatchSubmissionResult> SubmitSingleImageAsync(
        string imageLine,
        IReadOnlyCollection<string> pendingRemovedImages,
        CancellationToken cancellationToken)
    {
        var result = await _gitHubMirror.SubmitImageAsync(imageLine, CommentOtherImages, pendingRemovedImages, cancellationToken);
        return new MirrorBatchSubmissionResult(
            [result.SourceImage],
            [result.ExpectedRepository],
            result.Branch,
            result.CommitSha,
            result.CommitUrl,
            result.WorkflowDispatchRequested);
    }

    private async Task<MirrorBatchSubmissionResult> SubmitMultipleImagesAsync(
        IReadOnlyCollection<string> imageLines,
        IReadOnlyCollection<string> pendingRemovedImages,
        CancellationToken cancellationToken)
    {
        return await _gitHubMirror.SubmitImagesAsync(imageLines, CommentOtherImages, pendingRemovedImages, cancellationToken);
    }

    public async Task<IActionResult> OnPostRepullImagesAsync(CancellationToken cancellationToken)
    {
        var selectedImages = SelectedImages
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedImages.Count == 0)
        {
            const string message = "请先选择要重新 pull 的源镜像。";
            if (IsAjaxRequest())
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return new JsonResult(new { ok = false, message });
            }

            ErrorMessage = message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        try
        {
            var pendingRemovedImages = await _registryRefresh.GetPendingRemovedImagesAsync(cancellationToken);
            var result = await _gitHubMirror.SubmitImagesAsync(selectedImages, commentOtherImages: true, pendingRemovedImages, cancellationToken);
            await _registryRefresh.ClearPendingRemovedImagesAsync(pendingRemovedImages, cancellationToken);
            foreach (var sourceImage in result.SourceImages)
            {
                await _registryRefresh.TrackSubmittedImageAsync(sourceImage, result.CommitSha, result.CommitUrl, cancellationToken);
            }

            SuccessMessage = $"已提交 {result.SourceImages.Count} 个镜像重新 pull，分支：{result.Branch}，Commit：{ShortSha(result.CommitSha)}。";
            CommitUrl = result.CommitUrl;

            if (IsAjaxRequest())
            {
                var repositories = await _acrRegistry.ListRepositoriesAsync(null, false, cancellationToken);
                var expected = result.ExpectedRepositories.ToHashSet(StringComparer.OrdinalIgnoreCase);

                return new JsonResult(new
                {
                    ok = true,
                    message = SuccessMessage,
                    commitUrl = CommitUrl,
                    repositories = repositories
                        .Where(x => expected.Contains(x.Name))
                        .Select(ToRepositoryDto)
                });
            }
        }
        catch (Exception ex)
        {
            if (IsAjaxRequest())
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return new JsonResult(new { ok = false, message = ex.Message });
            }

            ErrorMessage = ex.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveImageAsync(string sourceImage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceImage))
        {
            const string message = "请先选择要移除的源镜像。";
            if (IsAjaxRequest())
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return new JsonResult(new { ok = false, message });
            }

            ErrorMessage = message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        try
        {
            await _registryRefresh.RemoveTrackedImageAsync(sourceImage, cancellationToken);

            SuccessMessage = $"已从本地列表移除 {sourceImage}。下次提交镜像时会一并从 GitHub images.txt 移除。";
            CommitUrl = null;

            if (IsAjaxRequest())
            {
                return new JsonResult(new
                {
                    ok = true,
                    message = SuccessMessage,
                    commitUrl = (string?)null,
                    sourceImage,
                    repoId = ImageNameMapper.ToAliyunRepositoryName(sourceImage)
                });
            }
        }
        catch (Exception ex)
        {
            if (IsAjaxRequest())
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return new JsonResult(new { ok = false, message = ex.Message });
            }

            ErrorMessage = ex.Message;
            await LoadAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage(new { search = Search, statusFilter = StatusFilter });
    }


    public async Task<IActionResult> OnGetTagsAsync(string repoId, bool refresh, CancellationToken cancellationToken)
    {
        var repositories = await _acrRegistry.ListRepositoriesAsync(null, false, cancellationToken);
        var repository = repositories.FirstOrDefault(x => x.RepoId.Equals(repoId, StringComparison.OrdinalIgnoreCase));
        if (repository is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return new JsonResult(new { ok = false, message = "仓库不存在。" });
        }

        var tags = await _acrRegistry.ListTagsAsync(repoId, refresh, cancellationToken);
        var defaultTag = tags.FirstOrDefault(x => x.Tag.Equals("latest", StringComparison.OrdinalIgnoreCase))?.Tag
            ?? tags.FirstOrDefault()?.Tag;

        return new JsonResult(new
        {
            ok = true,
            repository = ToRepositoryDto(repository),
            defaultCopyAddress = defaultTag is null ? null : BuildImageAddress(repository, defaultTag),
            tags = tags.Select(tag => new
            {
                tag = tag.Tag,
                digest = tag.Digest,
                status = tag.Status,
                statusClass = StatusCss(tag.Status),
                copyAddress = BuildImageAddress(repository, tag.Tag)
            })
        });
    }

    public async Task<IActionResult> OnGetRepositoriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var repositories = await _acrRegistry.ListRepositoriesAsync(null, false, cancellationToken);
            return new JsonResult(new
            {
                ok = true,
                repositories = repositories.Select(ToRepositoryDto)
            });
        }
        catch (Exception ex)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return new JsonResult(new { ok = false, message = ex.Message });
        }
    }

    public async Task<IActionResult> OnPostDeleteTagAsync(
        string repoId,
        string repoName,
        string repoNamespace,
        string tag,
        CancellationToken cancellationToken)
    {
        try
        {
            await _acrRegistry.DeleteTagAsync(repoId, tag, cancellationToken);
            SuccessMessage = $"已删除 {repoNamespace}/{repoName}:{tag}。";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            RepoId = repoId;
            RepoName = repoName;
            RepoNamespace = repoNamespace;
            await LoadAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage(new { repoId, repoName, repoNamespace, search = Search });
    }

    public async Task<IActionResult> OnPostDeleteRepositoryAsync(
        string repoId,
        string repoName,
        string repoNamespace,
        CancellationToken cancellationToken)
    {
        try
        {
            await _acrRegistry.DeleteRepositoryAsync(repoId, repoName, repoNamespace, cancellationToken);
            SuccessMessage = $"已删除仓库 {repoNamespace}/{repoName}。";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            RepoId = repoId;
            RepoName = repoName;
            RepoNamespace = repoNamespace;
            await LoadAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage(new { search = Search });
    }

    public string FormatDate(DateTimeOffset? value)
    {
        return value?.ToString("yyyy-MM-dd HH:mm") ?? "-";
    }

    public string FormatProbePlan(AcrRepository repository)
    {
        var lastChecked = repository.UpdatedAt.HasValue
            ? $"最近探测 {FormatDate(repository.UpdatedAt)}"
            : "暂无探测记录";

        if (repository.PendingRefreshCount <= 0)
        {
            return lastChecked;
        }

        return $"{lastChecked}；下次 {FormatDate(repository.NextRefreshAt)}，剩余 {repository.PendingRefreshCount} 次";
    }

    public string FormatWorkflowPlan(AcrRepository repository)
    {
        if (string.IsNullOrWhiteSpace(repository.LastMirrorCommitSha))
        {
            return "暂无 Action 追踪";
        }

        var status = repository.LastWorkflowStatus switch
        {
            "completed" => repository.LastWorkflowConclusion == "success" ? "Action 成功" : $"Action {repository.LastWorkflowConclusion ?? "completed"}",
            "in_progress" => "Action 运行中",
            "queued" => "Action 排队中",
            "waiting" => "等待 Action",
            null or "" => "等待 Action",
            _ => $"Action {repository.LastWorkflowStatus}"
        };

        return repository.LastWorkflowCheckedAt.HasValue
            ? $"{status}；最近检查 {FormatDate(repository.LastWorkflowCheckedAt)}"
            : status;
    }

    public bool IsRecentMirror(AcrRepository repository)
    {
        if (!repository.LastMirrorSubmittedAt.HasValue)
        {
            return false;
        }

        var finishedAt = repository.LastMirrorCompletedAt ?? repository.LastMirrorFailedAt;
        return !finishedAt.HasValue || DateTimeOffset.Now - finishedAt.Value <= RecentMirrorWindow;
    }

    public string FormatSize(long? bytes)
    {
        if (!bytes.HasValue)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes.Value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    public string StatusCss(string status)
    {
        return status switch
        {
            "已存在" => "status-pill is-ready",
            "Action 成功" => "status-pill is-ready",
            "未推送" => "status-pill is-missing",
            "等待 ACR" => "status-pill is-missing",
            "可拉取" => "status-pill is-ready",
            "等待 Action" => "status-pill is-muted",
            "Action 运行中" => "status-pill is-muted",
            "Action 失败" => "status-pill is-failed",
            _ => "status-pill is-muted"
        };
    }

    public string FilterCss(string filter)
    {
        return StatusFilter.Equals(filter, StringComparison.OrdinalIgnoreCase)
            ? "segment is-active"
            : "segment";
    }

    public string BuildImageAddress(AcrRepository repository, string tag)
    {
        var target = repository.Summary.Split(" -> ", 2, StringSplitOptions.None).LastOrDefault() ?? string.Empty;
        target = target.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

        var tagSeparator = target.LastIndexOf(':');
        var slash = target.LastIndexOf('/');
        var withoutTag = tagSeparator > slash ? target[..tagSeparator] : target;

        return $"{withoutTag}:{tag}";
    }

    public string BuildDerivedImageAddress(AcrRepository repository)
    {
        var target = repository.Summary.Split(" -> ", 2, StringSplitOptions.None).LastOrDefault() ?? string.Empty;
        return target.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    public string BuildSourceImageAddress(AcrRepository repository)
    {
        return repository.Summary.Split(" -> ", 2, StringSplitOptions.None).FirstOrDefault()?.Trim() ?? string.Empty;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var repositories = await _acrRegistry.ListRepositoriesAsync(null, Refresh, cancellationToken);
            Repositories = repositories;

            SelectedRepository = Repositories.FirstOrDefault(x => x.RepoId == RepoId)
                ?? Repositories.FirstOrDefault();

            if (SelectedRepository is not null)
            {
                RepoId = SelectedRepository.RepoId;
                RepoName = SelectedRepository.Name;
                RepoNamespace = SelectedRepository.Namespace;
                Tags = await _acrRegistry.ListTagsAsync(SelectedRepository.RepoId, Refresh, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage ??= ex.Message;
        }
    }

    private static string ShortSha(string sha)
    {
        return sha.Length <= 7 ? sha : sha[..7];
    }

    private static List<string> ParseImageLines(string imageLines)
    {
        return imageLines
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsAjaxRequest()
    {
        return string.Equals(Request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    }

    private object ToRepositoryDto(AcrRepository repository)
    {
        return new
        {
            repoId = repository.RepoId,
            name = repository.Name,
            @namespace = repository.Namespace,
            status = repository.Status,
            statusClass = StatusCss(repository.Status),
            probePlan = FormatProbePlan(repository),
            workflowPlan = FormatWorkflowPlan(repository),
            workflowUrl = repository.LastWorkflowUrl,
            mirrorCommitUrl = repository.LastMirrorCommitUrl,
            isRecentMirror = IsRecentMirror(repository),
            nextRefreshAt = repository.NextRefreshAt,
            pendingRefreshCount = repository.PendingRefreshCount,
            summary = repository.Summary,
            sourceImage = BuildSourceImageAddress(repository),
            copyAddress = BuildDerivedImageAddress(repository),
            tagsUrl = Url.Page("/Index", "Tags", new { repoId = repository.RepoId })
        };
    }
}
