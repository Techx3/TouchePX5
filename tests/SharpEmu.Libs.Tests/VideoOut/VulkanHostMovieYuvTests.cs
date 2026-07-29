// Copyright (C) 2026 Touché PX5 Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanHostMovieYuvTests
{
    [Fact]
    public void ConvertsBgraChromaToNv12UvOrder()
    {
        var blueBgra = new byte[]
        {
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
            255, 0, 0, 255,
        };
        var luma = new byte[4];
        var chroma = new byte[2];

        VulkanVideoPresenter.ConvertBgraToYuv420(
            blueBgra,
            width: 2,
            height: 2,
            luma,
            chroma);

        Assert.True(chroma[0] > chroma[1], "NV12 must store blue-difference U before V.");
    }
}
