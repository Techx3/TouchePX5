// Copyright (C) 2026 Touché PX5 Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Buffers.Binary;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class FirmwareManagerTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "TouchePx5FirmwareTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsyncStoresValidatedPackageAndManifest()
    {
        var sourcePath = CreatePackage("valid.PUP", validMagic: true);
        var root = Path.Combine(_temporaryDirectory, "installed");
        var manager = new FirmwareManager(root);

        var result = await manager.InstallAsync(sourcePath);

        Assert.False(result.AlreadyInstalled);
        Assert.Equal("Official SLB2", result.Firmware.ContainerFormat);
        Assert.Equal(FirmwareContainerKind.OfficialSlb2, result.Firmware.ContainerKind);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(sourcePath)), Convert.FromHexString(result.Firmware.Sha256));
        Assert.True(File.Exists(result.Firmware.PackagePath));
        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(result.Firmware.PackagePath));

        var installed = Assert.Single(manager.GetInstalled());
        Assert.Equal(result.Firmware.Sha256, installed.Sha256);
    }

    [Fact]
    public async Task InstallingSamePackageTwiceIsIdempotent()
    {
        var sourcePath = CreatePackage("duplicate.pup", validMagic: true);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        var first = await manager.InstallAsync(sourcePath);
        var second = await manager.InstallAsync(sourcePath);

        Assert.False(first.AlreadyInstalled);
        Assert.True(second.AlreadyInstalled);
        Assert.Equal(first.Firmware.Sha256, second.Firmware.Sha256);
        Assert.Single(manager.GetInstalled());
    }

    [Fact]
    public async Task InstallAsyncRejectsWrongContainerMagic()
    {
        var sourcePath = CreatePackage("invalid.PUP", validMagic: false);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(sourcePath));
        Assert.Empty(manager.GetInstalled());
    }

    [Fact]
    public async Task InstallAsyncRejectsNonPupExtension()
    {
        var sourcePath = CreatePackage("firmware.bin", validMagic: true);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(sourcePath));
    }

    [Fact]
    public async Task InstallAsyncRecognizesBoundedDecryptedPupTable()
    {
        var sourcePath = CreateDecryptedPackage("decrypted.PUP", invalidEntryBounds: false);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        var result = await manager.InstallAsync(sourcePath);

        Assert.Equal(FirmwareContainerKind.DecryptedPup, result.Firmware.ContainerKind);
        Assert.Equal("Decrypted PUP", result.Firmware.ContainerFormat);
        Assert.Equal(1, result.Firmware.EntryCount);
        Assert.True(result.Firmware.HasVersionMetadataEntry);
        Assert.Equal(1, result.Firmware.ExtractedEntryCount);

        var installationDirectory = Path.GetDirectoryName(result.Firmware.PackagePath)!;
        var extractedPath = Path.Combine(installationDirectory, "entries", "entry-0000-id-00c.bin");
        Assert.True(File.Exists(extractedPath));
        Assert.Equal(Enumerable.Range(1, 32).Select(value => (byte)value), File.ReadAllBytes(extractedPath));
        Assert.True(File.Exists(Path.Combine(installationDirectory, "inventory.json")));
        Assert.Contains(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(extractedPath))).ToLowerInvariant(),
            File.ReadAllText(Path.Combine(installationDirectory, "inventory.json")));
        var reloaded = Assert.Single(manager.GetInstalled());
        Assert.Equal(1, reloaded.ExtractedEntryCount);
    }

    [Fact]
    public async Task InstallAsyncRejectsDecryptedEntryOutsidePackage()
    {
        var sourcePath = CreateDecryptedPackage("invalid-bounds.PUP", invalidEntryBounds: true);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(sourcePath));
    }

    [Fact]
    public async Task InstallAsyncInventoriesButDoesNotExtractCompressedEntry()
    {
        var sourcePath = CreateDecryptedPackage(
            "compressed.PUP",
            invalidEntryBounds: false,
            compressed: true);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        var result = await manager.InstallAsync(sourcePath);

        Assert.Equal(1, result.Firmware.EntryCount);
        Assert.Equal(0, result.Firmware.ExtractedEntryCount);
        var installationDirectory = Path.GetDirectoryName(result.Firmware.PackagePath)!;
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(installationDirectory, "entries")));
        Assert.Contains("\"IsCompressed\": true", File.ReadAllText(Path.Combine(installationDirectory, "inventory.json")));
    }

    [Fact]
    public async Task InspectorRecognizesBoundedSiecafBackupArchive()
    {
        var sourcePath = CreateSiecafArchive("archive.dat", invalidBounds: false);

        var result = await FirmwarePackageInspector.InspectAsync(sourcePath);

        Assert.Equal(FirmwareContainerKind.BackupArchiveSiecaf, result.Kind);
        Assert.Equal("PS5 Backup (SIECAF)", result.FormatLabel);
        Assert.Equal(1, result.EntryCount);
        Assert.False(result.CanInspectEntries);
    }

    [Fact]
    public async Task InstallAsyncRejectsSiecafBackupAsNotFirmware()
    {
        var sourcePath = CreateSiecafArchive("archive.dat", invalidBounds: false);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(sourcePath));

        Assert.Contains("Backup and Restore archive", error.Message);
        Assert.Empty(manager.GetInstalled());
    }

    [Fact]
    public async Task InspectorRejectsSiecafDataOutsideArchive()
    {
        var sourcePath = CreateSiecafArchive("invalid-archive.dat", invalidBounds: true);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => FirmwarePackageInspector.InspectAsync(sourcePath));
    }

    private string CreatePackage(string fileName, bool validMagic)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var data = new byte[1024];
        RandomNumberGenerator.Fill(data);
        (validMagic ? "SLB2"u8 : "NOPE"u8).CopyTo(data);
        var path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string CreateDecryptedPackage(string fileName, bool invalidEntryBounds, bool compressed = false)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var data = new byte[1024];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xEEF51454);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x0C), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x10), (ulong)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x18), 1);

        const int entryOffset = 32;
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(entryOffset),
            (0x0CU << 20) | (compressed ? 0x8U : 0U));
        BinaryPrimitives.WriteUInt64LittleEndian(
            data.AsSpan(entryOffset + 8),
            invalidEntryBounds ? 2048UL : 64UL);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entryOffset + 16), 32);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entryOffset + 24), 32);
        for (var index = 0; index < 32; index++)
        {
            data[64 + index] = (byte)(index + 1);
        }

        var path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string CreateSiecafArchive(string fileName, bool invalidBounds)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var data = new byte[1024];
        "SIECAF\0\0"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x08), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x18), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x40), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x48), invalidBounds ? 2048UL : 512UL);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x50), 512);

        var path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllBytes(path, data);
        return path;
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
