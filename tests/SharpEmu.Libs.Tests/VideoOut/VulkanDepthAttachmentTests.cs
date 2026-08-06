// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanDepthAttachmentTests
{
    private static readonly GuestDepthTarget Target = new(
        ReadAddress: 0x1000,
        WriteAddress: 0x1000,
        Width: 1920,
        Height: 1080,
        GuestFormat: 1,
        SwizzleMode: 0,
        ClearDepth: 1f,
        ReadOnly: false);

    [Fact]
    public void GuestDepthTarget_AttachesForDepthWork()
    {
        var state = new GuestDepthState(
            TestEnable: true,
            WriteEnable: true,
            CompareOp: 3);

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void GuestDepthTarget_AttachesForEitherDepthOperation(
        bool testEnable,
        bool writeEnable)
    {
        var state = new GuestDepthState(testEnable, writeEnable, CompareOp: 3);

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
    }

    [Fact]
    public void GuestDepthTarget_RequiresTargetAndDepthWork()
    {
        var state = GuestDepthState.Default;

        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
        Assert.False(VulkanVideoPresenter.ShouldAttachGuestDepth(
            target: null,
            new GuestDepthState(true, false, CompareOp: 3)));
    }

    [Fact]
    public void GuestDepthTarget_AttachesForDepthClear()
    {
        var state = new GuestDepthState(
            TestEnable: false,
            WriteEnable: false,
            CompareOp: 7,
            ClearEnable: true);

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
    }

    [Theory]
    [InlineData(0x41u, true)]
    [InlineData(0x40u, false)]
    public void DepthState_DecodesRenderControlClearBit(
        uint renderControl,
        bool clearEnable)
    {
        var registers = new Dictionary<uint, uint>
        {
            [0x000] = renderControl,
            [0x200] = 0x776,
        };

        var state = AgcExports.DecodeDepthState(registers);

        Assert.True(state.TestEnable);
        Assert.True(state.WriteEnable);
        Assert.Equal(7u, state.CompareOp);
        Assert.Equal(clearEnable, state.ClearEnable);
    }

    [Fact]
    public void DepthState_DecodesFrontAndBackStencilState()
    {
        var registers = new Dictionary<uint, uint>
        {
            // stencil enable, back-face enable, front LESS, back GREATER
            [0x200] = 0x0050_0281,
            // front: fail ZERO, pass REPLACE, depth-fail INVERT
            // back: fail INCREMENT_CLAMP, pass DECREMENT_CLAMP, depth-fail INCREMENT_WRAP
            [0x10B] = 0x0086_5741,
            // reference, compare mask, write mask
            [0x10C] = 0x0033_55AA,
            [0x10D] = 0x00CC_77BB,
            [0x000] = 0x2,
        };

        var state = AgcExports.DecodeDepthState(registers);

        Assert.True(state.StencilTestEnable);
        Assert.True(state.StencilClearEnable);
        Assert.Equal(2u, state.FrontStencil.CompareOp);
        Assert.Equal(1u, state.FrontStencil.FailOp);
        Assert.Equal(4u, state.FrontStencil.PassOp);
        Assert.Equal(7u, state.FrontStencil.DepthFailOp);
        Assert.Equal(0xAAu, state.FrontStencil.Reference);
        Assert.Equal(0x55u, state.FrontStencil.CompareMask);
        Assert.Equal(0x33u, state.FrontStencil.WriteMask);
        Assert.Equal(5u, state.BackStencil.CompareOp);
        Assert.Equal(5u, state.BackStencil.FailOp);
        Assert.Equal(6u, state.BackStencil.PassOp);
        Assert.Equal(8u, state.BackStencil.DepthFailOp);
        Assert.Equal(0xBBu, state.BackStencil.Reference);
        Assert.Equal(0x77u, state.BackStencil.CompareMask);
        Assert.Equal(0xCCu, state.BackStencil.WriteMask);
    }

    [Fact]
    public void GuestDepthTarget_AttachesForStencilWork()
    {
        var state = new GuestDepthState(
            TestEnable: false,
            WriteEnable: false,
            CompareOp: 7,
            StencilTestEnable: true,
            FrontStencil: GuestStencilFaceState.Default,
            BackStencil: GuestStencilFaceState.Default);

        Assert.True(VulkanVideoPresenter.ShouldAttachGuestDepth(Target, state));
    }

    [Fact]
    public void StaleColorAlias_DropsDepthOnlyForAConfirmedStaleSurface()
    {
        Assert.True(VulkanVideoPresenter.ShouldDropStaleAliasedDepth(
            selfAlias: false,
            writtenAsColorThisFrame: false,
            writtenAsColorPreviousFrame: false,
            hasReciprocalCrossedPair: false,
            explicitlyInitializedAsDepth: false,
            hasInitializedColorImage: true,
            hasColorRenderPass: true,
            extentMatches: true));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void CrossedLiveColorAlias_DropsBogusDepth(
        bool writtenThisFrame,
        bool writtenPreviousFrame,
        bool explicitlyInitializedAsDepth)
    {
        Assert.True(VulkanVideoPresenter.ShouldDropStaleAliasedDepth(
            selfAlias: false,
            writtenAsColorThisFrame: writtenThisFrame,
            writtenAsColorPreviousFrame: writtenPreviousFrame,
            hasReciprocalCrossedPair: true,
            explicitlyInitializedAsDepth,
            hasInitializedColorImage: true,
            hasColorRenderPass: true,
            extentMatches: true));
    }

    [Fact]
    public void CrossedLiveColorAlias_DoesNotDependOnVulkanImageCacheState()
    {
        Assert.True(VulkanVideoPresenter.ShouldDropStaleAliasedDepth(
            selfAlias: false,
            writtenAsColorThisFrame: false,
            writtenAsColorPreviousFrame: false,
            hasReciprocalCrossedPair: true,
            explicitlyInitializedAsDepth: true,
            hasInitializedColorImage: false,
            hasColorRenderPass: false,
            extentMatches: false));
    }

    [Theory]
    [InlineData(true, false, false, false, false, true, true, true)]
    [InlineData(false, true, false, false, false, true, true, true)]
    [InlineData(true, false, false, true, false, true, true, true)]
    [InlineData(false, false, false, false, true, true, true, true)]
    [InlineData(false, false, false, false, false, false, true, true)]
    [InlineData(false, false, false, false, false, true, false, true)]
    [InlineData(false, false, false, false, false, true, true, false)]
    public void StaleColorAlias_PreservesLegitimateDepth(
        bool selfAlias,
        bool writtenThisFrame,
        bool writtenPreviousFrame,
        bool hasReciprocalCrossedPair,
        bool explicitlyInitializedAsDepth,
        bool hasInitializedColor,
        bool hasRenderPass,
        bool extentMatches)
    {
        Assert.False(VulkanVideoPresenter.ShouldDropStaleAliasedDepth(
            selfAlias,
            writtenThisFrame,
            writtenPreviousFrame,
            hasReciprocalCrossedPair,
            explicitlyInitializedAsDepth,
            hasInitializedColor,
            hasRenderPass,
            extentMatches));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    public void ColorWrite_InvalidatesOnlyMatchingInactiveDepthBacking(
        bool aliasesDepthAddress,
        bool selfAliasesActiveDepth,
        bool extentMatches,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.ShouldInvalidateDepthBackingAfterColorWrite(
                aliasesDepthAddress,
                selfAliasesActiveDepth,
                extentMatches));
    }

    [Theory]
    [InlineData(100L, 100L, 8192L, true)]
    [InlineData(8292L, 100L, 8192L, true)]
    [InlineData(8293L, 100L, 8192L, false)]
    [InlineData(99L, 100L, 8192L, false)]
    [InlineData(100L, -1L, 8192L, false)]
    public void CrossedColorDepthPair_UsesBoundedWorkSequenceHistory(
        long currentSequence,
        long observedSequence,
        long maximumAge,
        bool expected)
    {
        Assert.Equal(
            expected,
            VulkanVideoPresenter.IsRecentCrossedColorDepthPair(
                currentSequence,
                observedSequence,
                maximumAge));
    }
}
