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
    bool HasVersionMetadataEntry)
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
/// Validates and stores user-supplied firmware packages. This deliberately
/// does not decrypt, extract, or redistribute proprietary package contents.
/// </summary>
public sealed class FirmwareManager
{
    private const int ManifestSchemaVersion = 2;
    private const int MinimumContainerSize = 512;
    private const string PackageFileName = "PS5UPDATE.PUP";
    private const string ManifestFileName = "manifest.json";
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

        if (!string.Equals(Path.GetExtension(fullSourcePath), ".pup", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Firmware packages must use the .PUP extension.");
        }

        var sourceInfo = new FileInfo(fullSourcePath);
        if (sourceInfo.Length < MinimumContainerSize)
        {
            throw new InvalidDataException("The firmware package is too small to contain a valid container.");
        }

        var inspection = await FirmwarePackageInspector
            .InspectAsync(fullSourcePath, cancellationToken)
            .ConfigureAwait(false);

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

            var installationDirectory = Path.Combine(_rootDirectory, hash);
            var packagePath = Path.Combine(installationDirectory, PackageFileName);
            var manifestPath = Path.Combine(installationDirectory, ManifestFileName);
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

            var manifest = new FirmwareManifest(
                ManifestSchemaVersion,
                hash,
                sourceInfo.Length,
                DateTimeOffset.UtcNow,
                inspection.FormatLabel,
                Path.GetFileName(fullSourcePath),
                inspection.Kind,
                inspection.EntryCount,
                inspection.HasVersionMetadataEntry);
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
        manifest.HasVersionMetadataEntry);

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

    private sealed record FirmwareManifest(
        int SchemaVersion,
        string Sha256,
        long Size,
        DateTimeOffset InstalledAtUtc,
        string ContainerFormat,
        string SourceFileName,
        FirmwareContainerKind ContainerKind = FirmwareContainerKind.OfficialSlb2,
        int? EntryCount = null,
        bool HasVersionMetadataEntry = false);
}
