// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;

namespace Touche.Firmware;

[JsonConverter(typeof(JsonStringEnumConverter<FirmwareModuleFormat>))]
public enum FirmwareModuleFormat
{
    Unknown,
    Elf64,
    SonySelf,
}

[JsonConverter(typeof(JsonStringEnumConverter<FirmwareModuleState>))]
public enum FirmwareModuleState
{
    Unknown,
    Parseable,
    MissingDependencies,
    UnsupportedArchitecture,
    UnsupportedRelocation,
    UnsupportedEncryption,
    Loadable,
    LleCompatible,
    RuntimeIncompatible,
}

public sealed record FirmwareSelfMetadata(
    uint ProgramType,
    ushort HeaderSize,
    ushort MetadataSize,
    ulong DeclaredFileSize,
    ushort SegmentCount,
    bool HasOrderedSegments,
    bool HasEncryptedSegments,
    bool HasSignedSegments,
    bool HasCompressedSegments,
    bool HasBlockedSegments);

public sealed record FirmwareModule(
    string VirtualPath,
    string Sha256,
    FirmwareModuleFormat Format,
    FirmwareModuleState State,
    string? Architecture,
    ulong? EntryPoint,
    int ProgramHeaderCount,
    bool HasDynamicTable,
    IReadOnlyList<string> Dependencies,
    string? Reason)
{
    /// <summary>
    /// Guest path replaced by this artifact. This is only populated for a verified
    /// decrypted ELF sidecar whose name is the protected SELF path plus ".elf".
    /// </summary>
    public string? ProvidesVirtualPath { get; init; }

    /// <summary>
    /// Passive container metadata. Segment payloads are never decrypted or executed
    /// while building the firmware catalog.
    /// </summary>
    public FirmwareSelfMetadata? SelfMetadata { get; init; }
}

public sealed record FirmwareModuleCatalog
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string ProfileId { get; init; }

    public required string ContentHash { get; init; }

    public required IReadOnlyList<FirmwareModule> Modules { get; init; }
}
