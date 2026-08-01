// Copyright (C) 2026 Touché PX5 Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using System.Buffers.Binary;
using System.IO.Compression;

namespace SharpEmu.GUI;

public sealed record InstalledFirmware(
    string Sha256,
    long Size,
    DateTimeOffset InstalledAtUtc,
    string ContainerFormat,
    string SourceFileName,
    string PackagePath,
    FirmwareContainerKind ContainerKind,
    int? EntryCount,
    bool HasVersionMetadataEntry,
    int ExtractedEntryCount)
{
    public string DisplayName =>
        $"{ContainerFormat} · {FormatSize(Size)} · {Sha256[..Math.Min(12, Sha256.Length)]}";

    private static string FormatSize(long bytes)
    {
        const double gib = 1024d * 1024d * 1024d;
        const double mib = 1024d * 1024d;
        return bytes >= gib ? $"{bytes / gib:0.00} GiB" : $"{bytes / mib:0.0} MiB";
    }
}

public readonly record struct FirmwareInstallProgress(long BytesCopied, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(BytesCopied * 100d / TotalBytes, 0, 100);
}

public sealed record FirmwareInstallResult(InstalledFirmware Firmware, bool AlreadyInstalled);

/// <summary>
/// Validates and stores user-supplied firmware packages. Entries from an
/// already-decrypted PUP can be extracted locally; protected content is
/// never decrypted or redistributed.
/// </summary>
public sealed class FirmwareManager
{
    private const int ManifestSchemaVersion = 3;
    private const int MinimumContainerSize = 512;
    private const string PackageFileName = "PS5UPDATE.PUP";
    private const string ManifestFileName = "manifest.json";
    private const string InventoryFileName = "inventory.json";
    private const string EntriesDirectoryName = "entries";
    private const long MaximumBlockTableBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _rootDirectory;

    public FirmwareManager()
        : this(Path.Combine(AppContext.BaseDirectory, "user", "firmware"))
    {
    }

    internal FirmwareManager(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory => _rootDirectory;

    public IReadOnlyList<InstalledFirmware> GetInstalled()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return [];
        }

        var installed = new List<InstalledFirmware>();
        foreach (var directory in Directory.EnumerateDirectories(_rootDirectory))
        {
            var hash = Path.GetFileName(directory);
            if (!IsSha256(hash))
            {
                continue;
            }

            var packagePath = Path.Combine(directory, PackageFileName);
            var manifestPath = Path.Combine(directory, ManifestFileName);
            try
            {
                if (!File.Exists(packagePath) || !File.Exists(manifestPath))
                {
                    continue;
                }

                var manifest = JsonSerializer.Deserialize<FirmwareManifest>(
                    File.ReadAllText(manifestPath),
                    JsonOptions);
                if (manifest is null ||
                    manifest.SchemaVersion is < 1 or > ManifestSchemaVersion ||
                    !string.Equals(manifest.Sha256, hash, StringComparison.OrdinalIgnoreCase) ||
                    new FileInfo(packagePath).Length != manifest.Size)
                {
                    continue;
                }

                installed.Add(ToInstalled(manifest, packagePath));
            }
            catch (Exception)
            {
                // An interrupted or manually edited installation is ignored.
            }
        }

