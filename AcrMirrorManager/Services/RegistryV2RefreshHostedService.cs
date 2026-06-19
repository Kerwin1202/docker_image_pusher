namespace AcrMirrorManager.Services;

public sealed class RegistryV2RefreshHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RegistryV2RefreshHostedService> _logger;

    public RegistryV2RefreshHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<RegistryV2RefreshHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (!stoppingToken.IsCancellationRequested)
        {
            await TickAsync(stoppingToken);

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var refresh = scope.ServiceProvider.GetRequiredService<IRegistryV2RefreshService>();
            await refresh.RunMirrorActionJobsAsync(cancellationToken);
            await refresh.RunDueRefreshJobsAsync(cancellationToken);
            await refresh.RunDailyMissingRefreshAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registry V2 background refresh failed.");
        }
    }
}
