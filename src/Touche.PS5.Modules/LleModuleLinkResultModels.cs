// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.PS5.Modules;

public sealed record LleMaterializedImport(
    uint SymbolIndex,
    string SymbolName,
    ImportBindingSource Source,
    ulong RuntimeAddress,
    string? HleDispatchKey);

public sealed record LleAppliedRelocation(
    ulong RuntimeAddress,
    uint Type,
    int Width,
    ulong EncodedValue);

public sealed record LleLinkedModule
{
    public required string FirmwareProfileId { get; init; }

    public required string ModuleVirtualPath { get; init; }

    public required string ModuleHash { get; init; }

    public required ulong RuntimeImageStart { get; init; }

    public required IReadOnlyList<LleMaterializedImport> Imports { get; init; }

    public required IReadOnlyList<LleAppliedRelocation> Relocations { get; init; }
}
