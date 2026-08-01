// Copyright (C) 2026 Touché PX5 Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using System.Buffers.Binary;
using System.IO.Compression;
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
    public async Task InstallAsyncExtractsCompressedEntry()
    {
        var sourcePath = CreateDecryptedPackage(
            "compressed.PUP",
            invalidEntryBounds: false,
            compressed: true);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        var result = await manager.InstallAsync(sourcePath);

        Assert.Equal(1, result.Firmware.EntryCount);
        Assert.Equal(1, result.Firmware.ExtractedEntryCount);
        var installationDirectory = Path.GetDirectoryName(result.Firmware.PackagePath)!;
        var extractedPath = Path.Combine(installationDirectory, "entries", "entry-0000-id-00c.bin");
        Assert.Equal(Enumerable.Range(1, 32).Select(value => (byte)value), File.ReadAllBytes(extractedPath));
        Assert.Contains("\"IsCompressed\": true", File.ReadAllText(Path.Combine(installationDirectory, "inventory.json")));
    }

    [Fact]
    public async Task InstallAsyncExtractsBlockedCompressedEntry()
    {
        var expected = Enumerable.Range(0, 4096)
            .Select(index => (byte)((index * 37 + 11) & 0xff))
            .ToArray();
        "EXFAT   "u8.CopyTo(expected.AsSpan(3));
        var sourcePath = CreateBlockedCompressedPackage("blocked.PUP", expected);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        var result = await manager.InstallAsync(sourcePath);

        Assert.Equal(2, result.Firmware.EntryCount);
        Assert.Equal(2, result.Firmware.ExtractedEntryCount);
        var installationDirectory = Path.GetDirectoryName(result.Firmware.PackagePath)!;
        var extractedPath = Path.Combine(installationDirectory, "entries", "entry-0001-id-203.exfat");
        Assert.Equal(expected, File.ReadAllBytes(extractedPath));
    }

    [Fact]
    public async Task InstallAsyncRejectsTruncatedBlockedTable()
    {
        var sourcePath = CreateBlockedCompressedPackage(
            "truncated-block-table.PUP",
            Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray(),
            truncateTable: true);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(sourcePath));

        Assert.Contains("block table", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manager.GetInstalled());
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
        var payload = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var storedPayload = compressed ? Compress(payload) : payload;
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
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entryOffset + 16), (ulong)storedPayload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(entryOffset + 24), (ulong)payload.Length);
        storedPayload.CopyTo(data.AsSpan(64));

        var path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string CreateBlockedCompressedPackage(
        string fileName,
        byte[] payload,
        bool truncateTable = false)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var compressed = Compress(payload);
        var alignedCompressedSize = (compressed.Length + 15) & ~15;
        var padding = alignedCompressedSize - compressed.Length;
        // Real PUP block tables can round the final encoded size beyond the
        // entry's physical end. Offsets must remain the extraction boundary.
        var encodedSize = checked((uint)((alignedCompressedSize + 0x100) | padding));

        const int headerSize = 96;
        var tableSize = truncateTable ? 32 : 40;
        var tableOffset = headerSize;
        var payloadOffset = tableOffset + tableSize;
        var data = new byte[Math.Max(1024, payloadOffset + alignedCompressedSize)];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xEEF51454);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x0C), headerSize);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x10), (ulong)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x18), 2);

        WritePupEntry(
            data.AsSpan(32, 32),
            flags: (1U << 20) | 1U,
            offset: (ulong)tableOffset,
            storedSize: (ulong)tableSize,
            unpackedSize: (ulong)tableSize);
        WritePupEntry(
            data.AsSpan(64, 32),
            flags: (0x203U << 20) | 0x8U | 0x800U | (7U << 12),
            offset: (ulong)payloadOffset,
            storedSize: (ulong)alignedCompressedSize,
            unpackedSize: (ulong)payload.Length);

        if (!truncateTable)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(tableOffset + 32), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(tableOffset + 36), encodedSize);
        }
        compressed.CopyTo(data.AsSpan(payloadOffset));

        var path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static void WritePupEntry(
        Span<byte> destination,
        uint flags,
        ulong offset,
        ulong storedSize,
        ulong unpackedSize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, flags);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], storedSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], unpackedSize);
    }

    private static byte[] Compress(ReadOnlySpan<byte> payload)
    {
        using var destination = new MemoryStream();
        using (var zlib = new ZLibStream(destination, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(payload);
        }
        return destination.ToArray();
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
