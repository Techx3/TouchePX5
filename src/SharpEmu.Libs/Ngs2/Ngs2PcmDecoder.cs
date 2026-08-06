// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Ngs2;

/// <summary>Decodes the little-endian signed PCM waveform types used by NGS2.</summary>
public static class Ngs2PcmDecoder
{
    public const uint Signed16LittleEndian = 0x12;

    public static bool TryDecodeInterleaved(
        ReadOnlySpan<byte> data,
        int channels,
        int requestedFrames,
        out short[] left,
        out short[]? right)
    {
        left = [];
        right = null;
        if (channels is < 1 or > 8)
        {
            return false;
        }

        var availableFrames = data.Length / (sizeof(short) * channels);
        var frames = requestedFrames > 0
            ? Math.Min(requestedFrames, availableFrames)
            : availableFrames;
        if (frames <= 0)
        {
            return false;
        }

        left = new short[frames];
        right = channels > 1 ? new short[frames] : null;
        var frameBytes = sizeof(short) * channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * frameBytes;
            left[frame] = BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
            if (right is not null)
            {
                right[frame] = BinaryPrimitives.ReadInt16LittleEndian(data[(offset + sizeof(short))..]);
            }
        }

        return true;
    }
}
