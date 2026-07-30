// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanMrtCompatibilityTests
{
    [Fact]
    public void CommonExtent_UsesSmallestAttachmentDimensions()
    {
        GuestRenderTarget[] targets =
        [
            new(0x1000, 1920, 1080, 10, 0),
            new(0x2000, 960, 1080, 10, 0),
            new(0x3000, 1920, 540, 10, 0),
        ];

        var extent = VulkanVideoPresenter.ResolveCommonMrtExtent(targets);

        Assert.Equal(960u, extent.Width);
        Assert.Equal(540u, extent.Height);
    }

    [Fact]
    public void CommonExtent_HandlesEmptyAttachmentSet()
    {
        var extent = VulkanVideoPresenter.ResolveCommonMrtExtent([]);

        Assert.Equal(0u, extent.Width);
        Assert.Equal(0u, extent.Height);
    }

    [Fact]
    public void AliasDetection_RejectsOnlyRepeatedAddresses()
    {
        GuestRenderTarget[] uniqueTargets =
        [
            new(0x1000, 1920, 1080, 10, 0),
            new(0x2000, 960, 540, 10, 0),
        ];
        GuestRenderTarget[] aliasedTargets =
        [
            new(0x1000, 1920, 1080, 10, 0),
            new(0x1000, 960, 540, 10, 0),
        ];

        Assert.False(VulkanVideoPresenter.RenderTargetsAliased(uniqueTargets));
        Assert.True(VulkanVideoPresenter.RenderTargetsAliased(aliasedTargets));
    }

    [Fact]
    public void AliasDetection_AllowsDistinctArraySlicesAtSameAddress()
    {
        GuestRenderTarget[] layeredTargets =
        [
            new(0x1000, 1920, 1080, 10, 0, BaseArrayLayer: 0),
            new(0x1000, 1920, 1080, 10, 0, BaseArrayLayer: 1),
            new(0x1000, 1920, 1080, 10, 0, BaseArrayLayer: 2),
            new(0x1000, 1920, 1080, 10, 0, BaseArrayLayer: 3),
        ];

        Assert.False(VulkanVideoPresenter.RenderTargetsAliased(layeredTargets));
    }

    [Fact]
    public void AliasDetection_RejectsOverlappingArraySliceRanges()
    {
        GuestRenderTarget[] layeredTargets =
        [
            new(0x1000, 1920, 1080, 10, 0, BaseArrayLayer: 1, LayerCount: 2),
            new(0x1000, 1920, 1080, 10, 0, BaseArrayLayer: 2, LayerCount: 1),
        ];

        Assert.True(VulkanVideoPresenter.RenderTargetsAliased(layeredTargets));
    }
}
