// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;

namespace Touche.PS5.Modules;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<LleSegmentPermissions>))]
public enum LleSegmentPermissions
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
}

public sealed record LleLoadSegment(
    int ProgramHeaderIndex,
    ulong FileOffset,
    ulong FileSize,
    ulong VirtualAddress,
    ulong MemorySize,
    ulong Alignment,
    LleSegmentPermissions Permissions);

public sealed record LleModuleLoadPlan
{
    public required string FirmwareProfileId { get; init; }

    public required string ModuleVirtualPath { get; init; }

    public required string ModuleHash { get; init; }

    public required ushort ElfType { get; init; }

    public required ulong EntryPoint { get; init; }

    public required ulong ImageVirtualStart { get; init; }

    public required ulong ImageSize { get; init; }

    public required bool HasDynamicTable { get; init; }

    public required IReadOnlyList<LleLoadSegment> Segments { get; init; }
}

public sealed record LleMappedSegment(
    int ProgramHeaderIndex,
    ulong RuntimeAddress,
    ulong MemorySize,
    LleSegmentPermissions Permissions);

public sealed record LleMappedModule
{
    public required string FirmwareProfileId { get; init; }

    public required string ModuleVirtualPath { get; init; }

    public required string ModuleHash { get; init; }

    public required ulong RuntimeImageStart { get; init; }

    public required ulong RuntimeEntryPoint { get; init; }

    public required ulong ImageSize { get; init; }

    public required IReadOnlyList<LleMappedSegment> Segments { get; init; }
}
