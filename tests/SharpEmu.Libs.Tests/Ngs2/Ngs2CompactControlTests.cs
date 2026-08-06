// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

public sealed class Ngs2CompactControlTests
{
    [Theory]
    [InlineData((ushort)2, (ushort)0, 0x00000400u, true)]
    [InlineData((ushort)2, (ushort)1, 0x00000400u, false)]
    [InlineData((ushort)8, (ushort)0, 0x00000400u, false)]
    [InlineData((ushort)2, (ushort)0, 0x00000300u, false)]
    public void RecognizesOnlyObservedLifecyclePulse(
        ushort size,
        ushort next,
        uint id,
        bool expected)
    {
        Assert.Equal(expected, Ngs2Exports.IsCompactLifecyclePulse(size, next, id));
    }
}
