// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Touche.Core.Contracts;
using Xunit;

namespace Touche.Core.Contracts.Tests;

public sealed class SessionDescriptorTests
{
    [Fact]
    public void DefaultsAreStableForTheInitialSchema()
    {
        var descriptor = CreateDescriptor();

        Assert.Equal(1, descriptor.SchemaVersion);
        Assert.Equal("touche.ps5", descriptor.CoreId);
        Assert.Equal(SessionSurfaceMode.Embedded, descriptor.Graphics.SurfaceMode);
        Assert.Equal("default", descriptor.Audio.DeviceId);
        Assert.Equal(1.0, descriptor.Audio.Volume);
        Assert.Equal("dual-sense-default", descriptor.Input.ProfileId);
        Assert.True(descriptor.Diagnostics.CaptureCrashDump);
    }

    [Fact]
    public void JsonRoundTripPreservesVersionedSessionInput()
    {
        var descriptor = CreateDescriptor() with
        {
            Graphics = new GraphicsSessionSettings
            {
                AdapterId = "vulkan-device-1",
                FpsLimit = 60,
                SurfaceMode = SessionSurfaceMode.SeparateWindow,
            },
            Firmware = new FirmwareSessionSettings { ProfileId = "ps5-fw-local" },
        };

        var json = JsonSerializer.Serialize(descriptor);
        var restored = JsonSerializer.Deserialize<SessionDescriptor>(json);

        Assert.NotNull(restored);
        Assert.Equal(descriptor, restored);
        Assert.Contains("\"SeparateWindow\"", json);
        Assert.DoesNotContain("SharpEmu", json, StringComparison.OrdinalIgnoreCase);
    }

    private static SessionDescriptor CreateDescriptor() => new()
    {
        SessionId = Guid.Parse("4fcb7c9a-0a49-4d71-b6ef-10fb75a487a8"),
        CoreId = ToucheCoreIds.PlayStation5,
        Game = new GameLaunchRequest
        {
            ExecutablePath = @"F:\Games\Example\eboot.bin",
            TitleId = "PPSA00000",
            DisplayName = "Example",
            ContentHash = "sha256:0123456789abcdef",
        },
    };
}
