// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;

namespace Touche.Firmware;

[JsonConverter(typeof(JsonStringEnumConverter<FirmwareArtifactKind>))]
public enum FirmwareArtifactKind
{
    Unknown,
    ElfOrSelf,
    FileSystemImage,
    Configuration,
    Resource,
    Archive,
    Encrypted,
}

[JsonConverter(typeof(JsonStringEnumConverter<FirmwareArtifactState>))]
public enum FirmwareArtifactState
{
    Discovered,
    Hashed,
    Recognized,
    Encrypted,
    Compressed,
    Unsupported,
    Corrupted,
    Catalogued,
}

public sealed record FirmwareArtifact(
    string VirtualPath,
    long Size,
    string Sha256,
    FirmwareArtifactKind Kind,
    FirmwareArtifactState State);

/// <summary>
/// Portable profile manifest. It deliberately contains no source paths,
/// timestamps or machine-specific metadata.
/// </summary>
public sealed record FirmwareProfileManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string ProfileId { get; init; }

    public string Platform { get; init; } = "ps5";

    public required string ContentHash { get; init; }

    public required IReadOnlyList<FirmwareArtifact> Artifacts { get; init; }
}

public sealed record FirmwareDirectoryScan(
    [property: JsonIgnore] string SourceDirectory,
    FirmwareProfileManifest Manifest,
    long TotalBytes);

public sealed record FirmwareImportResult(
    FirmwareProfileManifest Manifest,
    string ProfileDirectory,
    bool AlreadyImported,
    FirmwareModuleCatalog? ModuleCatalog = null);

public sealed record ImportedFirmwareProfile(
    string ProfileId,
    DateTimeOffset ImportedAtUtc,
    int ArtifactCount,
    long TotalBytes,
    int ModuleCount,
    int ParseableModuleCount,
    int MissingDependencyCount,
    int EncryptedModuleCount,
    int IncompatibleModuleCount,
    [property: JsonIgnore] string ProfileDirectory);

public sealed record FirmwareScanOptions
{
    public int MaximumFileCount { get; init; } = 200_000;

    public long MaximumTotalBytes { get; init; } = 256L * 1024 * 1024 * 1024;

    public long MaximumSingleFileBytes { get; init; } = 64L * 1024 * 1024 * 1024;
}
