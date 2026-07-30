// Copyright (C) 2026 Touché PX5 Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text.Json;

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
/// Validates and stores user-supplied firmware packages. Direct entries from
/// an already-decrypted PUP can be extracted locally; protected content is
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
                ? await ExtractDirectEntriesAsync(
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

    private static async Task<ExtractedFirmwareEntries> ExtractDirectEntriesAsync(
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
                if (entry.CanExtractDirectly)
                {
                    relativePath = Path.Combine(EntriesDirectoryName, entry.FileName).Replace('\\', '/');
                    var outputPath = Path.Combine(directoryPath, entry.FileName);
                    source.Position = checked((long)entry.Offset);
                    await using var destination = new FileStream(
                        outputPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    extractedSha256 = await CopyExactlyAsync(
                        source,
                        destination,
                        checked((long)entry.StoredSize),
                        buffer,
                        cancellationToken).ConfigureAwait(false);
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

    private static async Task<string> CopyExactlyAsync(
        Stream source,
        Stream destination,
        long byteCount,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var remaining = byteCount;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (remaining > 0)
        {
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The firmware entry ended before its declared size.");
            }

            hasher.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
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
