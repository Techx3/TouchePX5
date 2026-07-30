// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5PixelInputMappingTests
{
    [Theory]
    [InlineData(0u, 0u, 0u)]
    [InlineData(0u, 3u, 0u)]
    [InlineData(1u, 0u, 0u)]
    [InlineData(1u, 3u, 0x3F800000u)]
    [InlineData(2u, 0u, 0x3F800000u)]
    [InlineData(2u, 3u, 0u)]
    [InlineData(3u, 0u, 0x3F800000u)]
    [InlineData(3u, 3u, 0x3F800000u)]
    public void DefaultSelectorProducesExpectedComponent(
        uint selector,
        uint component,
        uint expected)
    {
        var control = 0x20u | (selector << 8);
        Assert.True(Gen5PixelInputMapping.UsesDefaultValue(control));
        Assert.Equal(expected, Gen5PixelInputMapping.GetDefaultComponentBits(control, component));
    }

    [Fact]
    public void ParameterLocationUsesLowFiveBits()
    {
        Assert.Equal(17u, Gen5PixelInputMapping.GetParameterLocation(0xC31u));
    }
}
