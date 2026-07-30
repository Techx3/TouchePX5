// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text.Json;

namespace Touche.Firmware;

public sealed class FirmwareDirectoryImporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _storeRoot;
    private readonly FirmwareDirectoryScanner _scanner;

    public FirmwareDirectoryImporter(string storeRoot, FirmwareDirectoryScanner? scanner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        _storeRoot = Path.GetFullPath(storeRoot);
        _scanner = scanner ?? new FirmwareDirectoryScanner();
    }

    public async Task<FirmwareImportResult> ImportAsync(
        string sourceDirectory,
        FirmwareScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var scan = await _scanner.ScanAsync(sourceDirectory, options, cancellationToken).ConfigureAwait(false);
        var profilesRoot = Path.Combine(_storeRoot, "profiles");
        var objectsRoot = Path.Combine(_storeRoot, "objects");
        var stagingRoot = Path.Combine(_storeRoot, ".staging");
        var profileDirectory = Path.Combine(profilesRoot, scan.Manifest.ProfileId);
        var manifestPath = Path.Combine(profileDirectory, "manifest.json");
        var alreadyImported = File.Exists(manifestPath);
        if (alreadyImported)
        {
            var existingManifest = JsonSerializer.Deserialize<FirmwareProfileManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false),
                JsonOptions);
            if (existingManifest is null ||
                !string.Equals(existingManifest.ProfileId, scan.Manifest.ProfileId, StringComparison.Ordinal) ||
                !string.Equals(existingManifest.ContentHash, scan.Manifest.ContentHash, StringComparison.Ordinal) ||
                !existingManifest.Artifacts.SequenceEqual(scan.Manifest.Artifacts))
            {
                throw new InvalidDataException($"Firmware profile manifest is inconsistent: {scan.Manifest.ProfileId}");
            }
        }

        Directory.CreateDirectory(profilesRoot);
        Directory.CreateDirectory(objectsRoot);
        Directory.CreateDirectory(stagingRoot);
        var stagingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            foreach (var artifact in scan.Manifest.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = FirmwareDirectoryScanner.ResolveArtifactPath(
                    scan.SourceDirectory,
                    artifact.VirtualPath);
                var objectDirectory = Path.Combine(objectsRoot, artifact.Sha256[..2]);
                var objectPath = Path.Combine(objectDirectory, artifact.Sha256);
                if (File.Exists(objectPath))
                {
                    if (new FileInfo(objectPath).Length != artifact.Size)
                    {
                        throw new InvalidDataException($"CAS object has an invalid size: {artifact.Sha256}");
                    }
                    var existingHash = await ComputeFileHashAsync(objectPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(existingHash, artifact.Sha256, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"CAS object failed hash verification: {artifact.Sha256}");
                    }
                    continue;
                }

                Directory.CreateDirectory(objectDirectory);
                var temporaryObject = Path.Combine(stagingDirectory, artifact.Sha256 + ".tmp");
                var copiedHash = await CopyAndHashAsync(sourcePath, temporaryObject, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(copiedHash, artifact.Sha256, StringComparison.Ordinal))
                {
                    throw new IOException($"Firmware artifact changed during import: {artifact.VirtualPath}");
                }

                try
                {
                    File.Move(temporaryObject, objectPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(objectPath))
                {
                    File.Delete(temporaryObject);
                }
            }

            if (alreadyImported)
            {
                return new FirmwareImportResult(scan.Manifest, profileDirectory, AlreadyImported: true);
            }

            var stagedProfile = Path.Combine(stagingDirectory, "profile");
            Directory.CreateDirectory(stagedProfile);
            await File.WriteAllTextAsync(
                Path.Combine(stagedProfile, "manifest.json"),
                JsonSerializer.Serialize(scan.Manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.Move(stagedProfile, profileDirectory);
            }
            catch (IOException) when (File.Exists(manifestPath))
            {
                Directory.Delete(stagedProfile, recursive: true);
                return new FirmwareImportResult(scan.Manifest, profileDirectory, AlreadyImported: true);
            }

            return new FirmwareImportResult(scan.Manifest, profileDirectory, AlreadyImported: false);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<string> ComputeFileHashAsync(
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
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }
}
