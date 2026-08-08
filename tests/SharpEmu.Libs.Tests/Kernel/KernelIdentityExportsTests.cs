// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelIdentityExportsTests
{
    private const ulong MemoryBase = 0x7100_0000_0000UL;

    [Fact]
    public void KernelIsTrinityModeReportsFalse()
    {
        var context = new CpuContext(
            new FakeCpuMemory(MemoryBase, 0x100),
            Generation.Gen5);

        var result = KernelExports.KernelIsTrinityMode(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void KernelGetOpenPsIdWritesDeterministicZeroId()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x100);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = MemoryBase + 0x20;

        var result = KernelExports.KernelGetOpenPsId(context);
        Span<byte> openPsId = stackalloc byte[16];

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.True(memory.TryRead(MemoryBase + 0x20, openPsId));
        Assert.True(openPsId.SequenceEqual(new byte[16]));
    }

    [Fact]
    public void KernelGetOpenPsIdRejectsNullOutput()
    {
        var context = new CpuContext(
            new FakeCpuMemory(MemoryBase, 0x100),
            Generation.Gen5);

        var result = KernelExports.KernelGetOpenPsId(context);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }
}
