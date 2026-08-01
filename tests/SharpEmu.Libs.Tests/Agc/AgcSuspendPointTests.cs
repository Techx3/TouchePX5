// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcSuspendPointTests
{
    [Fact]
    public void SuspendPoint_RequestsBriefCooperativeBlockForGuestThread()
    {
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var previousThread = GuestThreadExecution.EnterGuestThread(0x1234);

        try
        {
            var result = AgcExports.SuspendPoint(context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
            Assert.Equal(0UL, context[CpuRegister.Rax]);
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out var reason,
                out _,
                out _,
                out var wakeKey,
                out var waiter,
                out var deadline));
            Assert.Equal("agc_suspend_point", reason);
            Assert.Equal("agc_suspend_point", wakeKey);
            Assert.Null(waiter);
            Assert.True(deadline > 0);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }
    }
}
