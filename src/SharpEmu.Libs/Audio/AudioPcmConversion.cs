// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Audio;

/// <summary>
/// Converts guest AudioOut submissions (mono/stereo/7.1, s16 or float32) into the
/// interleaved stereo 16-bit PCM that host audio streams accept. Platform-neutral —
/// device specifics live behind IHostAudioStream.
/// </summary>
internal static class AudioPcmConversion
{
    /// <summary>Bytes per output frame: two 16-bit channels.</summary>
    public const int OutputFrameSize = 4;

    public static void ConvertToStereoPcm16(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        float volume)
    {
        Span<float> channelVolumes = stackalloc float[Math.Max(channels, 1)];
        channelVolumes.Fill(volume);
        ConvertToStereoPcm16(
            source,
            destination,
            frames,
            channels,
            bytesPerSample,
            isFloat,
            channelVolumes);
    }

    public static void ConvertToStereoPcm16(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        int frames,
        int channels,
        int bytesPerSample,
        bool isFloat,
        ReadOnlySpan<float> channelVolumes)
    {
        var sourceFrameSize = checked(channels * bytesPerSample);
        for (var frame = 0; frame < frames; frame++)
        {
            var sourceFrame = source.Slice(frame * sourceFrameSize, sourceFrameSize);
            var left = ReadSampleFloat(sourceFrame, 0, bytesPerSample, isFloat) * GetVolume(channelVolumes, 0);
            var right = channels == 1
                ? left
                : ReadSampleFloat(sourceFrame, 1, bytesPerSample, isFloat) * GetVolume(channelVolumes, 1);

            // Prospero 5.1/7.1 order: FL, FR, C, LFE, BL, BR, SL, SR.
            // Preserve effects routed outside the front pair while honoring
            // the per-channel mute/volume values supplied by the title.
            if (channels > 2)
            {
                var center = ReadSampleFloat(sourceFrame, 2, bytesPerSample, isFloat) * GetVolume(channelVolumes, 2);
                left += center * 0.70710678f;
                right += center * 0.70710678f;
            }
            if (channels > 3)
            {
                var lfe = ReadSampleFloat(sourceFrame, 3, bytesPerSample, isFloat) * GetVolume(channelVolumes, 3);
                left += lfe * 0.5f;
                right += lfe * 0.5f;
            }
            if (channels > 4)
            {
                left += ReadSampleFloat(sourceFrame, 4, bytesPerSample, isFloat) * GetVolume(channelVolumes, 4) * 0.70710678f;
            }
            if (channels > 5)
            {
                right += ReadSampleFloat(sourceFrame, 5, bytesPerSample, isFloat) * GetVolume(channelVolumes, 5) * 0.70710678f;
            }
            if (channels > 6)
            {
                left += ReadSampleFloat(sourceFrame, 6, bytesPerSample, isFloat) * GetVolume(channelVolumes, 6) * 0.70710678f;
            }
            if (channels > 7)
            {
                right += ReadSampleFloat(sourceFrame, 7, bytesPerSample, isFloat) * GetVolume(channelVolumes, 7) * 0.70710678f;
            }

            BinaryPrimitives.WriteInt16LittleEndian(
                destination[(frame * OutputFrameSize)..],
                ConvertFloatSample(left));
            BinaryPrimitives.WriteInt16LittleEndian(
                destination[((frame * OutputFrameSize) + 2)..],
                ConvertFloatSample(right));
        }

    }

    private static float GetVolume(ReadOnlySpan<float> volumes, int channel) =>
        channel < volumes.Length ? Math.Clamp(volumes[channel], 0.0f, 1.0f) : 1.0f;

    private static float ReadSampleFloat(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        bool isFloat)
    {
        var sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
        if (isFloat)
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(sample);
            return float.IsNaN(value) ? 0f : Math.Clamp(value, -1f, 1f);
        }

        var pcm = BinaryPrimitives.ReadInt16LittleEndian(sample);
        return pcm < 0 ? pcm / 32768f : pcm / 32767f;
    }

    private static short ReadSample(
        ReadOnlySpan<byte> frame,
        int channel,
        int bytesPerSample,
        bool isFloat)
    {
        var sample = frame.Slice(channel * bytesPerSample, bytesPerSample);
        if (!isFloat)
        {
            return BinaryPrimitives.ReadInt16LittleEndian(sample);
        }

        var bits = BinaryPrimitives.ReadInt32LittleEndian(sample);
        return ConvertFloatSample(BitConverter.Int32BitsToSingle(bits));
    }

    private static short ConvertFloatSample(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        value = Math.Clamp(value, -1.0f, 1.0f);
        var scale = value < 0.0f ? 32768.0f : short.MaxValue;
        return checked((short)MathF.Round(value * scale));
    }

    // <paramref name="volume"/> is expected pre-clamped to [0, 1] by the caller.
    private static short ApplyVolume(short sample, float volume)
    {
        var scaled = MathF.Round(sample * volume);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }
}
