// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDirectRegisterExportsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;

    [Fact]
    public void DcbSetShRegisterDirect_EmitsSetShRegPacket()
    {
        const ulong commandBuffer = BaseAddress + 0x100;
        const ulong commands = BaseAddress + 0x400;
        const uint registerOffset = 0x12345;
        const uint registerValue = 0x89ABCDEF;
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        WriteUInt64(memory, commandBuffer + 0x10, commands);
        WriteUInt64(memory, commandBuffer + 0x18, commands + 0x100);
        ctx[CpuRegister.Rdi] = commandBuffer;
        ctx[CpuRegister.Rsi] = ((ulong)registerValue << 32) | registerOffset;

        var result = AgcExports.DcbSetShRegisterDirect(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(commands, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC0017600u, ReadUInt32(memory, commands));
        Assert.Equal(registerOffset & 0xFFFFu, ReadUInt32(memory, commands + 4));
        Assert.Equal(registerValue, ReadUInt32(memory, commands + 8));
        Assert.Equal(commands + 12, ReadUInt64(memory, commandBuffer + 0x10));
    }

    [Fact]
    public void DcbSetShRegisterDirectGetSize_ReturnsThreeDwords()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), Generation.Gen5);

        var result = AgcExports.DcbSetShRegisterDirectGetSize(ctx);

        Assert.Equal(12, result);
        Assert.Equal(12UL, ctx[CpuRegister.Rax]);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
