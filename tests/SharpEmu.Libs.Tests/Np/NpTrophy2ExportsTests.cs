// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpTrophy2ExportsTests
{
    [Fact]
    public void TrophyInfoArrayFailsClosedUntilItsLayoutsAreImplemented()
    {
        var context = new CpuContext(
            new FakeCpuMemory(0x7200_0000_0000UL, 0x100),
            Generation.Gen5);

        var result = NpTrophy2Exports.NpTrophy2GetTrophyInfoArray(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(
            unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND),
            context[CpuRegister.Rax]);
    }
}
