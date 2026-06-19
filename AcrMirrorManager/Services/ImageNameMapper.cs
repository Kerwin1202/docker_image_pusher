using System.Text.RegularExpressions;
using AcrMirrorManager.Models;

namespace AcrMirrorManager.Services;

public static partial class ImageNameMapper
{
    public static ImageMapping ToAliyunImageMapping(string imageLine)
    {
        return new ImageMapping(
            NormalizeImageLine(imageLine),
            ToAliyunRepositoryName(imageLine),
            ToAliyunTag(imageLine));
    }

    public static string ToAliyunRepositoryName(string imageLine)
    {
        var sourceImage = ExtractSourceImage(imageLine);
        var imageWithoutDigest = sourceImage.Split('@', 2)[0];
        var imageWithoutTag = StripTag(imageWithoutDigest);
        var imagePath = StripRegistryHost(imageWithoutTag);
        var flatRepository = imagePath.Replace('/', '_');
        var platformSegment = ExtractPlatform(imageLine)?.Replace('/', '_');

        return string.IsNullOrWhiteSpace(platformSegment)
            ? flatRepository
            : $"{flatRepository}_{platformSegment}";
    }

    public static string ToAliyunTag(string imageLine)
    {
        var sourceImage = ExtractSourceImage(imageLine);
        var imageWithoutDigest = sourceImage.Split('@', 2)[0];
        var fileName = imageWithoutDigest.Split('/')[^1];
        var tagSeparator = fileName.LastIndexOf(':');

        return tagSeparator >= 0 ? fileName[(tagSeparator + 1)..] : "latest";
    }

    public static IEnumerable<string> ExtractImageLines(string imagesText, bool includeCommentedImages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in imagesText.Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace("\r", "\n", StringComparison.Ordinal)
                     .Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var uncommented = line;
            if (line.StartsWith('#'))
            {
                if (!includeCommentedImages)
                {
                    continue;
                }

                uncommented = line.TrimStart('#').TrimStart();
            }

            if (!LooksLikeImageLine(uncommented))
            {
                continue;
            }

            if (seen.Add(uncommented))
            {
                yield return uncommented;
            }
        }
    }

    private static string NormalizeImageLine(string imageLine)
    {
        var normalized = imageLine.Trim();
        while (normalized.StartsWith("#", StringComparison.Ordinal))
        {
            normalized = normalized[1..].TrimStart();
        }

        return normalized;
    }

    private static string ExtractSourceImage(string imageLine)
    {
        var tokens = Whitespace().Split(NormalizeImageLine(imageLine)).Where(static x => x.Length > 0).ToArray();
        if (tokens.Length == 0)
        {
            throw new ArgumentException("镜像地址不能为空。", nameof(imageLine));
        }

        return tokens[^1];
    }

    private static string? ExtractPlatform(string imageLine)
    {
        var tokens = Whitespace().Split(NormalizeImageLine(imageLine)).Where(static x => x.Length > 0).ToArray();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "--platform" && i + 1 < tokens.Length)
            {
                return tokens[i + 1];
            }

            if (tokens[i].StartsWith("--platform=", StringComparison.Ordinal))
            {
                return tokens[i]["--platform=".Length..];
            }
        }

        return null;
    }

    private static string StripTag(string image)
    {
        var lastSlash = image.LastIndexOf('/');
        var lastColon = image.LastIndexOf(':');
        return lastColon > lastSlash ? image[..lastColon] : image;
    }

    private static string StripRegistryHost(string imagePath)
    {
        var firstSlash = imagePath.IndexOf('/');
        if (firstSlash < 0)
        {
            return imagePath;
        }

        var firstPart = imagePath[..firstSlash];
        return firstPart.Contains('.', StringComparison.Ordinal)
            || firstPart.Contains(':', StringComparison.Ordinal)
            || firstPart.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? imagePath[(firstSlash + 1)..]
            : imagePath;
    }

    private static bool LooksLikeImageLine(string line)
    {
        if (line.Length == 0 || line.Contains('：', StringComparison.Ordinal))
        {
            return false;
        }

        if (line.StartsWith("--platform", StringComparison.Ordinal))
        {
            return Whitespace().Split(line).Length >= 2;
        }

        if (line.Contains(' ') || line.Contains('\t'))
        {
            return false;
        }

        return line.Contains('/', StringComparison.Ordinal)
            || line.Contains(':', StringComparison.Ordinal)
            || line.Contains('.', StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
