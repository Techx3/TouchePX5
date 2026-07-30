// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace Touche.Firmware;

public sealed class FirmwareProfileRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly string _storeRoot;

    public FirmwareProfileRepository(string storeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        _storeRoot = Path.GetFullPath(storeRoot);
    }

    public string StoreRoot => _storeRoot;

    public IReadOnlyList<ImportedFirmwareProfile> GetImportedProfiles()
    {
        var profilesRoot = Path.Combine(_storeRoot, "profiles");
        if (!Directory.Exists(profilesRoot))
        {
            return [];
        }

        var profiles = new List<ImportedFirmwareProfile>();
        foreach (var directory in Directory.EnumerateDirectories(profilesRoot))
        {
            try
            {
                var manifest = Read<FirmwareProfileManifest>(Path.Combine(directory, "manifest.json"));
                if (manifest is null ||
                    manifest.SchemaVersion != FirmwareProfileManifest.CurrentSchemaVersion ||
                    manifest.Artifacts is null ||
                    string.IsNullOrWhiteSpace(manifest.ProfileId) ||
                    string.IsNullOrWhiteSpace(manifest.ContentHash) ||
                    !string.Equals(Path.GetFileName(directory), manifest.ProfileId, StringComparison.Ordinal) ||
                    !string.Equals(manifest.ProfileId, $"ps5-extracted-{manifest.ContentHash}", StringComparison.Ordinal) ||
                    manifest.Artifacts.Any(artifact => !IsValidArtifact(artifact)))
                {
                    continue;
                }

                long totalBytes = 0;
                foreach (var artifact in manifest.Artifacts)
                {
                    totalBytes = checked(totalBytes + artifact.Size);
                }

                var catalog = Read<FirmwareModuleCatalog>(Path.Combine(directory, "modules.json"));
                if (catalog is not null &&
                    (catalog.SchemaVersion != FirmwareModuleCatalog.CurrentSchemaVersion ||
                     catalog.Modules is null ||
                     !string.Equals(catalog.ProfileId, manifest.ProfileId, StringComparison.Ordinal)))
                {
                    continue;
                }

                var modules = catalog?.Modules ?? [];
                var installation = Read<FirmwareProfileInstallation>(Path.Combine(directory, "installation.json"));
                var importedAt = installation is not null &&
                                 string.Equals(installation.ProfileId, manifest.ProfileId, StringComparison.Ordinal)
                    ? installation.ImportedAtUtc
                    : new DateTimeOffset(Directory.GetCreationTimeUtc(directory), TimeSpan.Zero);
                profiles.Add(new ImportedFirmwareProfile(
                    manifest.ProfileId,
                    importedAt,
                    manifest.Artifacts.Count,
                    totalBytes,
                    modules.Count,
                    modules.Count(module => module.State == FirmwareModuleState.Parseable),
                    modules.Count(module => module.State == FirmwareModuleState.MissingDependencies),
                    modules.Count(module => module.State == FirmwareModuleState.UnsupportedEncryption),
                    modules.Count(module => module.State is
                        FirmwareModuleState.UnsupportedArchitecture or
                        FirmwareModuleState.UnsupportedRelocation or
                        FirmwareModuleState.RuntimeIncompatible),
                    directory));
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                JsonException or
                OverflowException)
            {
                // Incomplete or manually modified profiles are ignored.
            }
        }

        profiles.Sort(static (left, right) => right.ImportedAtUtc.CompareTo(left.ImportedAtUtc));
        return profiles;
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    }

    private static bool IsValidArtifact(FirmwareArtifact artifact) =>
        artifact.Size >= 0 &&
        !string.IsNullOrWhiteSpace(artifact.Sha256) &&
        !string.IsNullOrWhiteSpace(artifact.VirtualPath) &&
        artifact.Sha256.Length == 64 &&
        artifact.Sha256.All(char.IsAsciiHexDigit) &&
        artifact.VirtualPath.StartsWith("/", StringComparison.Ordinal) &&
        !artifact.VirtualPath.Contains('\\') &&
        !artifact.VirtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component is "." or "..");

    private sealed record FirmwareProfileInstallation(string ProfileId, DateTimeOffset ImportedAtUtc);
}
