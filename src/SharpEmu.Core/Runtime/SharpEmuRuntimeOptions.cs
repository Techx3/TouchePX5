// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Runtime;

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Debugging;

public readonly struct SharpEmuRuntimeOptions
{
    public CpuExecutionEngine CpuEngine { get; init; }

    /// <summary>Maximum submitted video frames per second; 0 disables pacing.</summary>
    public int FpsLimit { get; init; }

    public bool StrictDynlibResolution { get; init; }

    public int ImportTraceLimit { get; init; }

    /// <summary>Enables the experimental, user-supplied firmware LLE provider path.</summary>
    public bool EnableExperimentalFirmwareLle { get; init; }

    /// <summary>Root of the local content-addressed extracted-firmware store.</summary>
    public string? FirmwareProfileStoreRoot { get; init; }

    /// <summary>Immutable identifier of the selected extracted firmware profile.</summary>
    public string? FirmwareProfileId { get; init; }

    /// <summary>
    /// An optional debugger to attach to guest execution. Flows through to
    /// <see cref="CpuExecutionOptions.DebugHook"/>. Null (the default) runs with
    /// no debugger attached.
    /// </summary>
    public ICpuDebugHook? DebugHook { get; init; }
}
