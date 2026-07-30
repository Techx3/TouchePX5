// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcColorViewTests
{
    [Fact]
    public void DecodeColorRenderTargetView_ExtractsSliceRangeAndMip()
    {
        const uint sliceStart = 3;
        const uint sliceMax = 6;
        const uint mipLevel = 5;
        var view = sliceStart | (sliceMax << 13) | (mipLevel << 26);

        var decoded = AgcExports.DecodeColorRenderTargetView(view);

        Assert.Equal(sliceStart, decoded.BaseArrayLayer);
        Assert.Equal(4u, decoded.LayerCount);
        Assert.Equal(mipLevel, decoded.MipLevel);
    }

    [Fact]
    public void DecodeColorRenderTargetView_DefaultsToFirstSliceAndMip()
    {
        var decoded = AgcExports.DecodeColorRenderTargetView(0);

        Assert.Equal(0u, decoded.BaseArrayLayer);
        Assert.Equal(1u, decoded.LayerCount);
        Assert.Equal(0u, decoded.MipLevel);
    }
}
