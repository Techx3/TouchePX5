// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VideoOutFlipStatusTests
{
    [Fact]
    public void EncodeFlipStatus_UsesProsperoLayoutAndClearsReservedFields()
    {
        var status = new byte[0x80];
        Array.Fill(status, (byte)0xCC);

        VideoOutExports.EncodeFlipStatus(
            status,
            count: 7,
            processTime: 11,
            processTimeCounter: 13,
            flipArg: -17,
            submitProcessTimeCounter: 19,
            gcQueueNum: 2,
            flipPendingNum: 3,
            currentBuffer: -1);

        Assert.Equal(7UL, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x00)));
        Assert.Equal(11UL, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x08)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x10)));
        Assert.Equal(-17L, BinaryPrimitives.ReadInt64LittleEndian(status.AsSpan(0x18)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x20)));
        Assert.Equal(13UL, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x28)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(0x30)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(0x34)));
        Assert.Equal(-1, BinaryPrimitives.ReadInt32LittleEndian(status.AsSpan(0x38)));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(status.AsSpan(0x3C)));
        Assert.Equal(19UL, BinaryPrimitives.ReadUInt64LittleEndian(status.AsSpan(0x40)));
        Assert.All(status[0x48..], value => Assert.Equal(0, value));
    }
}
