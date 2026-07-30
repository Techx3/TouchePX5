// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using Touche.PS5.Modules;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class CoreLleGuestLinkTransactionFactoryTests
{
    private const string ModulePath = "/system/common/lib/libExample.sprx";

    [Fact]
    public async Task MapsLleImageAndMergesSharedPagePermissions()
    {
        var memory = new InMemoryVirtualMemory();
        using var factory = new CoreLleGuestMemoryTransactionFactory(memory);
        await using (var transaction = await factory.BeginAsync(ModulePath, 0x8000, 0x3000))
        {
            await transaction.StageSegmentAsync(
                0x8000,
                0x1800,
                0,
                new byte[] { 1, 2, 3, 4 },
                LleSegmentPermissions.Read | LleSegmentPermissions.Execute);
            await transaction.StageSegmentAsync(
                0x9800,
                0x800,
                4,
                new byte[] { 5, 6 },
                LleSegmentPermissions.Read | LleSegmentPermissions.Write);
            await transaction.CommitAsync();
        }

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, memory.Read(0x8000, 4));
        Assert.Equal(new byte[4], memory.Read(0x8010, 4));
        Assert.Equal(new byte[] { 5, 6 }, memory.Read(0x9800, 2));
        Assert.Equal(
            GuestPageProtection.Read | GuestPageProtection.Execute,
            memory.Protections[0x8000]);
        Assert.Equal(
            GuestPageProtection.Read | GuestPageProtection.Write | GuestPageProtection.Execute,
            memory.Protections[0x9000]);
        Assert.True(factory.TryReleaseModule(ModulePath, 0x8000));
        Assert.Empty(memory.SnapshotRegions());
    }

    [Fact]
    public async Task RollsBackLleImageWhenSegmentWriteFails()
    {
        var memory = new InMemoryVirtualMemory();
        memory.FailWritesAt.Add(0x8000);
        using var factory = new CoreLleGuestMemoryTransactionFactory(memory);

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var transaction = await factory.BeginAsync(ModulePath, 0x8000, 0x1000);
            await transaction.StageSegmentAsync(
                0x8000,
                0x1000,
                0,
                new byte[] { 1 },
                LleSegmentPermissions.Read);
        });

        Assert.Empty(memory.SnapshotRegions());
        Assert.False(factory.TryReleaseModule(ModulePath, 0x8000));
    }

    [Fact]
    public async Task RollsBackLleImageWhenFinalProtectionFails()
    {
        var memory = new InMemoryVirtualMemory { FailProtectionCall = 2 };
        using var factory = new CoreLleGuestMemoryTransactionFactory(memory);

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var transaction = await factory.BeginAsync(ModulePath, 0x8000, 0x1000);
            await transaction.StageSegmentAsync(
                0x8000,
                0x1000,
                0,
                new byte[] { 1 },
                LleSegmentPermissions.Read | LleSegmentPermissions.Execute);
            await transaction.CommitAsync();
        });

        Assert.Empty(memory.SnapshotRegions());
    }

    [Fact]
    public async Task CommitsRelocationAndOwnsExecutableThunkUntilModuleRelease()
    {
        var memory = CreateMemory();
        using var factory = new CoreLleGuestLinkTransactionFactory(memory);
        ulong thunkAddress;
        await using (var transaction = await factory.BeginAsync(ModulePath, 0x8000, 0x1000))
        {
            thunkAddress = await transaction.StageHleThunkAsync("exampleNid", controlledStub: false);
            await transaction.StageWriteAsync(0x8100, new byte[] { 1, 2, 3, 4 });
            await transaction.CommitAsync();
        }

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, memory.Read(0x8100, 4));
        var stub = memory.Read(thunkAddress, 16);
        Assert.Equal(0xcc, stub[0]);
        Assert.Equal(0xc3, stub[1]);
        Assert.Equal(0, stub[2]);
        Assert.Equal(
            GuestPageProtection.Read | GuestPageProtection.Execute,
            memory.Protections[thunkAddress]);
        Assert.True(factory.TryReleaseModule(ModulePath, 0x8000));
        Assert.DoesNotContain(memory.SnapshotRegions(), region => region.VirtualAddress == thunkAddress);
    }

    [Fact]
    public async Task ReusesThunkForSameDispatchKeyWithinTransaction()
    {
        var memory = CreateMemory();
        using var factory = new CoreLleGuestLinkTransactionFactory(memory);
        await using var transaction = await factory.BeginAsync(ModulePath, 0x8000, 0x1000);

        var first = await transaction.StageHleThunkAsync("exampleNid", controlledStub: true);
        var second = await transaction.StageHleThunkAsync("exampleNid", controlledStub: true);
        await transaction.CommitAsync();

        Assert.Equal(first, second);
        Assert.Equal(1, memory.Read(first, 16)[2]);
    }

    [Fact]
    public async Task RestoresAppliedWritesAndReleasesThunksWhenCommitFails()
    {
        var memory = CreateMemory();
        memory.FailWritesAt.Add(0x8108);
        using var factory = new CoreLleGuestLinkTransactionFactory(memory);
        ulong thunkAddress = 0;

        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var transaction = await factory.BeginAsync(ModulePath, 0x8000, 0x1000);
            thunkAddress = await transaction.StageHleThunkAsync("exampleNid", controlledStub: false);
            await transaction.StageWriteAsync(0x8100, new byte[] { 1, 2, 3, 4 });
            await transaction.StageWriteAsync(0x8108, new byte[] { 5, 6, 7, 8 });
            await transaction.CommitAsync();
        });

        Assert.Equal(new byte[4], memory.Read(0x8100, 4));
        Assert.DoesNotContain(memory.SnapshotRegions(), region => region.VirtualAddress == thunkAddress);
        Assert.False(factory.TryReleaseModule(ModulePath, 0x8000));
    }

    [Fact]
    public async Task RejectsSecondCommittedTransactionUntilModuleIsReleased()
    {
        var memory = CreateMemory();
        using var factory = new CoreLleGuestLinkTransactionFactory(memory);
        await using (var first = await factory.BeginAsync(ModulePath, 0x8000, 0x1000))
        {
            await first.CommitAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await factory.BeginAsync(ModulePath, 0x8000, 0x1000));
        Assert.True(factory.TryReleaseModule(ModulePath, 0x8000));
        await using var second = await factory.BeginAsync(ModulePath, 0x8000, 0x1000);
        await second.CommitAsync();
    }

    private static InMemoryVirtualMemory CreateMemory()
    {
        var memory = new InMemoryVirtualMemory();
        memory.Map(
            0x8000,
            0x1000,
            0,
            new byte[0x1000],
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        return memory;
    }

    private sealed class InMemoryVirtualMemory :
        IVirtualMemory,
        IGuestAddressSpace,
        IReleasableVirtualMemory
    {
        private readonly SortedDictionary<ulong, Region> _regions = [];
        private ulong _nextAllocation = 0x0000_6f00_0000_0000;

        public HashSet<ulong> FailWritesAt { get; } = [];

        public int FailProtectionCall { get; init; }

        public int ProtectionCallCount { get; private set; }

        public Dictionary<ulong, GuestPageProtection> Protections { get; } = [];

        public void Clear()
        {
            _regions.Clear();
            Protections.Clear();
        }

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
            if (!TryFind(virtualAddress, destination.Length, out var region, out var offset))
            {
                return false;
            }
            region.Data.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (FailWritesAt.Contains(virtualAddress) ||
                !TryFind(virtualAddress, source.Length, out var region, out var offset))
            {
                return false;
            }
            source.CopyTo(region.Data.AsSpan(offset, source.Length));
            return true;
        }

        public ulong AllocateAt(
            ulong desiredAddress,
            ulong size,
            bool executable = true,
            bool allowAlternative = true)
        {
            if (!allowAlternative)
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
                        executable
                            ? ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute
                            : ProgramHeaderFlags.Read | ProgramHeaderFlags.Write));
                return desiredAddress;
            }
            if (!TryAllocateAtOrAbove(desiredAddress, size, executable, 0x1000, out var address))
            {
                throw new OutOfMemoryException();
            }
            return address;
        }

        public bool TryBackFixedRange(ulong address, ulong size, bool executable) => false;

        public bool TryAllocateAtOrAbove(
            ulong desiredAddress,
            ulong size,
            bool executable,
            ulong alignment,
            out ulong actualAddress)
        {
            actualAddress = Math.Max(desiredAddress, _nextAllocation);
            var mask = alignment - 1;
            actualAddress = (actualAddress + mask) & ~mask;
            if (size > int.MaxValue || _regions.ContainsKey(actualAddress))
            {
                actualAddress = 0;
                return false;
            }
            _regions.Add(
                actualAddress,
                new Region(
                    new byte[(int)size],
                    executable
                        ? ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute
                        : ProgramHeaderFlags.Read | ProgramHeaderFlags.Write));
            _nextAllocation = actualAddress + size;
            return true;
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection)
        {
            ProtectionCallCount++;
            if ((FailProtectionCall != 0 && ProtectionCallCount == FailProtectionCall) ||
                size == 0 ||
                size > int.MaxValue ||
                !TryFind(address, (int)size, out _, out _))
            {
                return false;
            }
            var start = address & ~0xfffUL;
            var end = (address + size + 0xfffUL) & ~0xfffUL;
            for (var page = start; page < end; page += 0x1000)
            {
                Protections[page] = protection;
            }
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) =>
            TryAllocateAtOrAbove(_nextAllocation, size, executable: false, alignment, out address);

        public bool TryFreeGuestMemory(ulong address) => TryReleaseMapping(address);

        public bool TryReleaseMapping(ulong virtualAddress)
        {
            Protections.Remove(virtualAddress);
            return _regions.Remove(virtualAddress);
        }

        public byte[] Read(ulong address, int length)
        {
            var data = new byte[length];
            Assert.True(TryRead(address, data));
            return data;
        }

        private bool TryFind(
            ulong address,
            int length,
            out Region region,
            out int offset)
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
