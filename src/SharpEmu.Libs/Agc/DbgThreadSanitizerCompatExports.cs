// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Agc;

// The firmware exposes this NID but its private diagnostic name is not part of
// the public symbol catalog. Keep the explicit compatibility name local.
#pragma warning disable SHEM006

/// <summary>
/// Controlled no-op used only when the optional firmware ThreadSanitizer
/// provider is absent. Retail execution does not require sanitizer reporting.
/// </summary>
public static class DbgThreadSanitizerCompatExports
{
    [SysAbiExport(
        Nid = "iCzcQSfuX-E",
        ExportName = "sceCompatDbgThreadSanitizerForAgcDriver",
        Target = Generation.Gen5,
        LibraryName = "libSceDbgThreadSanitizer")]
    public static int DbgThreadSanitizerForAgcDriver(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }
}
#pragma warning restore SHEM006
