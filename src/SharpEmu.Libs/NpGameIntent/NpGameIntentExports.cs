// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Libs.NpGameIntent;

public static class NpGameIntentExports
{
    private static int _initialized;

    [SysAbiExport(
        Nid = "m87BHxt-H60",
        ExportName = "sceNpGameIntentInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentInitialize(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 1);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private const int NpIntentNotFound = unchecked((int)0x80553806);
    private const int NpIntentNotInitialized = unchecked((int)0x80553802);
    private const int NpIntentInvalidArgument = unchecked((int)0x80553804);

    [SysAbiExport(
        Nid = "jEIXUAr9XE8",
        ExportName = "sceNpGameIntentReceiveIntent",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpGameIntent")]
    public static int NpGameIntentReceiveIntent(CpuContext ctx)
    {
        // No launch intent is ever pending in the emulator; report "not found" so the
        // title keeps booting normally instead of waiting on an intent handoff.
        var result = Volatile.Read(ref _initialized) == 0
            ? NpIntentNotInitialized
            : ctx[CpuRegister.Rdi] == 0
                ? NpIntentInvalidArgument
                : NpIntentNotFound;
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)result);
        return result;
    }
}
