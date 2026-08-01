// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using Touche.Firmware;
using Xunit;

namespace Touche.Firmware.Tests;

public sealed class FirmwareDirectoryImporterTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "ToucheFirmwareImportTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanProducesDeterministicPortableManifest()
    {
        var first = CreateExtractedTree("first", reverseCreationOrder: false);
        var second = CreateExtractedTree("second", reverseCreationOrder: true);
        var scanner = new FirmwareDirectoryScanner();

        var firstScan = await scanner.ScanAsync(first);
        var secondScan = await scanner.ScanAsync(second);

        Assert.Equal(firstScan.Manifest.ProfileId, secondScan.Manifest.ProfileId);
        Assert.Equal(firstScan.Manifest.ContentHash, secondScan.Manifest.ContentHash);
        Assert.Equal(
            firstScan.Manifest.Artifacts.Select(artifact => artifact.VirtualPath),
            secondScan.Manifest.Artifacts.Select(artifact => artifact.VirtualPath));
        Assert.Equal(FirmwareArtifactKind.ElfOrSelf, firstScan.Manifest.Artifacts[0].Kind);
        Assert.Equal(FirmwareArtifactKind.Configuration, firstScan.Manifest.Artifacts[1].Kind);
        var json = JsonSerializer.Serialize(firstScan.Manifest);
        Assert.DoesNotContain(_temporaryDirectory, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SourceDirectory", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportPromotesVerifiedObjectsAndIsIdempotent()
    {
        var source = CreateExtractedTree("source", reverseCreationOrder: false);
        var store = Path.Combine(_temporaryDirectory, "store");
        var importer = new FirmwareDirectoryImporter(store);

        var first = await importer.ImportAsync(source);
        var second = await importer.ImportAsync(source);

        Assert.False(first.AlreadyImported);
        Assert.True(second.AlreadyImported);
        Assert.True(File.Exists(Path.Combine(first.ProfileDirectory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(first.ProfileDirectory, "modules.json")));
        Assert.Single(first.ModuleCatalog!.Modules);
        foreach (var artifact in first.Manifest.Artifacts)
        {
            var objectPath = Path.Combine(store, "objects", artifact.Sha256[..2], artifact.Sha256);
            Assert.True(File.Exists(objectPath));
            Assert.Equal(artifact.Size, new FileInfo(objectPath).Length);
            Assert.Equal(
                artifact.Sha256,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(objectPath))).ToLowerInvariant());
        }
    }

    [Fact]
    public async Task ScanEnforcesFileAndByteLimits()
    {
        var source = CreateExtractedTree("limited", reverseCreationOrder: false);
        var scanner = new FirmwareDirectoryScanner();

        await Assert.ThrowsAsync<InvalidDataException>(() => scanner.ScanAsync(
            source,
            new FirmwareScanOptions { MaximumFileCount = 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => scanner.ScanAsync(
            source,
            new FirmwareScanOptions { MaximumTotalBytes = 4 }));
    }

    [Fact]
    public async Task ScanRecognizesExtractedPupContainersAndFileSystems()
    {
        var root = Path.Combine(_temporaryDirectory, "container-signatures");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "outer.bin"), "SLB2payload"u8.ToArray());
        var exfat = new byte[512];
        "EXFAT   "u8.CopyTo(exfat.AsSpan(3));
        File.WriteAllBytes(Path.Combine(root, "system-image.bin"), exfat);
        var scanner = new FirmwareDirectoryScanner();

        var result = await scanner.ScanAsync(root);

        Assert.Contains(result.Manifest.Artifacts, artifact =>
            artifact.VirtualPath == "/outer.bin" &&
            artifact.Kind == FirmwareArtifactKind.Archive &&
            artifact.State == FirmwareArtifactState.Recognized);
        Assert.Contains(result.Manifest.Artifacts, artifact =>
            artifact.VirtualPath == "/system-image.bin" &&
            artifact.Kind == FirmwareArtifactKind.FileSystemImage &&
            artifact.State == FirmwareArtifactState.Recognized);
    }

    [Fact]
    public async Task ReimportRejectsTamperedCasObject()
    {
        var source = CreateExtractedTree("tamper-source", reverseCreationOrder: false);
        var store = Path.Combine(_temporaryDirectory, "tamper-store");
        var importer = new FirmwareDirectoryImporter(store);
        var imported = await importer.ImportAsync(source);
        var artifact = imported.Manifest.Artifacts[0];
        var objectPath = Path.Combine(store, "objects", artifact.Sha256[..2], artifact.Sha256);
        var bytes = File.ReadAllBytes(objectPath);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(objectPath, bytes);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(source));

        Assert.Contains("hash verification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepositorySummarizesImportedProfileAndIgnoresCorruption()
    {
        var source = CreateExtractedTree("repository-source", reverseCreationOrder: false);
        var store = Path.Combine(_temporaryDirectory, "repository-store");
        var importer = new FirmwareDirectoryImporter(store);
        var imported = await importer.ImportAsync(source);
        var repository = new FirmwareProfileRepository(store);

        var profile = Assert.Single(repository.GetImportedProfiles());

        Assert.Equal(imported.Manifest.ProfileId, profile.ProfileId);
        Assert.Equal(2, profile.ArtifactCount);
        Assert.Equal(1, profile.ModuleCount);
        Assert.Equal(1, profile.IncompatibleModuleCount);
        File.WriteAllText(Path.Combine(imported.ProfileDirectory, "manifest.json"), "{}");
        Assert.Empty(repository.GetImportedProfiles());
    }

    private string CreateExtractedTree(string name, bool reverseCreationOrder)
    {
        var root = Path.Combine(_temporaryDirectory, name);
        Directory.CreateDirectory(Path.Combine(root, "system", "common", "lib"));
        Directory.CreateDirectory(Path.Combine(root, "system", "config"));
        var files = new (string Path, byte[] Content)[]
        {
            (Path.Combine(root, "system", "common", "lib", "libExample.sprx"),
                [0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0]),
            (Path.Combine(root, "system", "config", "version.json"),
                "{\"version\":\"test\"}"u8.ToArray()),
        };
        foreach (var file in reverseCreationOrder ? files.Reverse() : files)
        {
            File.WriteAllBytes(file.Path, file.Content);
        }

        return root;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
