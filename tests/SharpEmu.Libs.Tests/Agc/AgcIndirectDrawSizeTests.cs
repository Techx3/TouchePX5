// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcIndirectDrawSizeTests
{
    [Fact]
    public void DcbDrawIndexIndirectMultiGetSize_ReturnsEightDwords()
    {
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

        var result = AgcExports.DcbDrawIndexIndirectMultiGetSize(ctx);

        Assert.Equal(32, result);
        Assert.Equal(32UL, ctx[CpuRegister.Rax]);
    }
}
