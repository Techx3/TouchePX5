// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanHostSurfaceTests
{
    [Fact]
    public void PixelSize_IsPublishedAsOneCoherentSnapshot()
    {
        using var surface = new VulkanHostSurface(VulkanHostSurfaceKind.Win32, 1);

        surface.UpdatePixelSize(1920, 1080);

        Assert.Equal((1920, 1080), surface.GetPixelSize());
        Assert.Equal(1920, surface.PixelWidth);
        Assert.Equal(1080, surface.PixelHeight);
    }

    [Fact]
    public void ChildDescriptor_DoesNotExportProcessLocalInstanceHandle()
    {
        using var surface = new VulkanHostSurface(
            VulkanHostSurfaceKind.Win32,
            windowHandle: 0x1234,
            instanceHandle: 0x5678);
        surface.UpdatePixelSize(1280, 720);

        Assert.True(surface.TryGetChildProcessDescriptor(out var descriptor));
        Assert.Equal("win32:1234:1280:720:0", descriptor);
    }

    [Fact]
    public void ChildDescriptor_RejectsOversizedSurface()
    {
        var descriptor = $"win32:1:{int.MaxValue}:720:0";

        Assert.False(VulkanHostSurface.TryCreateChildProcessSurface(
            descriptor,
            out var surface,
            out var error));
        Assert.Null(surface);
        Assert.Contains("invalid size", error);
    }

    [Fact]
    public void ChildDescriptor_RejectsForeignPlatformBeforeNativeCalls()
    {
        var kind = OperatingSystem.IsWindows() ? "xlib" : "win32";

        Assert.False(VulkanHostSurface.TryCreateChildProcessSurface(
            $"{kind}:1:640:480:0",
            out var surface,
            out var error));
        Assert.Null(surface);
        Assert.Contains("only valid", error);
    }

    [Fact]
    public void Dispose_IsIdempotentAndStopsSizeUpdates()
    {
        var surface = new VulkanHostSurface(VulkanHostSurfaceKind.Win32, 1);
        surface.UpdatePixelSize(800, 600);

        surface.Dispose();
        surface.Dispose();
        surface.UpdatePixelSize(1024, 768);

        Assert.Equal((800, 600), surface.GetPixelSize());
    }
}
