// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// Backend-neutral view of the RDNA2 image resource descriptor fields used by
/// shader translation and diagnostics. Width, height and depth are exposed as
/// their real extents; the guest stores width/height/depth minus one.
/// </summary>
public sealed record Gen5ImageDescriptor(
    ulong BaseAddress,
    uint UnifiedFormat,
    uint DataFormat,
    uint NumberFormat,
    uint Width,
    uint Height,
    uint Depth,
    uint Pitch,
    uint BaseLevel,
    uint LastLevel,
    uint BaseArray,
    uint LastArray,
    uint DstSelect,
    uint SwizzleMode,
    uint ResourceType,
    uint DescriptorFlags,
    ulong MetadataAddress)
{
    public static bool TryDecode(
        IReadOnlyList<uint> words,
        out Gen5ImageDescriptor descriptor,
        out string error)
    {
        descriptor = default!;
        error = string.Empty;
        if (words.Count < 4)
        {
            error = $"descriptor has {words.Count} dwords; expected at least 4";
            return false;
        }

        var unifiedFormat = (words[1] >> 20) & 0x1FFu;
        if (unifiedFormat == 0 ||
            !Gfx10UnifiedFormat.TryDecode(
                unifiedFormat,
                out var dataFormat,
                out var numberFormat))
        {
            error = $"invalid unified format 0x{unifiedFormat:X}";
            return false;
        }

        var baseAddress =
            (((ulong)(words[1] & 0xFFu) << 32) | words[0]) << 8;
        var width =
            (((words[1] >> 30) & 0x3u) | ((words[2] & 0x3FFFu) << 2)) + 1;
        var height = ((words[2] >> 14) & 0xFFFFu) + 1;
        var resourceType = (words[3] >> 28) & 0xFu;
        var word4 = words.Count >= 5 ? words[4] : 0u;
        var depthOrLastSlice = (word4 & 0x1FFFu) + 1;
        var depth = resourceType is 10u or 11u or 12u or 13u or 15u
            ? depthOrLastSlice
            : 1u;
        var lastArray = resourceType is 11u or 12u or 13u or 15u
            ? depthOrLastSlice - 1u
            : 0u;
        var pitch = resourceType is 8u or 9u or 14u && word4 != 0
            ? (word4 & 0x3FFFu) + 1
            : width;
        var word6 = words.Count >= 7 ? words[6] : 0u;
        var word7 = words.Count >= 8 ? words[7] : 0u;

        descriptor = new Gen5ImageDescriptor(
            baseAddress,
            unifiedFormat,
            dataFormat,
            numberFormat,
            width,
            height,
            depth,
            pitch,
            (words[3] >> 12) & 0xFu,
            (words[3] >> 16) & 0xFu,
            (word4 >> 16) & 0x1FFFu,
            lastArray,
            words[3] & 0xFFFu,
            (words[3] >> 20) & 0x1Fu,
            resourceType,
            word6 & 0x00FF_FFFFu,
            ((((ulong)word7 << 8) | (word6 >> 24)) << 8));
        return true;
    }
}
