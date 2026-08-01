// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Ime;

public sealed class ImeExportsTests
{
    private const ulong Base = 0x1_0000_0000;

    [Fact]
    public void KeyboardGetInfo_WritesDisconnectedKeyboardRecord()
    {
        const ulong informationAddress = Base + 0x100;
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = informationAddress;

        Assert.Equal(0, ImeExports.ImeKeyboardGetInfo(context));

        var information = new byte[0x24];
        Assert.True(memory.TryRead(informationAddress, information));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(information[0x00..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(information[0x04..]));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(information[0x08..]));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(information[0x0C..]));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(information[0x10..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(information[0x14..]));
    }
}
