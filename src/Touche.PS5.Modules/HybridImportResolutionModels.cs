// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;

namespace Touche.PS5.Modules;

[JsonConverter(typeof(JsonStringEnumConverter<ImportBindingSource>))]
public enum ImportBindingSource
{
    Hle,
    Lle,
    ControlledStub,
    Unresolved,
}

public sealed record HleSymbolDescriptor(
    string ModuleName,
    string SymbolName,
    string DispatchKey,
    HleImplementationQuality Quality);

public sealed record LleExportDescriptor(
    string FirmwareProfileId,
    string ModuleVirtualPath,
    string ModuleHash,
    string SymbolName,
    ulong RuntimeAddress,
    ulong Size);

public sealed record ImportBindingDecision
{
    public required uint SymbolIndex { get; init; }

    public required string SymbolName { get; init; }

    public required ImportBindingSource Source { get; init; }

    public string? ProviderModule { get; init; }

    public string? HleDispatchKey { get; init; }

    public ulong? LleRuntimeAddress { get; init; }

    public bool UsedFallback { get; init; }

    public required string ReasonCode { get; init; }

    public required string Reason { get; init; }
}

public sealed record ModuleImportResolutionPlan
{
    public required string FirmwareProfileId { get; init; }

    public required string ModuleVirtualPath { get; init; }

    public required ModuleResolutionMode Mode { get; init; }

    public required bool RelocationsSupported { get; init; }

    public required IReadOnlyList<ImportBindingDecision> Bindings { get; init; }

    public bool CanLink =>
        RelocationsSupported &&
        Bindings.All(binding => binding.Source != ImportBindingSource.Unresolved);
}
