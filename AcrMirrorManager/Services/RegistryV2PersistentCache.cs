using System.Text.Json;
using AcrMirrorManager.Models;
using AcrMirrorManager.Options;
using Microsoft.Extensions.Options;

namespace AcrMirrorManager.Services;

public sealed class RegistryV2PersistentCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly IWebHostEnvironment _environment;
    private readonly RegistryV2Options _options;

    public RegistryV2PersistentCache(IWebHostEnvironment environment, IOptions<RegistryV2Options> options)
    {
        _environment = environment;
        _options = options.Value;
    }

    public async Task<RegistryV2CacheDocument> ReadAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnlockedAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task UpdateAsync(Action<RegistryV2CacheDocument> update, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadUnlockedAsync(cancellationToken);
            update(document);
            await WriteUnlockedAsync(document, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<RegistryV2CacheDocument> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        var path = CachePath();
        if (!File.Exists(path))
        {
            return new RegistryV2CacheDocument();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RegistryV2CacheDocument>(stream, JsonOptions, cancellationToken)
            ?? new RegistryV2CacheDocument();
    }

    private async Task WriteUnlockedAsync(RegistryV2CacheDocument document, CancellationToken cancellationToken)
    {
        var path = CachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var tempPath = $"{path}.tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string CachePath()
    {
        return Path.IsPathRooted(_options.CachePath)
            ? _options.CachePath
            : Path.Combine(_environment.ContentRootPath, _options.CachePath);
    }
}
