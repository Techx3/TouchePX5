// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Touche.PS5.Modules;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Converts the active HLE registry into neutral descriptors consumed by the
/// hybrid import resolver. It never exposes executable delegates to the LLE layer.
/// </summary>
public sealed class HleImportCatalogAdapter
{
    public IReadOnlyList<HleSymbolDescriptor> CreateDescriptors(
        IModuleManager moduleManager,
        IEnumerable<HleModuleDescriptor>? moduleQualities = null,
        HleImplementationQuality defaultQuality = HleImplementationQuality.Partial,
        Generation target = Generation.Gen5)
    {
        ArgumentNullException.ThrowIfNull(moduleManager);
        if (!Enum.IsDefined(defaultQuality))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultQuality));
        }
        if (target == Generation.None || (target & ~(Generation.Gen4 | Generation.Gen5)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        var qualities = BuildQualityIndex(moduleQualities ?? []);
        var exports = moduleManager.GetExports()
            .Where(export => (export.Target & target) != 0)
            .OrderBy(export => export.Nid, StringComparer.Ordinal)
            .ToArray();
        var reservedIdentifiers = exports
            .Select(export => export.Nid)
            .ToHashSet(StringComparer.Ordinal);
        var uniqueNames = exports
            .Where(export => !string.Equals(export.Name, export.Nid, StringComparison.Ordinal))
            .GroupBy(export => export.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1 && !reservedIdentifiers.Contains(group.Key))
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        var descriptors = new List<HleSymbolDescriptor>(exports.Length + uniqueNames.Count);
        foreach (var export in exports)
        {
            var quality = qualities.GetValueOrDefault(NormalizeModuleName(export.LibraryName), defaultQuality);
            descriptors.Add(CreateDescriptor(export, export.Nid, quality));
            if (uniqueNames.TryGetValue(export.Name, out var uniqueExport) && ReferenceEquals(export, uniqueExport))
            {
                descriptors.Add(CreateDescriptor(export, export.Name, quality));
            }
        }

        return descriptors
            .OrderBy(descriptor => descriptor.SymbolName, StringComparer.Ordinal)
            .ToArray();
    }

    private static HleSymbolDescriptor CreateDescriptor(
        ExportedFunction export,
        string symbolName,
        HleImplementationQuality quality) => new(
            export.LibraryName,
            symbolName,
            export.Nid,
            quality);

    private static IReadOnlyDictionary<string, HleImplementationQuality> BuildQualityIndex(
        IEnumerable<HleModuleDescriptor> descriptors)
    {
        var result = new Dictionary<string, HleImplementationQuality>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.ModuleName);
            if (!Enum.IsDefined(descriptor.Quality) ||
                !result.TryAdd(NormalizeModuleName(descriptor.ModuleName), descriptor.Quality))
            {
                throw new InvalidDataException($"Invalid or duplicate HLE module quality: {descriptor.ModuleName}");
            }
        }
        return result;
    }

    private static string NormalizeModuleName(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if (moduleName.Any(char.IsControl) || moduleName.Contains('/') || moduleName.Contains('\\'))
        {
            throw new InvalidDataException($"Invalid HLE module name: {moduleName}");
        }

        var extension = Path.GetExtension(moduleName);
        return extension.Equals(".prx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sprx", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(moduleName)
            : moduleName;
    }
}
