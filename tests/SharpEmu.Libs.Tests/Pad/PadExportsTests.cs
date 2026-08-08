// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    [Fact]
    public void GetTriggerEffectStateWritesEightBytesWithoutTouchingGuard()
    {
        const ulong stateAddress = Base + 0x100;
        const ulong guardAddress = stateAddress + 8;
        const ulong guard = 0xC0DE_C0DE_CAFE_BA00UL;
        Assert.True(_memory.TryWrite(stateAddress, Enumerable.Repeat((byte)0xEE, 8).ToArray()));
        Assert.True(_memory.TryWrite(guardAddress, BitConverter.GetBytes(guard)));

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = stateAddress;

        Assert.Equal(0, PadExports.PadGetTriggerEffectState(_ctx));

        Span<byte> state = stackalloc byte[8];
        Span<byte> preservedGuard = stackalloc byte[8];
        Assert.True(_memory.TryRead(stateAddress, state));
        Assert.True(_memory.TryRead(guardAddress, preservedGuard));
        Assert.True(state.SequenceEqual(new byte[8]));
        Assert.Equal(guard, BitConverter.ToUInt64(preservedGuard));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void GetTriggerEffectStateRejectsForeignHandles(int handle)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        _ctx[CpuRegister.Rsi] = Base + 0x100;

        Assert.Equal(InvalidHandle, PadExports.PadGetTriggerEffectState(_ctx));
    }

    [Theory]
    [InlineData(Generation.Gen5, false, 0, true)]
    [InlineData(Generation.Gen5, false, 1, true)]
    [InlineData(Generation.Gen5, false, 2, true)]
    [InlineData(Generation.Gen4, false, 0, true)]
    [InlineData(Generation.Gen4, false, 1, false)]
    [InlineData(Generation.Gen4, false, 2, false)]
    [InlineData(Generation.Gen4, true, 2, true)]
    [InlineData(Generation.Gen5, true, 3, false)]
    public void PortTypeAcceptance_MatchesGeneration(
        Generation generation,
        bool extended,
        int type,
        bool expected)
    {
        Assert.Equal(expected, PadExports.IsPortTypeAccepted(generation, extended, type));
    }

    [Fact]
    public void DeviceClassParseData_ReturnsStandardClassWithoutExtendedPayload()
    {
        const ulong padDataAddress = Base + 0x100;
        const ulong classDataAddress = Base + 0x300;
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = padDataAddress;
        _ctx[CpuRegister.Rdx] = classDataAddress;

        Assert.Equal(0, PadExports.PadDeviceClassParseData(_ctx));

        var classData = new byte[0x18];
        Assert.True(_memory.TryRead(classDataAddress, classData));
        Assert.All(classData, value => Assert.Equal(0, value));
    }

    [Fact]
    public void DeviceClassParseData_RejectsInvalidHandle()
    {
        _ctx[CpuRegister.Rdi] = 2;
        _ctx[CpuRegister.Rsi] = Base + 0x100;
        _ctx[CpuRegister.Rdx] = Base + 0x300;

        Assert.Equal(InvalidHandle, PadExports.PadDeviceClassParseData(_ctx));
    }
}
