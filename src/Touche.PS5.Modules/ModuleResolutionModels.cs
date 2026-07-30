// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;

namespace Touche.PS5.Modules;

[JsonConverter(typeof(JsonStringEnumConverter<ModuleResolutionMode>))]
public enum ModuleResolutionMode
{
    Auto,
    PreferHle,
    PreferLle,
    HleOnly,
    LleOnly,
}

[JsonConverter(typeof(JsonStringEnumConverter<ModuleImplementationKind>))]
public enum ModuleImplementationKind
{
    Hle,
    Lle,
    Stub,
    Unresolved,
}

[JsonConverter(typeof(JsonStringEnumConverter<HleImplementationQuality>))]
public enum HleImplementationQuality
{
    CompleteStable,
    Partial,
    ControlledStub,
}

[JsonConverter(typeof(JsonStringEnumConverter<LleCompatibilityStatus>))]
public enum LleCompatibilityStatus
{
    Unknown,
    Compatible,
    Incompatible,
}

public sealed record HleModuleDescriptor(
    string ModuleName,
    HleImplementationQuality Quality);

public sealed record LleCompatibilityRecord
{
    public required string ModuleHash { get; init; }

    public required string FirmwareProfileId { get; init; }

    public required string CoreVersion { get; init; }

    public required LleCompatibilityStatus Status { get; init; }

    public IReadOnlyList<string> KnownCompatibleTitles { get; init; } = [];

    public IReadOnlyList<string> KnownIncompatibleTitles { get; init; } = [];

    public string? Reason { get; init; }
}

public sealed record GameModuleResolutionOverride(
    string TitleId,
    string ModuleName,
    ModuleResolutionMode Mode);

public sealed record ModuleResolutionPolicy
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public IReadOnlyList<HleModuleDescriptor> HleModules { get; init; } = [];

    public IReadOnlyList<LleCompatibilityRecord> LleCompatibility { get; init; } = [];

    public IReadOnlyList<GameModuleResolutionOverride> GameOverrides { get; init; } = [];
}

public sealed record ModuleResolutionRequest
{
    public required string ModuleName { get; init; }

    public ModuleResolutionMode RequestedMode { get; init; } = ModuleResolutionMode.Auto;

    public string? TitleId { get; init; }

    public required string FirmwareProfileId { get; init; }

    public required string CoreVersion { get; init; }

    public IReadOnlyList<GameModuleResolutionOverride> GameOverrides { get; init; } = [];
}

public sealed record ModuleResolutionDecision
{
    public required string ModuleName { get; init; }

    public required ModuleResolutionMode RequestedMode { get; init; }

    public required ModuleResolutionMode EffectiveMode { get; init; }

    public required ModuleImplementationKind SelectedImplementation { get; init; }

    public string? ModuleVirtualPath { get; init; }

    public string? ModuleHash { get; init; }

    public bool OverrideApplied { get; init; }

    public bool UsedFallback { get; init; }

    public required string ReasonCode { get; init; }

    public required string Reason { get; init; }
}
