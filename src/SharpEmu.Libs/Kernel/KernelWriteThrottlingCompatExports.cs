// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

internal static class KernelWriteThrottlingCompatExports
{
    // Kyty identifies this import by NID; the public symbol name is not present
    // in the available PS5 symbol catalog.
#pragma warning disable SHEM004, SHEM006
    [SysAbiExport(
        Nid = "YFC3dBBipj8",
        ExportName = "sceKernelWriteThrottlingCompat",
        Target = Generation.Gen5,
        LibraryName = "libkernel_write_throttling")]
    public static int DisableWriteThrottling(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
#pragma warning restore SHEM004, SHEM006
}
