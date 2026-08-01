// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Touche.Firmware;

public sealed class FirmwareDirectoryScanner
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public async Task<FirmwareDirectoryScan> ScanAsync(
        string sourceDirectory,
        FirmwareScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        options ??= new FirmwareScanOptions();
        ValidateOptions(options);

        var root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The extracted firmware directory was not found: {root}");
        }

        RejectReparsePoint(root);
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var files = EnumerateFilesSafely(root, rootPrefix, options.MaximumFileCount, cancellationToken);
        if (files.Count == 0)
        {
            throw new InvalidDataException("The extracted firmware directory is empty.");
        }

        var artifacts = new List<FirmwareArtifact>(files.Count);
        long totalBytes = 0;
        foreach (var filePath in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > options.MaximumSingleFileBytes)
            {
                throw new InvalidDataException($"Firmware artifact is too large: {ToVirtualPath(root, filePath)}");
            }

            totalBytes = checked(totalBytes + fileInfo.Length);
            if (totalBytes > options.MaximumTotalBytes)
            {
                throw new InvalidDataException(
                    $"The extracted firmware directory exceeds the {options.MaximumTotalBytes} byte limit.");
            }

            var (sha256, header, bytesRead) = await HashFileAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (bytesRead != fileInfo.Length || new FileInfo(filePath).Length != bytesRead)
            {
                throw new IOException($"Firmware artifact changed while it was being scanned: {filePath}");
            }

            var virtualPath = ToVirtualPath(root, filePath);
            var (kind, state) = Classify(filePath, header);
            artifacts.Add(new FirmwareArtifact(virtualPath, bytesRead, sha256, kind, state));
        }

        artifacts.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.VirtualPath, right.VirtualPath));
        var contentHash = ComputeManifestContentHash(artifacts);
        var manifest = new FirmwareProfileManifest
        {
            ProfileId = $"ps5-extracted-{contentHash}",
            ContentHash = contentHash,
            Artifacts = artifacts,
        };
        return new FirmwareDirectoryScan(root, manifest, totalBytes);
    }

    internal static string ResolveArtifactPath(string root, string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath) || virtualPath[0] != '/' || virtualPath.Contains('\\'))
        {
            throw new InvalidDataException($"Invalid firmware virtual path: {virtualPath}");
        }

        var components = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0 || components.Any(component => component is "." or ".." || component.Contains('\0')))
        {
            throw new InvalidDataException($"Invalid firmware virtual path: {virtualPath}");
        }

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine([fullRoot, .. components]));
        var prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, PathComparison))
        {
            throw new InvalidDataException($"Firmware virtual path escapes its source directory: {virtualPath}");
        }

        return candidate;
    }

    private static List<string> EnumerateFilesSafely(
        string root,
        string rootPrefix,
        int maximumFileCount,
        CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(entry);
                if (!fullPath.StartsWith(rootPrefix, PathComparison))
                {
                    throw new InvalidDataException($"Firmware entry escapes the selected directory: {entry}");
                }

                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Links and reparse points are not accepted: {entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(fullPath);
                }
                else
                {
                    files.Add(fullPath);
                    if (files.Count > maximumFileCount)
                    {
                        throw new InvalidDataException(
                            $"The extracted firmware directory exceeds the {maximumFileCount} file limit.");
                    }
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static async Task<(string Sha256, byte[] Header, long BytesRead)> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var header = new byte[16];
        var headerLength = 0;
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (headerLength < header.Length)
            {
                var copyLength = Math.Min(header.Length - headerLength, read);
                buffer.AsSpan(0, copyLength).CopyTo(header.AsSpan(headerLength));
                headerLength += copyLength;
            }

            hasher.AppendData(buffer, 0, read);
            total += read;
        }

        return (
            Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
            header.AsSpan(0, headerLength).ToArray(),
            total);
    }

    private static string ToVirtualPath(string root, string filePath)
    {
        var relative = Path.GetRelativePath(root, filePath);
        if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Firmware entry escapes the selected directory: {filePath}");
        }

        return "/" + relative.Replace('\\', '/');
    }

    private static (FirmwareArtifactKind Kind, FirmwareArtifactState State) Classify(
        string path,
        ReadOnlySpan<byte> header)
    {
        if ((header.Length >= 4 && header[0] == 0x7f && header[1..].StartsWith("ELF"u8)) ||
            header.StartsWith(new byte[] { 0x4f, 0x15, 0x3d, 0x1d }))
        {
            return (FirmwareArtifactKind.ElfOrSelf, FirmwareArtifactState.Recognized);
        }
        if (header.StartsWith("hsqs"u8))
        {
            return (FirmwareArtifactKind.FileSystemImage, FirmwareArtifactState.Recognized);
        }
        if (header.Length >= 11 && header[3..11].SequenceEqual("EXFAT   "u8))
        {
            return (FirmwareArtifactKind.FileSystemImage, FirmwareArtifactState.Recognized);
        }
        if (header.StartsWith("SLB2"u8))
        {
            return (FirmwareArtifactKind.Archive, FirmwareArtifactState.Recognized);
        }
        if (header.Length >= 4 &&
            header[0] == (byte)'P' &&
            header[1] == (byte)'K' &&
            header[2] == 0x03 &&
            header[3] == 0x04)
        {
            return (FirmwareArtifactKind.Archive, FirmwareArtifactState.Compressed);
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".json" or ".xml" or ".ini" or ".cfg" or ".conf" =>
                (FirmwareArtifactKind.Configuration, FirmwareArtifactState.Recognized),
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".wav" or ".at9" =>
                (FirmwareArtifactKind.Resource, FirmwareArtifactState.Recognized),
            ".zip" or ".gz" or ".tar" or ".7z" =>
                (FirmwareArtifactKind.Archive, FirmwareArtifactState.Compressed),
            ".img" or ".squashfs" =>
                (FirmwareArtifactKind.FileSystemImage, FirmwareArtifactState.Recognized),
            _ => (FirmwareArtifactKind.Unknown, FirmwareArtifactState.Hashed),
        };
    }

    private static string ComputeManifestContentHash(IReadOnlyList<FirmwareArtifact> artifacts)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[sizeof(long)];
        foreach (var artifact in artifacts)
        {
            var path = Encoding.UTF8.GetBytes(artifact.VirtualPath);
            hasher.AppendData(path);
            hasher.AppendData([0]);
            BinaryPrimitives.WriteInt64LittleEndian(size, artifact.Size);
            hasher.AppendData(size);
            hasher.AppendData(Convert.FromHexString(artifact.Sha256));
            hasher.AppendData([(byte)artifact.Kind, (byte)artifact.State]);
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The extracted firmware root cannot be a link or reparse point.");
        }
    }

    private static void ValidateOptions(FirmwareScanOptions options)
    {
        if (options.MaximumFileCount <= 0 ||
            options.MaximumTotalBytes <= 0 ||
            options.MaximumSingleFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Firmware scan limits must be positive.");
        }
    }
}
