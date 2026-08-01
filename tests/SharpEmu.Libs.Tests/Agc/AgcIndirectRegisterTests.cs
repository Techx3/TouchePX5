// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcIndirectRegisterTests
{
    [Theory]
    [InlineData(0x00000191u, 0x00000191u)]
    [InlineData(0x10000000u, 0x00000191u)]
    [InlineData(0x10000002u, 0x00000193u)]
    [InlineData(0x1000001Fu, 0x000001B0u)]
    [InlineData(0x00000200u, 0x00000200u)]
    public void DecodeIndirectCxRegisterOffsetMapsPixelInputSelector(
        uint encoded,
        uint expected)
    {
        Assert.Equal(expected, AgcExports.DecodeIndirectCxRegisterOffset(encoded));
    }

    [Theory]
    [InlineData(0x00000123u, 0x00000123u)]
    [InlineData(0x20000123u, 0x00000123u)]
    [InlineData(0x70000123u, 0x00000123u)]
    public void NormalizeIndirectRegisterOffsetRemovesSelectorBits(
        uint encoded,
        uint expected)
    {
        Assert.Equal(expected, AgcExports.NormalizeIndirectRegisterOffset(encoded));
    }
}
