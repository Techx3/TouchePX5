// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;

namespace Touche.PS5.Modules;

[JsonConverter(typeof(JsonStringEnumConverter<LleRelocationTableKind>))]
public enum LleRelocationTableKind
{
    Rela,
    ProcedureLinkage,
}

public sealed record LleRelocation(
    LleRelocationTableKind TableKind,
    ulong TargetVirtualAddress,
    uint SymbolIndex,
    uint Type,
    long Addend);

public sealed record LleDynamicLinkMetadata(
    ulong StringTableLocation,
    ulong StringTableSize,
    ulong SymbolTableLocation,
    ulong SymbolTableSize,
    ulong RelaLocation,
    ulong RelaSize,
    ulong ProcedureLinkageLocation,
    ulong ProcedureLinkageSize);

public sealed record LleDynamicSymbol(
    uint Index,
    string Name,
    byte Binding,
    byte Type,
    byte Visibility,
    ushort SectionIndex,
    ulong Value,
    ulong Size)
{
    public bool IsUndefined => SectionIndex == 0;
}

public sealed record LleModuleLinkPlan
{
    public required string FirmwareProfileId { get; init; }

    public required string ModuleVirtualPath { get; init; }

    public required string ModuleHash { get; init; }

    public required LleDynamicLinkMetadata Metadata { get; init; }

    public required IReadOnlyList<LleRelocation> Relocations { get; init; }

    public required IReadOnlyList<LleDynamicSymbol> ReferencedSymbols { get; init; }

    public required IReadOnlyList<LleDynamicSymbol> ImportedSymbols { get; init; }

    /// <summary>
    /// Globally visible symbols defined by this module and safe to publish as
    /// providers after the image has been mapped.
    /// </summary>
    public IReadOnlyList<LleDynamicSymbol> ExportedSymbols { get; init; } = [];

    public required IReadOnlyList<uint> UnsupportedRelocationTypes { get; init; }

    public bool CanApply => UnsupportedRelocationTypes.Count == 0;
}