        installed.Sort((a, b) => b.InstalledAtUtc.CompareTo(a.InstalledAtUtc));
        return installed;
    }

    public async Task<FirmwareInstallResult> InstallAsync(
        string sourcePath,
        IProgress<FirmwareInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("The firmware package was not found.", fullSourcePath);
        }

        var sourceInfo = new FileInfo(fullSourcePath);
        if (sourceInfo.Length < MinimumContainerSize)
        {
            throw new InvalidDataException("The firmware package is too small to contain a valid container.");
        }

        var inspection = await FirmwarePackageInspector
            .InspectAsync(fullSourcePath, cancellationToken)
            .ConfigureAwait(false);

        if (inspection.Kind == FirmwareContainerKind.BackupArchiveSiecaf)
        {
            throw new InvalidDataException(
                "The selected file is a PS5 Backup and Restore archive (SIECAF), not a firmware package.");
        }

        if (!string.Equals(Path.GetExtension(fullSourcePath), ".pup", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Firmware packages must use the .PUP extension.");
        }

        Directory.CreateDirectory(_rootDirectory);
        var stagingDirectory = Path.Combine(_rootDirectory, ".staging");
        Directory.CreateDirectory(stagingDirectory);
        var stagingPath = Path.Combine(stagingDirectory, $"{Guid.NewGuid():N}.pup.tmp");

        try
        {
            string hash;
            await using (var source = new FileStream(
                             fullSourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             stagingPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1024 * 1024];
                long copied = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hasher.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(new FirmwareInstallProgress(copied, sourceInfo.Length));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            var extractedEntries = inspection.Kind == FirmwareContainerKind.DecryptedPup
                ? await ExtractEntriesAsync(
                        stagingPath,
                        stagingDirectory,
                        inspection.Entries,
                        cancellationToken)
                    .ConfigureAwait(false)
                : new ExtractedFirmwareEntries(string.Empty, []);

            var installationDirectory = Path.Combine(_rootDirectory, hash);
            var packagePath = Path.Combine(installationDirectory, PackageFileName);
            var manifestPath = Path.Combine(installationDirectory, ManifestFileName);
            var inventoryPath = Path.Combine(installationDirectory, InventoryFileName);
            var entriesPath = Path.Combine(installationDirectory, EntriesDirectoryName);
            var alreadyInstalled = File.Exists(packagePath) &&
                                   new FileInfo(packagePath).Length == sourceInfo.Length;

            Directory.CreateDirectory(installationDirectory);
            if (alreadyInstalled)
            {
                File.Delete(stagingPath);
            }
            else
            {
                File.Move(stagingPath, packagePath, true);
            }

            if (inspection.Kind == FirmwareContainerKind.DecryptedPup)
            {
                ReplaceDirectory(extractedEntries.DirectoryPath, entriesPath);
                await File.WriteAllTextAsync(
                    inventoryPath + ".tmp",
                    JsonSerializer.Serialize(extractedEntries.Inventory, JsonOptions),
                    cancellationToken).ConfigureAwait(false);
                File.Move(inventoryPath + ".tmp", inventoryPath, true);
            }

            var manifest = new FirmwareManifest(
                ManifestSchemaVersion,
                hash,
                sourceInfo.Length,
                DateTimeOffset.UtcNow,
                inspection.FormatLabel,
                Path.GetFileName(fullSourcePath),
                inspection.Kind,
                inspection.EntryCount,
                inspection.HasVersionMetadataEntry,
                extractedEntries.Inventory);
            var manifestTempPath = manifestPath + ".tmp";
            await File.WriteAllTextAsync(
                manifestTempPath,
                JsonSerializer.Serialize(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(manifestTempPath, manifestPath, true);

            return new FirmwareInstallResult(ToInstalled(manifest, packagePath), alreadyInstalled);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }
    }

    private static InstalledFirmware ToInstalled(FirmwareManifest manifest, string packagePath) => new(
        manifest.Sha256,
        manifest.Size,
        manifest.InstalledAtUtc,
        manifest.ContainerFormat,
        manifest.SourceFileName,
        packagePath,
        manifest.ContainerKind,
        manifest.EntryCount,
        manifest.HasVersionMetadataEntry,
        manifest.Entries?.Count(entry => entry.ExtractedRelativePath is not null) ?? 0);

    private static async Task<ExtractedFirmwareEntries> ExtractEntriesAsync(
        string packagePath,
        string stagingRoot,
        IReadOnlyList<FirmwarePackageEntry> entries,
        CancellationToken cancellationToken)
    {
        var directoryPath = Path.Combine(stagingRoot, $"entries-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        var inventory = new List<FirmwareEntryManifest>(entries.Count);

        try
        {
            await using var source = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            var buffer = new byte[1024 * 1024];
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? relativePath = null;
                string? extractedSha256 = null;
                if (entry.CanExtract)
                {
                    var outputPath = Path.Combine(directoryPath, entry.FileName);
                    var tableEntry = entry.IsBlocked
                        ? FindBlockTable(entries, entry)
                        : null;
                    await ExtractEntryAsync(
                        source,
                        entry,
                        tableEntry,
                        outputPath,
                        buffer,
                        cancellationToken).ConfigureAwait(false);
                    var detectedFileName = await DetectExtractedFileNameAsync(
                        outputPath,
                        entry.FileName,
                        cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(detectedFileName, entry.FileName, StringComparison.Ordinal))
                    {
                        var detectedPath = Path.Combine(directoryPath, detectedFileName);
                        File.Move(outputPath, detectedPath);
                        outputPath = detectedPath;
                    }
                    relativePath = Path.Combine(EntriesDirectoryName, detectedFileName).Replace('\\', '/');
                    extractedSha256 = await ComputeFileHashAsync(outputPath, cancellationToken)
                        .ConfigureAwait(false);
                }

                inventory.Add(new FirmwareEntryManifest(
                    entry.Index,
                    entry.Id,
                    entry.Flags,
                    entry.Offset,
                    entry.StoredSize,
                    entry.UnpackedSize,
                    entry.IsCompressed,
                    entry.IsBlocked,
                    entry.IsSpecial,
                    relativePath,
                    extractedSha256));
            }

            return new ExtractedFirmwareEntries(directoryPath, inventory);
        }
        catch
        {
            TryDeleteDirectory(directoryPath);
            throw;
        }
    }

    private static FirmwarePackageEntry? FindBlockTable(
        IReadOnlyList<FirmwarePackageEntry> entries,
        FirmwarePackageEntry blockedEntry)
    {
        var matches = entries
            .Where(candidate => (candidate.Flags & 1) != 0 && candidate.Id == blockedEntry.Index)
            .ToArray();
        return matches.Length switch
        {
            0 when !blockedEntry.IsCompressed => null,
            0 => throw new InvalidDataException($"PUP entry {blockedEntry.Index} has no block table."),
            1 => matches[0],
            _ => throw new InvalidDataException($"PUP entry {blockedEntry.Index} has multiple block tables."),
        };
    }

    private static async Task ExtractEntryAsync(
        FileStream source,
        FirmwarePackageEntry entry,
        FirmwarePackageEntry? tableEntry,
        string outputPath,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (!entry.IsBlocked)
        {
            if (entry.IsCompressed)
            {
                await DecompressRangeExactlyAsync(
                    source,
                    entry.Offset,
                    entry.StoredSize,
                    destination,
                    entry.UnpackedSize,
                    buffer,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                source.Position = checked((long)entry.Offset);
                await CopyExactlyAsync(
                    source,
                    destination,
                    checked((long)entry.StoredSize),
                    buffer,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await ExtractBlockedEntryAsync(
                source,
                destination,
                entry,
                tableEntry,
                buffer,
                cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if ((ulong)destination.Length != entry.UnpackedSize)
        {
            throw new InvalidDataException(
                $"PUP entry {entry.Index} extracted {destination.Length} bytes; expected {entry.UnpackedSize}.");
        }
    }

    private static async Task ExtractBlockedEntryAsync(
        FileStream source,
        FileStream destination,
        FirmwarePackageEntry entry,
        FirmwarePackageEntry? tableEntry,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var blockShift = checked((int)((entry.Flags & 0xF000) >> 12) + 12);
        if (blockShift is < 12 or > 30)
        {
            throw new InvalidDataException($"PUP entry {entry.Index} has an invalid block size.");
        }

        var blockSize = 1UL << blockShift;
        var blockCount = checked((int)((entry.UnpackedSize + blockSize - 1) / blockSize));
        BlockInfo[]? blockInfos = null;
        if (entry.IsCompressed)
        {
            if (tableEntry is null)
            {
                throw new InvalidDataException($"PUP entry {entry.Index} has no block table.");
            }

            var tableBytes = await ReadEntryBytesAsync(source, tableEntry, buffer, cancellationToken)
                .ConfigureAwait(false);
            var infoOffset = checked(blockCount * 32);
            var infoBytes = checked(blockCount * 8);
            if (infoOffset > tableBytes.Length || infoBytes > tableBytes.Length - infoOffset)
            {
                throw new InvalidDataException($"PUP block table for entry {entry.Index} is truncated.");
            }

            blockInfos = new BlockInfo[blockCount];
            for (var index = 0; index < blockCount; index++)
            {
                var record = tableBytes.AsSpan(infoOffset + index * 8, 8);
                blockInfos[index] = new BlockInfo(
                    BinaryPrimitives.ReadUInt32LittleEndian(record),
                    BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
            }
        }

        ulong sequentialOffset = 0;
        for (var index = 0; index < blockCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputSize = Math.Min(blockSize, entry.UnpackedSize - (ulong)index * blockSize);
            var inputOffset = sequentialOffset;
            var inputSize = outputSize;
            var compressed = false;

            if (blockInfos is not null)
            {
                var info = blockInfos[index];
                if (info.Offset != 0)
                {
                    inputOffset = info.Offset;
                }

                var padding = info.Size & 0xF;
                var alignedSize = info.Size & ~0xFU;
                if (alignedSize < padding)
                {
                    throw new InvalidDataException($"PUP block {index} for entry {entry.Index} has invalid padding.");
                }

                var compressedBytes = alignedSize - padding;
                if (compressedBytes != blockSize &&
                    (index != blockCount - 1 || outputSize != info.Size))
                {
                    compressed = true;
                }
                else
                {
                    inputSize = info.Size;
                }

                var physicalEnd = entry.StoredSize;
                if (index + 1 < blockInfos.Length &&
                    blockInfos[index + 1].Offset > inputOffset)
                {
                    physicalEnd = Math.Min(physicalEnd, blockInfos[index + 1].Offset);
                }
                if (physicalEnd < inputOffset)
                {
                    throw new InvalidDataException(
                        $"PUP block {index} for entry {entry.Index} has a decreasing offset.");
                }

                var physicalSize = physicalEnd - inputOffset;
                if (compressed)
                {
                    // Some PUPs round the final table size beyond the declared
                    // entry boundary. The offsets are the authoritative physical
                    // layout; zlib terminates before any alignment padding.
                    inputSize = Math.Min(compressedBytes, physicalSize);
                    if (inputSize == 0)
                    {
                        throw new InvalidDataException(
                            $"Compressed PUP block {index} for entry {entry.Index} is empty.");
                    }
                }
                else if (inputSize > physicalSize)
                {
                    throw new InvalidDataException(
                        $"Raw PUP block {index} for entry {entry.Index} exceeds its physical range.");
                }
            }

            if (inputOffset > entry.StoredSize || inputSize > entry.StoredSize - inputOffset)
            {
                throw new InvalidDataException($"PUP block {index} for entry {entry.Index} is outside its payload.");
            }

            if (compressed)
            {
                await DecompressRangeExactlyAsync(
                    source,
                    entry.Offset + inputOffset,
                    inputSize,
                    destination,
                    outputSize,
                    buffer,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (inputSize != outputSize)
                {
                    throw new InvalidDataException($"Raw PUP block {index} for entry {entry.Index} has an invalid size.");
                }
                source.Position = checked((long)(entry.Offset + inputOffset));
                await CopyExactlyAsync(
                    source,
                    destination,
                    checked((long)inputSize),
                    buffer,
                    cancellationToken).ConfigureAwait(false);
            }

            sequentialOffset = checked(inputOffset + inputSize);
        }
    }

    private static async Task<byte[]> ReadEntryBytesAsync(
        FileStream source,
        FirmwarePackageEntry entry,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (entry.UnpackedSize > MaximumBlockTableBytes)
        {
            throw new InvalidDataException($"PUP block table {entry.Index} is too large.");
        }

        await using var memory = new MemoryStream(checked((int)entry.UnpackedSize));
        if (entry.IsCompressed)
        {
            await DecompressRangeExactlyAsync(
                source,
                entry.Offset,
                entry.StoredSize,
                memory,
                entry.UnpackedSize,
                buffer,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            source.Position = checked((long)entry.Offset);
            await CopyExactlyAsync(
                source,
                memory,
                checked((long)entry.StoredSize),
                buffer,
                cancellationToken).ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    private static async Task DecompressRangeExactlyAsync(
        FileStream source,
        ulong offset,
        ulong storedSize,
        Stream destination,
        ulong expectedSize,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        source.Position = checked((long)offset);
        await using var bounded = new BoundedReadStream(source, checked((long)storedSize));
        await using var zlib = new ZLibStream(bounded, CompressionMode.Decompress, leaveOpen: false);
        ulong written = 0;
        while (written < expectedSize)
        {
            var requested = (int)Math.Min((ulong)buffer.Length, expectedSize - written);
            var read = await zlib.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("A compressed PUP entry ended before its declared size.");
            }
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += checked((uint)read);
        }

        if (await zlib.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("A compressed PUP entry exceeds its declared size.");
        }
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long byteCount,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var remaining = byteCount;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The firmware entry ended before its declared size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<string> DetectExtractedFileNameAsync(
        string path,
        string fallbackFileName,
        CancellationToken cancellationToken)
    {
        var header = new byte[16];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            header.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        var bytes = header.AsSpan(0, read);
        var extension = bytes.Length switch
        {
            >= 11 when bytes[3..11].SequenceEqual("EXFAT   "u8) => ".exfat",
            >= 4 when bytes.StartsWith("SLB2"u8) => ".slb2",
            >= 4 when bytes.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }) => ".zip",
            >= 4 when bytes.StartsWith(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' }) => ".elf",
            >= 4 when BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0xEEF51454 => ".self",
            >= 4 when bytes.StartsWith("hsqs"u8) => ".squashfs",
            >= 5 when bytes.StartsWith("<?xml"u8) => ".xml",
            _ => Path.GetExtension(fallbackFileName),
        };
        return Path.ChangeExtension(fallbackFileName, extension);
    }

    private static void ReplaceDirectory(string sourcePath, string destinationPath)
    {
        TryDeleteDirectory(destinationPath);
        Directory.Move(sourcePath, destinationPath);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => char.IsAsciiHexDigit(character));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private sealed record ExtractedFirmwareEntries(
        string DirectoryPath,
        IReadOnlyList<FirmwareEntryManifest> Inventory);

    private sealed record FirmwareEntryManifest(
        int Index,
        uint Id,
        uint Flags,
        ulong Offset,
        ulong StoredSize,
        ulong UnpackedSize,
        bool IsCompressed,
        bool IsBlocked,
        bool IsSpecial,
        string? ExtractedRelativePath,
        string? ExtractedSha256);

    private readonly record struct BlockInfo(uint Offset, uint Size);

    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        private long _remaining = length >= 0 ? length : throw new ArgumentOutOfRangeException(nameof(length));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => length - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var requested = (int)Math.Min(count, _remaining);
            if (requested == 0) return 0;
            var read = inner.Read(buffer, offset, requested);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var requested = (int)Math.Min(buffer.Length, _remaining);
            if (requested == 0) return 0;
            var read = await inner.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record FirmwareManifest(
        int SchemaVersion,
        string Sha256,
        long Size,
        DateTimeOffset InstalledAtUtc,
        string ContainerFormat,
        string SourceFileName,
        FirmwareContainerKind ContainerKind = FirmwareContainerKind.OfficialSlb2,
        int? EntryCount = null,
        bool HasVersionMetadataEntry = false,
        IReadOnlyList<FirmwareEntryManifest>? Entries = null);
}
