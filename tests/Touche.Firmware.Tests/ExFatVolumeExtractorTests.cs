// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using Touche.Firmware;
using Xunit;

namespace Touche.Firmware.Tests;

public sealed class ExFatVolumeExtractorTests : IDisposable
{
    private const int SectorSize = 512;
    private const int FatSector = 24;
    private const int HeapSector = 25;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ToucheExFatTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExtractsContiguousAndFatChainedFiles()
    {
        Directory.CreateDirectory(_root);
        var image = Path.Combine(_root, "system.exfat");
        CreateImage(image, corruptRootChecksum: false);
        var destination = Path.Combine(_root, "expanded");

        var result = await new ExFatVolumeExtractor().ExtractAsync(image, destination);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(2, result.DirectoryCount);
        Assert.Equal("root", File.ReadAllText(Path.Combine(destination, "root.txt")));
        var nested = File.ReadAllBytes(Path.Combine(destination, "system", "module.sprx"));
        Assert.Equal(600, nested.Length);
        Assert.All(nested, value => Assert.Equal((byte)0x5a, value));
    }

    [Fact]
    public async Task RejectsCorruptDirectoryEntrySet()
    {
        Directory.CreateDirectory(_root);
        var image = Path.Combine(_root, "corrupt.exfat");
        CreateImage(image, corruptRootChecksum: true);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ExFatVolumeExtractor().ExtractAsync(image, Path.Combine(_root, "expanded")));

        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpanderFindsImagesBySignatureAndUsesAtomicTree()
    {
        var entries = Path.Combine(_root, "entries");
        Directory.CreateDirectory(entries);
        CreateImage(Path.Combine(entries, "entry.bin"), corruptRootChecksum: false);
        File.WriteAllText(Path.Combine(entries, "metadata.bin"), "not a filesystem");
        var destination = Path.Combine(_root, "expanded");

        var result = await new FirmwareFileSystemExpander().ExpandAsync(entries, destination);

        Assert.Equal(1, result.ImageCount);
        Assert.Equal(2, result.FileCount);
        Assert.True(File.Exists(Path.Combine(destination, "entry", "root.txt")));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".*.staging"));
    }

    [Fact]
    [Trait("Category", "ManualIntegration")]
    public async Task ExpandsConfiguredRealFirmwareImages()
    {
        var entries = Environment.GetEnvironmentVariable("TOUCHEPX5_TEST_EXFAT_ENTRIES");
        var destination = Environment.GetEnvironmentVariable("TOUCHEPX5_TEST_EXFAT_OUTPUT");
        if (string.IsNullOrWhiteSpace(entries) || string.IsNullOrWhiteSpace(destination))
        {
            return;
        }

        var result = await new FirmwareFileSystemExpander().ExpandAsync(entries, destination);

        Assert.Equal(2, result.ImageCount);
        Assert.True(result.FileCount > 0);
        Assert.True(result.ExtractedBytes > 0);
    }

    [Fact]
    [Trait("Category", "ManualIntegration")]
    public async Task ImportsConfiguredExpandedFirmwareProfile()
    {
        var expanded = Environment.GetEnvironmentVariable("TOUCHEPX5_TEST_EXPANDED_FIRMWARE");
        var store = Environment.GetEnvironmentVariable("TOUCHEPX5_TEST_PROFILE_STORE");
        if (string.IsNullOrWhiteSpace(expanded) || string.IsNullOrWhiteSpace(store))
        {
            return;
        }

        var result = await new FirmwareDirectoryImporter(store).ImportAsync(expanded);

        Assert.True(result.Manifest.Artifacts.Count > 0);
        Assert.True(result.ModuleCatalog?.Modules.Count >= 500);
    }

    private static void CreateImage(string path, bool corruptRootChecksum)
    {
        var image = new byte[40 * SectorSize];
        image[0] = 0xeb;
        image[1] = 0x76;
        image[2] = 0x90;
        "EXFAT   "u8.CopyTo(image.AsSpan(3));
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(72), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(80), FatSector);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(84), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(88), HeapSector);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(92), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(96), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(104), 0x0100);
        image[108] = 9;
        image[109] = 0;
        image[110] = 1;
        image[510] = 0x55;
        image[511] = 0xaa;

        var fat = image.AsSpan(FatSector * SectorSize, SectorSize);
        SetFat(fat, 2, 0xffffffff);
        SetFat(fat, 4, 0xffffffff);
        SetFat(fat, 5, 6);
        SetFat(fat, 6, 0xffffffff);

        var root = GetCluster(image, 2);
        WriteFileSet(root, 0, "root.txt", directory: false, noFatChain: true, firstCluster: 3, dataLength: 4);
        WriteFileSet(root, 96, "system", directory: true, noFatChain: false, firstCluster: 4, dataLength: 512);
        if (corruptRootChecksum)
        {
            root[10] ^= 0x01;
        }
        "root"u8.CopyTo(GetCluster(image, 3));

        var subdirectory = GetCluster(image, 4);
        WriteFileSet(
            subdirectory,
            0,
            "module.sprx",
            directory: false,
            noFatChain: false,
            firstCluster: 5,
            dataLength: 600,
            allocationLength: 1024);
        GetCluster(image, 5).Fill(0x5a);
        GetCluster(image, 6).Fill(0x5a);
        File.WriteAllBytes(path, image);
    }

    private static void WriteFileSet(
        Span<byte> directoryBytes,
        int offset,
        string name,
        bool directory,
        bool noFatChain,
        uint firstCluster,
        ulong dataLength,
        ulong? allocationLength = null)
    {
        var set = directoryBytes.Slice(offset, 96);
        set.Clear();
        set[0] = 0x85;
        set[1] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(set[4..], directory ? (ushort)0x10 : (ushort)0x20);
        var stream = set[32..64];
        stream[0] = 0xc0;
        stream[1] = (byte)(0x01 | (noFatChain ? 0x02 : 0));
        stream[3] = checked((byte)name.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(stream[8..], dataLength);
        BinaryPrimitives.WriteUInt32LittleEndian(stream[20..], firstCluster);
        BinaryPrimitives.WriteUInt64LittleEndian(stream[24..], allocationLength ?? dataLength);
        var fileName = set[64..96];
        fileName[0] = 0xc1;
        Encoding.Unicode.GetBytes(name).CopyTo(fileName[2..]);
        BinaryPrimitives.WriteUInt16LittleEndian(set[2..], ComputeChecksum(set));
    }

    private static ushort ComputeChecksum(ReadOnlySpan<byte> set)
    {
        ushort checksum = 0;
        for (var index = 0; index < set.Length; index++)
        {
            if (index is 2 or 3)
            {
                continue;
            }
            checksum = (ushort)(((checksum << 15) | (checksum >> 1)) + set[index]);
        }
        return checksum;
    }

    private static void SetFat(Span<byte> fat, uint cluster, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(checked((int)cluster * 4), 4), value);

    private static Span<byte> GetCluster(byte[] image, uint cluster) =>
        image.AsSpan(checked(HeapSector * SectorSize + (int)(cluster - 2) * SectorSize), SectorSize);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
