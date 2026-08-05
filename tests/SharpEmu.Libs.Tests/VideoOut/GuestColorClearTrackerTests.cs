// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class GuestColorClearTrackerTests
{
    [Fact]
    public void ExactSubresourceIsConsumedOnlyOnce()
    {
        var tracker = new GuestColorClearTracker();
        var target = CreateTarget();

        tracker.Request(target);

        Assert.Equal([GuestColorClearTracker.GetKey(target)], tracker.TakeFor([target]));
        Assert.Empty(tracker.TakeFor([target]));
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void UnrelatedVariantDoesNotConsumePendingClear()
    {
        var tracker = new GuestColorClearTracker();
        var requested = CreateTarget();
        tracker.Request(requested);

        var variants = new[]
        {
            requested with { Width = 256 },
            requested with { Height = 192 },
            requested with { MipLevels = 2 },
            requested with { MipLevel = 1 },
            requested with { BaseArrayLayer = 1 },
            requested with { LayerCount = 2 },
            requested with { Format = 11 },
            requested with { NumberType = 4 },
            requested with { CompSwap = 0 },
            requested with { TileMode = 7 },
        };

        foreach (var variant in variants)
        {
            Assert.Empty(tracker.TakeFor([variant]));
        }

        Assert.Equal(1, tracker.Count);
        Assert.Single(tracker.TakeFor([requested]));
    }

    [Fact]
    public void MatchingTargetsAreAttachedToTheSameQueuedWork()
    {
        var tracker = new GuestColorClearTracker();
        var first = CreateTarget(address: 0x1000);
        var second = CreateTarget(address: 0x2000, baseArrayLayer: 2);
        tracker.Request(first);
        tracker.Request(second);

        var taken = tracker.TakeFor([first, second]);

        Assert.Equal(2, taken.Length);
        Assert.Contains(GuestColorClearTracker.GetKey(first), taken);
        Assert.Contains(GuestColorClearTracker.GetKey(second), taken);
        Assert.Equal(0, tracker.Count);
    }

    private static GuestRenderTarget CreateTarget(
        ulong address = 0x84B10000,
        uint baseArrayLayer = 0) =>
        new(
            address,
            Width: 512,
            Height: 384,
            Format: 10,
            NumberType: 0,
            MipLevels: 1,
            BaseArrayLayer: baseArrayLayer,
            LayerCount: 1,
            MipLevel: 0,
            CompSwap: 1,
            TileMode: 5);
}
