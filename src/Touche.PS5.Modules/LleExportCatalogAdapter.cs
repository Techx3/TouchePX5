// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.PS5.Modules;

/// <summary>Publishes verified, mapped ELF exports for other LLE modules and the runtime.</summary>
public sealed class LleExportCatalogAdapter
{
    private const ushort ShnAbsolute = 0xfff1;

    public IReadOnlyList<LleExportDescriptor> CreateDescriptors(
        LleModuleLoadPlan loadPlan,
        LleMappedModule mappedModule,
        LleModuleLinkPlan linkPlan)
    {
        ArgumentNullException.ThrowIfNull(loadPlan);
        ArgumentNullException.ThrowIfNull(mappedModule);
        ArgumentNullException.ThrowIfNull(linkPlan);
        ValidateIdentity(loadPlan, mappedModule, linkPlan);

        var result = new List<LleExportDescriptor>(linkPlan.ExportedSymbols.Count);
        foreach (var symbol in linkPlan.ExportedSymbols.OrderBy(symbol => symbol.Name, StringComparer.Ordinal))
        {
            if (symbol is null ||
                symbol.IsUndefined ||
                string.IsNullOrWhiteSpace(symbol.Name) ||
                symbol.Name.Any(char.IsControl) ||
                symbol.Binding is not (1 or 2) ||
                symbol.Visibility is not (0 or 3) ||
                symbol.Type is not (1 or 2))
            {
                throw new InvalidDataException("The LLE export catalog contains an invalid symbol.");
            }

            ulong runtimeAddress;
            if (symbol.SectionIndex == ShnAbsolute)
            {
                runtimeAddress = symbol.Value;
            }
            else
            {
                if (symbol.Value < loadPlan.ImageVirtualStart ||
                    symbol.Value - loadPlan.ImageVirtualStart >= loadPlan.ImageSize ||
                    symbol.Size > loadPlan.ImageSize - (symbol.Value - loadPlan.ImageVirtualStart))
                {
                    throw new InvalidDataException($"LLE export '{symbol.Name}' is outside the mapped image.");
                }
                runtimeAddress = checked(
                    mappedModule.RuntimeImageStart + symbol.Value - loadPlan.ImageVirtualStart);
            }
            if (runtimeAddress == 0 || symbol.Size > ulong.MaxValue - runtimeAddress)
            {
                throw new InvalidDataException($"LLE export '{symbol.Name}' has an invalid runtime range.");
            }
            result.Add(new LleExportDescriptor(
                loadPlan.FirmwareProfileId,
                loadPlan.ModuleVirtualPath,
                loadPlan.ModuleHash,
                symbol.Name,
                runtimeAddress,
                symbol.Size)
            {
                SymbolType = symbol.Type,
                SonyIdentity = symbol.SonyIdentity,
            });
        }
        return result;
    }

    private static void ValidateIdentity(
        LleModuleLoadPlan loadPlan,
        LleMappedModule mappedModule,
        LleModuleLinkPlan linkPlan)
    {
        if (loadPlan.ExportedIdentity() != mappedModule.ExportedIdentity() ||
            loadPlan.ExportedIdentity() != linkPlan.ExportedIdentity() ||
            mappedModule.RuntimeImageStart == 0 ||
            mappedModule.ImageSize != loadPlan.ImageSize ||
            linkPlan.ExportedSymbols is null)
        {
            throw new InvalidDataException("The LLE export plans do not describe the same mapped module.");
        }
    }
}

internal static class LleExportIdentityExtensions
{
    public static (string Profile, string Path, string Hash) ExportedIdentity(this LleModuleLoadPlan plan) =>
        (plan.FirmwareProfileId, plan.ModuleVirtualPath, plan.ModuleHash);

    public static (string Profile, string Path, string Hash) ExportedIdentity(this LleMappedModule module) =>
        (module.FirmwareProfileId, module.ModuleVirtualPath, module.ModuleHash);

    public static (string Profile, string Path, string Hash) ExportedIdentity(this LleModuleLinkPlan plan) =>
        (plan.FirmwareProfileId, plan.ModuleVirtualPath, plan.ModuleHash);
}
