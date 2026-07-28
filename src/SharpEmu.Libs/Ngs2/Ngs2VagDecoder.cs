// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Ngs2;

// Clean-room PS-ADPCM ("VAG") decoder. NGS2 sampler voices point at waveforms
// wrapped in the classic Sony "VAGp" container: a 48-byte big-endian header
// followed by 16-byte ADPCM frames (2-byte predictor/shift + flags, then 14
// bytes = 28 nibbles = 28 samples). The predictor coefficient table and the
// nibble decode are the publicly documented PSX SPU ADPCM algorithm.
public static class Ngs2VagDecoder
{
    // Standard PS-ADPCM predictor filters (scaled by 1/64).
    private static readonly int[] Coeff0 = { 0, 60, 115, 98, 122 };
    private static readonly int[] Coeff1 = { 0, 0, -52, -55, -60 };

    public const int VagHeaderSize = 0x30;
    private const uint VagMagic = 0x56414770; // "VAGp"
    private const uint HevagVersion2 = 0x00020001;
    private const uint HevagVersion3 = 0x00030000;

    public readonly struct Waveform
    {
        public Waveform(short[] samples, short[]? rightSamples, int sampleRate, int loopStart, int loopEnd)
        {
            Samples = samples;
            RightSamples = rightSamples;
            SampleRate = sampleRate;
            LoopStart = loopStart;
            LoopEnd = loopEnd;
        }

        public short[] Samples { get; }
        public short[]? RightSamples { get; } // null when the waveform is mono
        public int SampleRate { get; }
        public int LoopStart { get; } // -1 when the waveform does not loop
        public int LoopEnd { get; }
    }

    // True when the buffer begins with a recognizable "VAGp" container header.
    public static bool IsVag(ReadOnlySpan<byte> data) =>
        data.Length >= VagHeaderSize &&
        BinaryPrimitives.ReadUInt32BigEndian(data) == VagMagic;

    // Decode a full "VAGp" container into mono PCM16. Returns false when the
    // header is missing/short so callers can skip unsupported formats safely.
    public static bool TryDecode(ReadOnlySpan<byte> data, out Waveform waveform)
    {
        waveform = default;
        if (!IsVag(data))
        {
            return false;
        }

        // Header (big-endian): +0x0C dataSize, +0x10 sampleRate, +0x1E channel
        // count (0 or 1 = mono; 2 = stereo with L/R frames interleaved per
        // 16-byte block, as shipped by e.g. Castlevania: Dominus Collection).
        var version = BinaryPrimitives.ReadUInt32BigEndian(data[0x04..]);
        var declaredSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]);
        var sampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(data[0x10..]);
        var channels = data[0x1E] == 2 ? 2 : 1;
        if (sampleRate <= 0)
        {
            sampleRate = 48000;
        }

        var body = data[VagHeaderSize..];
        // Trust the declared payload size when it fits; otherwise decode what we
        // actually have (some tools pad or under-report).
        var available = body.Length - (body.Length % 16);
        var frameBytes = declaredSize > 0 && declaredSize <= available ? declaredSize - (declaredSize % 16) : available;
        if (frameBytes <= 0)
        {
            return false;
        }

        waveform = version is HevagVersion2 or HevagVersion3
            ? Ngs2HevagDecoder.Decode(body[..frameBytes], sampleRate, channels)
            : Decode(body[..frameBytes], sampleRate, channels);
        return waveform.Samples.Length > 0;
    }

    // Decode raw 16-byte-framed PS-ADPCM (no container header) into PCM16 and
    // resolve loop points from the per-frame flag bytes. Stereo containers
    // interleave one left frame and one right frame per 16-byte block; each
    // channel keeps its own predictor history, so decoding them as one mono
    // stream corrupts the prediction at every frame boundary (audible as a
    // frame-periodic buzz layered over the music).
    public static Waveform Decode(ReadOnlySpan<byte> frames, int sampleRate, int channels = 1)
    {
        var stereo = channels == 2;
        var frameCount = frames.Length / 16;
        var framesPerChannel = stereo ? frameCount / 2 : frameCount;
        var samples = new short[framesPerChannel * 28];
        var rightSamples = stereo ? new short[framesPerChannel * 28] : null;
        var loopStart = -1;
        var loopEnd = -1;

        Span<int> hist1 = stackalloc int[2];
        Span<int> hist2 = stackalloc int[2];
        Span<int> outIndex = stackalloc int[2];
        var ended = false;
        for (var frame = 0; frame < frameCount && !ended; frame++)
        {
            var channel = stereo ? frame & 1 : 0;
            var target = channel == 1 ? rightSamples! : samples;
            if (outIndex[channel] + 28 > target.Length)
            {
                break;
            }

            var offset = frame * 16;
            var header = frames[offset];
            var shift = header & 0x0F;
            var filter = (header >> 4) & 0x0F;
            if (filter > 4)
            {
                filter = 0;
            }

            // Per-frame loop marker (exact PS-ADPCM values, not bit masks):
            //   6 = loop start, 3 = loop end + jump back, 1/7 = one-shot end.
            // Stereo pairs carry the same flags on both frames; track loop
            // positions from the left channel only so they stay in per-channel
            // sample units.
            var flags = frames[offset + 1];
            if (flags == 0x06 && channel == 0)
            {
                loopStart = outIndex[0];
            }

            var f0 = Coeff0[filter];
            var f1 = Coeff1[filter];
            var h1 = hist1[channel];
            var h2 = hist2[channel];
            var writeIndex = outIndex[channel];
            for (var i = 0; i < 14; i++)
            {
                var d = frames[offset + 2 + i];
                for (var nibble = 0; nibble < 2; nibble++)
                {
                    var raw = nibble == 0 ? d & 0x0F : d >> 4;
                    // Sign-extend the 4-bit sample into the top nibble, then scale.
                    var s = (short)(raw << 12) >> shift;
                    var predicted = (h1 * f0 + h2 * f1) >> 6;
                    var sample = Math.Clamp(s + predicted, short.MinValue, short.MaxValue);
                    target[writeIndex++] = (short)sample;
                    h2 = h1;
                    h1 = sample;
                }
            }

            hist1[channel] = h1;
            hist2[channel] = h2;
            outIndex[channel] = writeIndex;

            if (flags == 0x03 && channel == 0)
            {
                loopEnd = outIndex[0];
            }

            // One-shot end: stop after the last frame of the (stereo) pair so
            // both channels decode the same number of samples.
            if ((flags == 0x01 || flags == 0x07) && (!stereo || channel == 1))
            {
                ended = true;
            }
        }

        // Trim to the samples we actually decoded (a one-shot end marker can stop
        // us before the declared frame count).
        var decoded = outIndex[0];
        if (decoded != samples.Length)
        {
            Array.Resize(ref samples, decoded);
        }

        if (rightSamples is not null && outIndex[1] != rightSamples.Length)
        {
            Array.Resize(ref rightSamples, outIndex[1]);
        }

        if (loopStart >= 0 && loopEnd <= loopStart)
        {
            loopEnd = decoded;
        }

        return new Waveform(samples, rightSamples, sampleRate, loopStart, loopEnd);
    }
}
