// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcTextureTransportTests
{
    [Fact]
    public void SingleChannelAltReverse_RoutesAlphaIntoTheStoredChannel()
    {
        Assert.Equal(
            0xE7,
            AgcExports.GetSingleChannelRenderTargetComponentMapping(
                format: 1,
                compSwap: 3,
                enabled: true));
    }

    [Theory]
    [InlineData(2u, 3u, true)]
    [InlineData(1u, 1u, true)]
    [InlineData(1u, 3u, false)]
    public void SingleChannelCompSwap_PreservesIdentityOutsideMeasuredCase(
        uint format,
        uint compSwap,
        bool enabled)
    {
        Assert.Equal(
            0xE4,
            AgcExports.GetSingleChannelRenderTargetComponentMapping(
                format,
                compSwap,
                enabled));
    }

    [Theory]
    [InlineData(10u, 4u, 4u)]
    [InlineData(10u, 0u, 1u)]
    [InlineData(9u, 4u, 1u)]
    [InlineData(13u, 4u, 1u)]
    public void GetTextureVolumeDepth_OnlyUsesDescriptorDepthFor3D(
        uint type,
        uint descriptorDepth,
        uint expectedDepth)
    {
        Assert.Equal(
            expectedDepth,
            AgcExports.GetTextureVolumeDepth(type, descriptorDepth));
    }

    [Fact]
    public void GetTextureByteCount_MultipliesUncompressedVolumeDepth()
    {
        Assert.Equal(
            4UL * 8 * 6 * 5,
            AgcExports.GetTextureByteCount(
                format: 10,
                width: 8,
                height: 6,
                depth: 5));
    }

    [Fact]
    public void GetTextureByteCount_MultipliesBlockCompressedVolumeDepth()
    {
        // Format 169 uses one eight-byte BC block for each 4x4 texel block.
        Assert.Equal(
            2UL * 2 * 8 * 3,
            AgcExports.GetTextureByteCount(
                format: 169,
                width: 7,
                height: 5,
                depth: 3));
    }

    [Fact]
    public void GetTextureByteCount_LeavesTwoDimensionalSizingUnchanged()
    {
        Assert.Equal(
            AgcExports.GetTextureByteCount(10, 8, 6),
            AgcExports.GetTextureByteCount(10, 8, 6, depth: 1));
        Assert.Equal(
            AgcExports.GetTextureByteCount(10, 8, 6),
            AgcExports.GetTextureByteCount(10, 8, 6, depth: 0));
    }

    [Fact]
    public void GuestDrawTexture_CarriesRawTypeAndNormalizedDepth()
    {
        var texture = new GuestDrawTexture(
            Address: 0x1234,
            Width: 8,
            Height: 6,
            Format: 10,
            NumberType: 0,
            RgbaPixels: [],
            IsFallback: false,
            IsStorage: false,
            Type: 10,
            Depth: 5);

        Assert.Equal(10u, texture.Type);
        Assert.Equal(5u, texture.Depth);
    }

    [Fact]
    public void TextureContentIdentity_DistinguishesTypeAndDepth()
    {
        var twoDimensional = CreateIdentity(type: 9, depth: 1);
        var threeDimensional = CreateIdentity(type: 10, depth: 1);
        var deeperThreeDimensional = CreateIdentity(type: 10, depth: 5);

        Assert.NotEqual(twoDimensional, threeDimensional);
        Assert.NotEqual(threeDimensional, deeperThreeDimensional);
    }

    [Fact]
    public void PendingTextureUpload_IsVisibleUntilTheQueuedPayloadCompletes()
    {
        var texture = new GuestDrawTexture(
            Address: 0x1234,
            Width: 8,
            Height: 6,
            Format: 10,
            NumberType: 0,
            RgbaPixels: new byte[8 * 6 * 4],
            IsFallback: false,
            IsStorage: false,
            Pitch: 8);
        var identity = CreateIdentity(type: 9, depth: 1);

        VulkanVideoPresenter.ClearTextureContentTrackingForTests();
        try
        {
            Assert.False(VulkanVideoPresenter.IsTextureContentCached(identity));

            VulkanVideoPresenter.ReservePendingTextureUploadsForTests([texture]);
            Assert.True(VulkanVideoPresenter.IsTextureContentCached(identity));

            VulkanVideoPresenter.ReleasePendingTextureUploadsForTests([texture]);
            Assert.False(VulkanVideoPresenter.IsTextureContentCached(identity));
        }
        finally
        {
            VulkanVideoPresenter.ClearTextureContentTrackingForTests();
        }
    }

    [Fact]
    public void TexturePayloadAccounting_IncludesDistinctGpuDetileSource()
    {
        var rgba = new byte[17];
        var tiled = new byte[29];
        var texture = new GuestDrawTexture(
            Address: 0x1234,
            Width: 8,
            Height: 6,
            Format: 10,
            NumberType: 0,
            RgbaPixels: rgba,
            IsFallback: false,
            IsStorage: false,
            TiledSource: tiled);
        var sharedPayload = texture with { TiledSource = rgba };

        Assert.Equal(
            46UL,
            VulkanVideoPresenter.GetTexturePayloadBytesForTests([texture]));
        Assert.Equal(
            17UL,
            VulkanVideoPresenter.GetTexturePayloadBytesForTests([sharedPayload]));
    }

    [Fact]
    public void UntrackedLinearTextureProbe_RefreshesOnlyWhenGuestBytesChange()
    {
        const ulong address = 0x20000;
        var pixels = new byte[512 * 384 * 4];
        var memory = new FakeCpuMemory(address, pixels.Length);
        Assert.True(memory.TryWrite(address, pixels));
        var identity = CreateIdentity(type: 9, depth: 1) with
        {
            Address = address,
            Width = 512,
            Height = 384,
            Pitch = 512,
        };

        Assert.False(AgcExports.IsUntrackedLinearTextureContentUnchanged(
            memory, identity, address, (ulong)pixels.Length));
        Assert.True(AgcExports.IsUntrackedLinearTextureContentUnchanged(
            memory, identity, address, (ulong)pixels.Length));

        pixels[pixels.Length / 2] = 0x7F;
        Assert.True(memory.TryWrite(address, pixels));
        Assert.False(AgcExports.IsUntrackedLinearTextureContentUnchanged(
            memory, identity, address, (ulong)pixels.Length));
        Assert.True(AgcExports.IsUntrackedLinearTextureContentUnchanged(
            memory, identity, address, (ulong)pixels.Length));
    }

    [Theory]
    [InlineData(256u, 1u, 10u, 0u, 9u, 1024UL, true)]
    [InlineData(16u, 1u, 10u, 0u, 9u, 64UL, true)]
    [InlineData(257u, 1u, 10u, 0u, 9u, 1028UL, false)]
    [InlineData(256u, 2u, 10u, 0u, 9u, 2048UL, false)]
    [InlineData(256u, 1u, 10u, 1u, 9u, 1024UL, false)]
    [InlineData(256u, 1u, 11u, 0u, 9u, 1024UL, false)]
    [InlineData(256u, 1u, 10u, 0u, 10u, 1024UL, false)]
    [InlineData(256u, 1u, 10u, 0u, 9u, 4097UL, false)]
    public void SmallLinearPaletteClassifier_IsNarrowlyScoped(
        uint width,
        uint height,
        uint format,
        uint tileMode,
        uint type,
        ulong byteCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            AgcExports.IsSmallLinearPaletteDescriptor(
                width,
                height,
                format,
                tileMode,
                type,
                byteCount));
    }

    private static TextureContentIdentity CreateIdentity(uint type, uint depth) =>
        new(
            Address: 0x1234,
            Width: 8,
            Height: 6,
            Format: 10,
            NumberType: 0,
            DstSelect: 0xFAC,
            TileMode: 0,
            Pitch: 8,
            Sampler: default,
            Type: type,
            Depth: depth);
}
