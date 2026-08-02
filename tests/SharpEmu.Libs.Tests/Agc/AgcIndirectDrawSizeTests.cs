// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcIndirectDrawSizeTests
{
    private const ulong BaseAddress = 0x1_0000_0000;

    [Fact]
    public void DcbDrawIndexIndirectMultiGetSize_ReturnsEightDwords()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), Generation.Gen5);

        var result = AgcExports.DcbDrawIndexIndirectMultiGetSize(ctx);

        Assert.Equal(32, result);
        Assert.Equal(32UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SubmittedIndirectDraw_PreservesDecodedInstanceCount()
    {
        const ulong commandBuffer = BaseAddress + 0x100;
        const ulong commands = BaseAddress + 0x400;
        const ulong indirectArguments = BaseAddress + 0x800;
        const ulong submitPacket = BaseAddress + 0x900;
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        WriteUInt64(memory, commandBuffer + 0x10, commands);
        WriteUInt64(memory, commandBuffer + 0x18, commands + 0x100);

        ctx[CpuRegister.Rdi] = commandBuffer;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = indirectArguments;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbSetBaseIndirectArgs(ctx));

        ctx[CpuRegister.Rdi] = commandBuffer;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DcbDrawIndirect(ctx));

        WriteUInt32(memory, indirectArguments, 3);
        WriteUInt32(memory, indirectArguments + 4, 7);
        WriteUInt32(memory, indirectArguments + 8, 0);
        WriteUInt32(memory, indirectArguments + 12, 0);
        WriteUInt64(memory, submitPacket, commands);
        WriteUInt32(memory, submitPacket + 8, 9);

        ctx[CpuRegister.Rdi] = submitPacket;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.DriverSubmitDcb(ctx));
        Assert.Equal(7u, AgcExports.GetSubmittedGraphicsInstanceCountForDiagnostics(ctx));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
