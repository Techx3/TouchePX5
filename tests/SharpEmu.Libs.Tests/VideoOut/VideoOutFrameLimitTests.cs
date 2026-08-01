// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutFrameLimitTests
{
    [Theory]
    [InlineData(0, 60, 60)]
    [InlineData(1, 60, 30)]
    [InlineData(2, 60, 20)]
    [InlineData(0, 30, 30)]
    [InlineData(1, 15, 15)]
    [InlineData(0, 120, 60)]
    public void LimitCapsGuestFlipCadence(
        int flipRate,
        int configuredLimit,
        int expectedRate)
    {
        Assert.Equal(
            expectedRate,
            VideoOutExports.ResolveFramePacingRate(flipRate, configuredLimit));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLimitDisablesPacing(int configuredLimit)
    {
        Assert.Equal(
            0,
            VideoOutExports.ResolveFramePacingRate(0, configuredLimit));
    }
}
