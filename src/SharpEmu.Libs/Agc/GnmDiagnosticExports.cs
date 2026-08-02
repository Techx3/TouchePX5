// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

/// <summary>
/// Conservative compatibility exports used by firmware coredump plumbing.
/// They do not participate in command submission or rendering.
/// </summary>
public static class GnmDiagnosticExports
{
    [SysAbiExport(
        Nid = "HRyNHoAjb6E",
        ExportName = "sceGnmIsCoredumpValid",
        Target = Generation.Gen5,
        LibraryName = "libSceGnmDriver")]
    public static int GnmIsCoredumpValid(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "O-7nHKgcNSQ",
        ExportName = "sceGnmGetCoredumpProtectionFaultTimestamp",
        Target = Generation.Gen5,
        LibraryName = "libSceGnmDriver")]
    public static int GnmGetCoredumpProtectionFaultTimestamp(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }
}
