// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    [Theory]
    [InlineData(Generation.Gen5, false, 0, true)]
    [InlineData(Generation.Gen5, false, 1, true)]
    [InlineData(Generation.Gen5, false, 2, true)]
    [InlineData(Generation.Gen4, false, 0, true)]
    [InlineData(Generation.Gen4, false, 1, false)]
    [InlineData(Generation.Gen4, false, 2, false)]
    [InlineData(Generation.Gen4, true, 2, true)]
    [InlineData(Generation.Gen5, true, 3, false)]
    public void PortTypeAcceptance_MatchesGeneration(
        Generation generation,
        bool extended,
        int type,
        bool expected)
    {
        Assert.Equal(expected, PadExports.IsPortTypeAccepted(generation, extended, type));
    }
}
