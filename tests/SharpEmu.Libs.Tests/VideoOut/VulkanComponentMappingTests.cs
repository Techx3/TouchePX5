// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanComponentMappingTests
{
    [Fact]
    public void IdentityDstSelectMapsRgbaWithoutReordering()
    {
        var mapping = VulkanVideoPresenter.GetGuestComponentMapping(0xFAC);

        Assert.Equal(ComponentSwizzle.R, mapping.R);
        Assert.Equal(ComponentSwizzle.G, mapping.G);
        Assert.Equal(ComponentSwizzle.B, mapping.B);
        Assert.Equal(ComponentSwizzle.A, mapping.A);
    }

    [Fact]
    public void AlternateDstSelectMapsBgraBackToGuestRgba()
    {
        var mapping = VulkanVideoPresenter.GetGuestComponentMapping(0xF2E);

        Assert.Equal(ComponentSwizzle.B, mapping.R);
        Assert.Equal(ComponentSwizzle.G, mapping.G);
        Assert.Equal(ComponentSwizzle.R, mapping.B);
        Assert.Equal(ComponentSwizzle.A, mapping.A);
    }

    [Fact]
    public void ConstantDstSelectChannelsRemainConstants()
    {
        // X=0, Y=1, Z=R, W=A.
        var mapping = VulkanVideoPresenter.GetGuestComponentMapping(
            0u | (1u << 3) | (4u << 6) | (7u << 9));

        Assert.Equal(ComponentSwizzle.Zero, mapping.R);
        Assert.Equal(ComponentSwizzle.One, mapping.G);
        Assert.Equal(ComponentSwizzle.R, mapping.B);
        Assert.Equal(ComponentSwizzle.A, mapping.A);
    }
}
