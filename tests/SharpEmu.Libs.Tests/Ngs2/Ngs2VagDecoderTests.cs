// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

public sealed class Ngs2VagDecoderTests
{
    [Fact]
    public void DecodeUsesSixAsLoopStartAndThreeAsLoopEnd()
    {
        var frames = new byte[2 * 16];
        frames[1] = 0x06;
        frames[17] = 0x03;

        var waveform = Ngs2VagDecoder.Decode(frames, 48_000);

        Assert.Equal(0, waveform.LoopStart);
        Assert.Equal(56, waveform.LoopEnd);
        Assert.Equal(56, waveform.Samples.Length);
    }

    [Fact]
    public void StereoLoopMarkersRemainInPerChannelSampleUnits()
    {
        var frames = new byte[4 * 16];
        frames[1] = 0x06;
        frames[17] = 0x06;
        frames[33] = 0x03;
        frames[49] = 0x03;

        var waveform = Ngs2VagDecoder.Decode(frames, 44_100, channels: 2);

        Assert.Equal(0, waveform.LoopStart);
        Assert.Equal(56, waveform.LoopEnd);
        Assert.Equal(56, waveform.Samples.Length);
        Assert.NotNull(waveform.RightSamples);
        Assert.Equal(56, waveform.RightSamples!.Length);
    }

    [Fact]
    public void TryDecodeUsesHevagPredictorsForVitaAndPs4Containers()
    {
        var data = new byte[Ngs2VagDecoder.VagHeaderSize + 16];
        "VAGp"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x00020001);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), 48_000);

        // Predictor 60, shift 8, flag 0, repeated positive code 7.
        data[48] = 0xC8;
        data[49] = 0x30;
        data.AsSpan(50, 14).Fill(0x77);

        Assert.True(Ngs2VagDecoder.TryDecode(data, out var waveform));
        short[] expected = [112, 282, 474, 663, 840, 1006, 1163, 1313];
        Assert.Equal(expected, waveform.Samples[..8]);
    }
}
