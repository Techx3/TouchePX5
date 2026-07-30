// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

/// <summary>
/// Decodes SPI_PS_INPUT_CNTL parameter mappings shared by every graphics backend.
/// </summary>
public static class Gen5PixelInputMapping
{
    private const uint DefaultValueFlag = 1u << 5;

    public static bool UsesDefaultValue(uint control) =>
        (control & DefaultValueFlag) != 0;

    public static uint GetParameterLocation(uint control) => control & 0x1Fu;

    public static uint GetDefaultComponentBits(uint control, uint component)
    {
        var selector = (control >> 8) & 0x3u;
        var isOne = component < 3u
            ? selector >= 2u
            : (selector & 1u) != 0;
        return isOne ? 0x3F800000u : 0u;
    }
}
