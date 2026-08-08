// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

public sealed class Ngs2PcmDecoderTests
{
    [Theory]
    [InlineData(true, false, false, 0x3u)]
    [InlineData(false, true, false, 0x5u)]
    [InlineData(false, false, true, 0u)]
    [InlineData(false, false, false, 0u)]
    public void VoiceStateFlagsReleaseCompletedVoices(
        bool playing,
        bool paused,
        bool stopped,
        uint expected)
    {
        Assert.Equal(expected, Ngs2Exports.GetVoiceStateFlags(playing, paused, stopped));
    }

    [Theory]
    [InlineData(true, 0x00000005u, true)]
    [InlineData(true, 0x10000000u, true)]
    [InlineData(true, 0x10000001u, true)]
    [InlineData(true, 0x00000002u, false)]
    [InlineData(true, 0x00000006u, false)]
    [InlineData(false, 0x10000001u, false)]
    public void ReusedVoiceResetsOnlyForFreshAudioConfiguration(
        bool reusableObserved,
        uint paramId,
        bool expected)
    {
        Assert.Equal(
            expected,
            Ngs2Exports.ShouldPrepareVoiceForReuse(reusableObserved, paramId));
    }

    [Theory]
    [InlineData(0u, true, false, false, true)]
    [InlineData(0u, false, true, false, true)]
    [InlineData(0u, false, false, true, true)]
    [InlineData(0u, false, false, false, false)]
    [InlineData(3u, true, true, true, false)]
    public void OnlyCompletedOrKilledIdleVoicesBecomeReusable(
        uint stateFlags,
        bool stopped,
        bool explicitlyStopped,
        bool compactLifecycleStopped,
        bool expected)
    {
        Assert.Equal(
            expected,
            Ngs2Exports.ShouldMarkVoiceReusable(
                stateFlags,
                stopped,
                explicitlyStopped,
                compactLifecycleStopped));
    }

    [Fact]
    public void CompactLifecycleRetiresOnlyLoopingVagVoices()
    {
        Assert.True(Ngs2Exports.ShouldRetireOnCompactLifecycle(
            streamingPending: false, waveformType: 0, hasPcm: true, loopStart: 28));
        Assert.False(Ngs2Exports.ShouldRetireOnCompactLifecycle(
            streamingPending: false, waveformType: 0, hasPcm: true, loopStart: -1));
        Assert.False(Ngs2Exports.ShouldRetireOnCompactLifecycle(
            streamingPending: true, waveformType: Ngs2PcmDecoder.Signed16LittleEndian,
            hasPcm: true, loopStart: -1));
    }

    [Fact]
    public void DecodesStereoSigned16LittleEndian()
    {
        byte[] data = [0x01, 0x00, 0xFF, 0x7F, 0x00, 0x80, 0xFE, 0xFF];

        Assert.True(Ngs2PcmDecoder.TryDecodeInterleaved(data, 2, 0, out var left, out var right));
        Assert.Equal([1, short.MinValue], left);
        Assert.Equal([short.MaxValue, -2], right);
    }

    [Fact]
    public void HonorsRequestedFrameCountForMono()
    {
        byte[] data = [0x01, 0x00, 0x02, 0x00, 0x03, 0x00];

        Assert.True(Ngs2PcmDecoder.TryDecodeInterleaved(data, 1, 2, out var left, out var right));
        Assert.Equal([1, 2], left);
        Assert.Null(right);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void RejectsInvalidChannelCounts(int channels)
    {
        Assert.False(Ngs2PcmDecoder.TryDecodeInterleaved([0, 0], channels, 0, out _, out _));
    }

    [Fact]
    public void StreamingTransitionAdvancesFromPreviousBlockToNewBlock()
    {
        var first = Ngs2Exports.GetStreamingTransitionWeight(96);
        var middle = Ngs2Exports.GetStreamingTransitionWeight(48);
        var last = Ngs2Exports.GetStreamingTransitionWeight(1);

        Assert.InRange(first, 0.0104f, 0.0105f);
        Assert.InRange(middle, 0.5104f, 0.5105f);
        Assert.Equal(1f, last);
        Assert.Equal(1f, Ngs2Exports.GetStreamingTransitionWeight(0));
    }

    [Fact]
    public void DirectPcmSizeIsLearnedOnceAcrossBufferRotations()
    {
        var learned = Ngs2Exports.LearnDirectPcmBufferBytes(
            currentBytes: 0,
            previousAddress: 0x10000,
            nextAddress: 0x10400);
        var preserved = Ngs2Exports.LearnDirectPcmBufferBytes(
            currentBytes: learned,
            previousAddress: 0x10400,
            nextAddress: 0x18000);

        Assert.Equal(1024, learned);
        Assert.Equal(1024, preserved);
    }

    [Theory]
    [InlineData(0x10000UL, 0x10040UL)]
    [InlineData(0x10000UL, 0x500004UL)]
    [InlineData(0x10001UL, 0x10402UL)]
    public void DirectPcmSizeRejectsImplausibleFirstStride(ulong first, ulong second)
    {
        Assert.Equal(0, Ngs2Exports.LearnDirectPcmBufferBytes(0, first, second));
    }

    [Fact]
    public void StreamingSnapshotMustRemainStableBeforeDecode()
    {
        Assert.True(Ngs2Exports.StreamingSnapshotsMatch([1, 2, 3], [1, 2, 3]));
        Assert.False(Ngs2Exports.StreamingSnapshotsMatch([1, 2, 3], [1, 9, 3]));
    }

    [Fact]
    public void OutputLimiterAttacksImmediatelyBeforeClipping()
    {
        var gain = Ngs2Exports.GetNextLimiterGain(currentGain: 1f, peak: 2f);

        Assert.InRange(gain, 0.489f, 0.491f);
        Assert.True(2f * gain <= 0.98f);
    }

    [Fact]
    public void OutputLimiterReleasesGraduallyAfterPeakPasses()
    {
        var gain = Ngs2Exports.GetNextLimiterGain(currentGain: 0.5f, peak: 0.4f);

        Assert.InRange(gain, 0.504f, 0.506f);
    }
}
