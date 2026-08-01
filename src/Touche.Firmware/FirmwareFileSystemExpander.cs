// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.Firmware;

public sealed record FirmwareFileSystemExpansionResult(
    string DestinationDirectory,
    int ImageCount,
    int FileCount,
    int DirectoryCount,
    long ExtractedBytes);

/// <summary>
/// Expands recognized filesystem images from a decrypted PUP entry directory
/// into an atomic, ordinary directory tree suitable for profile import.
/// </summary>
public sealed class FirmwareFileSystemExpander
{
    private readonly ExFatVolumeExtractor _exFatExtractor = new();

    public async Task<FirmwareFileSystemExpansionResult> ExpandAsync(
        string entriesDirectory,
        string destinationDirectory,
        ExFatExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entriesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var source = Path.GetFullPath(entriesDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"The firmware entry directory was not found: {source}");
        }
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The firmware entry directory cannot be a link or reparse point.");
        }
        var sourcePrefix = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, PathComparison))
        {
            throw new InvalidDataException("The expansion destination cannot be inside the firmware entry directory.");
        }

        var images = new List<string>();
        foreach (var path in Directory.EnumerateFiles(source).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Firmware entries cannot be links or reparse points: {path}");
            }
            if (await IsExFatAsync(path, cancellationToken).ConfigureAwait(false))
            {
                images.Add(path);
            }
        }
        if (images.Count == 0)
        {
            throw new InvalidDataException("No exFAT filesystem images were found in the firmware entries.");
        }

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("The expansion destination has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(staging);
        try
        {
            var fileCount = 0;
            var directoryCount = 0;
            long totalBytes = 0;
            foreach (var image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imageDirectory = Path.Combine(staging, Path.GetFileNameWithoutExtension(image));
                var result = await _exFatExtractor
                    .ExtractAsync(image, imageDirectory, options, cancellationToken)
                    .ConfigureAwait(false);
                fileCount = checked(fileCount + result.FileCount);
                directoryCount = checked(directoryCount + result.DirectoryCount);
                totalBytes = checked(totalBytes + result.ExtractedBytes);
            }

            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            Directory.Move(staging, destination);
            return new FirmwareFileSystemExpansionResult(
                destination,
                images.Count,
                fileCount,
                directoryCount,
                totalBytes);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task<bool> IsExFatAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[11];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var total = 0;
        while (total < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            total += read;
        }
        return header.AsSpan(3, 8).SequenceEqual("EXFAT   "u8);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
