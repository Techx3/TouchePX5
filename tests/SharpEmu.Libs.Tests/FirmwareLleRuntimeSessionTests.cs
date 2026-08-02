// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using Touche.Firmware;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class FirmwareLleRuntimeSessionTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"touche-runtime-lle-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("missingNid")]
    [InlineData("LwG8g3niqwA#A#B")]
    public async Task LoadsUniqueFirmwareProviderForMissingHleImport(string exportSymbolName)
    {
        var source = Path.Combine(_temporaryDirectory, "source");
        var store = Path.Combine(_temporaryDirectory, "store");
        var modulePath = Path.Combine(source, "system", "common", "lib", "libProvider.sprx");
        Directory.CreateDirectory(Path.GetDirectoryName(modulePath)!);
        await File.WriteAllBytesAsync(modulePath, CreateProviderElf(exportSymbolName));
        var imported = await new FirmwareDirectoryImporter(store).ImportAsync(source);
        var memory = new InMemoryVirtualMemory();
        var modules = new ModuleManager();
        modules.Freeze();
        using var session = new FirmwareLleRuntimeSession(
            store,
            imported.Manifest.ProfileId,
            memory,
            modules,
            Aerolib.Empty);
        var requestedSymbolName = exportSymbolName.Split('#')[0];
        var image = new SelfImage(
            isSelf: false,
            elfHeader: default,
            programHeaders: [],
            mappedRegions: [],
            importStubs: new Dictionary<ulong, string> { [0x4000] = requestedSymbolName });
        var importStubs = new Dictionary<ulong, string>(image.ImportStubs);
        var runtimeSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);

        var summary = await session.LoadMissingProvidersAsync(image, importStubs, runtimeSymbols);

        Assert.Equal(1, summary.MissingImports);
        Assert.Equal(1, summary.CandidateModules);
        Assert.Equal(1, summary.LoadedModules);
        Assert.Equal(1, summary.PublishedImports);
        Assert.Equal(0x0000_6800_0000_0180UL, runtimeSymbols[requestedSymbolName]);
    }

    [Fact]
    public async Task PrefersCanonicalProviderOverCompatibilityVariants()
    {
        const string exportSymbolName = "LwG8g3niqwA#A#B";
        const string providerModuleName = "libSceGnmDriver";
        var source = Path.Combine(_temporaryDirectory, "source-canonical");
        var store = Path.Combine(_temporaryDirectory, "store-canonical");
        var library = Path.Combine(source, "lib");
        Directory.CreateDirectory(library);
        foreach (var fileName in new[]
                 {
                     "libSceGnmDriver.sprx",
                     "libSceGnmDriverCompat1.sprx",
                     "libSceGnmDriverForNeoMode.sprx",
                 })
        {
            await File.WriteAllBytesAsync(
                Path.Combine(library, fileName),
                CreateProviderElf(exportSymbolName, providerModuleName));
        }

        var imported = await new FirmwareDirectoryImporter(store).ImportAsync(source);
        var memory = new InMemoryVirtualMemory();
        var modules = new ModuleManager();
        modules.Freeze();
        using var session = new FirmwareLleRuntimeSession(
            store,
            imported.Manifest.ProfileId,
            memory,
            modules,
            Aerolib.Empty);
        var image = new SelfImage(
            isSelf: false,
            elfHeader: default,
            programHeaders: [],
            mappedRegions: [],
            importStubs: new Dictionary<ulong, string> { [0x4000] = "LwG8g3niqwA" });
        var importStubs = new Dictionary<ulong, string>(image.ImportStubs);
        var runtimeSymbols = new Dictionary<string, ulong>(StringComparer.Ordinal);

        var summary = await session.LoadMissingProvidersAsync(image, importStubs, runtimeSymbols);

        Assert.Equal(3, summary.CandidateModules);
        Assert.Equal(1, summary.LoadedModules);
        Assert.Equal(1, summary.PublishedImports);
        Assert.Equal(0, summary.AmbiguousImports);
        Assert.Equal(0x0000_6800_0000_0180UL, runtimeSymbols["LwG8g3niqwA"]);
    }

    private static byte[] CreateProviderElf(
        string exportSymbolName,
        string providerModuleName = "libProvider")
    {
        var bytes = new byte[0x600];
        bytes[0] = 0x7f;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 62);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 0x1180);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 2);

        var load = bytes.AsSpan(64, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(load, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(load[4..], 5);
        BinaryPrimitives.WriteUInt64LittleEndian(load[16..], 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(load[32..], 0x600);
        BinaryPrimitives.WriteUInt64LittleEndian(load[40..], 0x700);
        BinaryPrimitives.WriteUInt64LittleEndian(load[48..], 0x1000);

        var dynamicHeader = bytes.AsSpan(120, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader[4..], 4);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[8..], 0x200);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[16..], 0x1200);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[32..], 128);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[40..], 128);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[48..], 8);

        WriteDynamic(bytes, 0x200, 5, 0x1400);
        var libraryNameOffset = checked((uint)(exportSymbolName.Length + 2));
        var moduleNameOffset = checked(libraryNameOffset + (uint)providerModuleName.Length + 1);
        var stringBytes = Encoding.ASCII.GetBytes(
            $"\0{exportSymbolName}\0{providerModuleName}\0{providerModuleName}\0");
        WriteDynamic(bytes, 0x210, 10, (ulong)stringBytes.Length);
        WriteDynamic(bytes, 0x220, 6, 0x1480);
        WriteDynamic(bytes, 0x230, 11, 24);
        WriteDynamic(bytes, 0x240, 0x6100003F, 48);
        WriteDynamic(bytes, 0x250, 0x61000043, PackSonyRecord(1, 1, moduleNameOffset));
        WriteDynamic(bytes, 0x260, 0x61000047, PackSonyRecord(0, 1, libraryNameOffset));
        WriteDynamic(bytes, 0x270, 0, 0);
        stringBytes.CopyTo(bytes.AsSpan(0x400));
        var symbol = bytes.AsSpan(0x480 + 24, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(symbol, 1);
        symbol[4] = 0x12;
        BinaryPrimitives.WriteUInt16LittleEndian(symbol[6..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(symbol[8..], 0x1180);
        BinaryPrimitives.WriteUInt64LittleEndian(symbol[16..], 16);
        return bytes;
    }

    private static void WriteDynamic(byte[] bytes, int offset, long tag, ulong value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), tag);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8), value);
    }

    private static ulong PackSonyRecord(ushort id, ushort version, uint nameOffset) =>
        ((ulong)id << 48) | ((ulong)version << 32) | nameOffset;

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

    private sealed class InMemoryVirtualMemory :
        IVirtualMemory,
        IGuestAddressSpace,
        IReleasableVirtualMemory
    {
        private readonly SortedDictionary<ulong, Region> _regions = [];

        public void Clear() => _regions.Clear();

        public void Map(
            ulong virtualAddress,
            ulong memorySize,
            ulong fileOffset,
            ReadOnlySpan<byte> fileData,
            ProgramHeaderFlags protection)
        {
            var data = new byte[checked((int)memorySize)];
            fileData.CopyTo(data);
            _regions.Add(virtualAddress, new Region(data, protection));
        }

        public IReadOnlyList<VirtualMemoryRegion> SnapshotRegions() => _regions
            .Select(item => new VirtualMemoryRegion(
                item.Key,
                (ulong)item.Value.Data.Length,
                0,
                (ulong)item.Value.Data.Length,
                item.Value.Protection))
            .ToArray();

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!Find(virtualAddress, destination.Length, out var region, out var offset))
            {
                return false;
            }
            region.Data.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!Find(virtualAddress, source.Length, out var region, out var offset))
            {
                return false;
            }
            source.CopyTo(region.Data.AsSpan(offset, source.Length));
            return true;
        }

        public ulong AllocateAt(ulong desiredAddress, ulong size, bool executable = true, bool allowAlternative = true)
        {
            if (size > int.MaxValue || _regions.Any(item =>
                    desiredAddress < item.Key + (ulong)item.Value.Data.Length &&
                    item.Key < desiredAddress + size))
            {
                throw new InvalidOperationException();
            }
            _regions.Add(
                desiredAddress,
                new Region(
                    new byte[(int)size],
                    ProgramHeaderFlags.Read | ProgramHeaderFlags.Write |
                    (executable ? ProgramHeaderFlags.Execute : ProgramHeaderFlags.None)));
            return desiredAddress;
        }

        public bool TryBackFixedRange(ulong address, ulong size, bool executable) => false;

        public bool TryAllocateAtOrAbove(
            ulong desiredAddress,
            ulong size,
            bool executable,
            ulong alignment,
            out ulong actualAddress)
        {
            actualAddress = (desiredAddress + alignment - 1) & ~(alignment - 1);
            try
            {
                _ = AllocateAt(actualAddress, size, executable, allowAlternative: false);
                return true;
            }
            catch (InvalidOperationException)
            {
                actualAddress = 0;
                return false;
            }
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) =>
            size <= int.MaxValue && Find(address, (int)size, out _, out _);

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) =>
            TryAllocateAtOrAbove(0x7000_0000, size, executable: false, alignment, out address);

        public bool TryFreeGuestMemory(ulong address) => TryReleaseMapping(address);

        public bool TryReleaseMapping(ulong virtualAddress) => _regions.Remove(virtualAddress);

        private bool Find(ulong address, int length, out Region region, out int offset)
        {
            foreach (var item in _regions)
            {
                if (address >= item.Key &&
                    address - item.Key <= (ulong)item.Value.Data.Length &&
                    (ulong)length <= (ulong)item.Value.Data.Length - (address - item.Key))
                {
                    region = item.Value;
                    offset = checked((int)(address - item.Key));
                    return true;
                }
            }
            region = null!;
            offset = 0;
            return false;
        }

        private sealed record Region(byte[] Data, ProgramHeaderFlags Protection);
    }
}
