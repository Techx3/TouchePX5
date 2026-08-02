// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.PS5.Modules;

/// <summary>
/// Chooses providers for verified undefined symbols. It does not create HLE
/// thunks, patch relocations or call any provider.
/// </summary>
public sealed class HybridImportResolver
{
    private readonly IReadOnlyDictionary<HleKey, HleSymbolDescriptor> _hleSymbols;
    private readonly IReadOnlyDictionary<LleKey, LleExportDescriptor> _lleSymbols;
    private readonly IReadOnlyDictionary<LleContextKey, LleExportDescriptor> _contextualLleSymbols;
    private readonly IReadOnlyDictionary<LleKey, LleExportDescriptor> _uniqueLleNids;

    public HybridImportResolver(
        IEnumerable<HleSymbolDescriptor>? hleSymbols = null,
        IEnumerable<LleExportDescriptor>? lleSymbols = null)
    {
        _hleSymbols = BuildHleIndex(hleSymbols ?? []);
        var exports = (lleSymbols ?? []).ToArray();
        _lleSymbols = BuildLleIndex(exports);
        _contextualLleSymbols = BuildContextualLleIndex(exports);
        _uniqueLleNids = BuildUniqueLleNidIndex(exports);
    }

    public ModuleImportResolutionPlan Resolve(
        LleModuleLinkPlan linkPlan,
        ModuleResolutionMode mode = ModuleResolutionMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(linkPlan);
        ValidateLinkPlan(linkPlan);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var bindings = linkPlan.ImportedSymbols
            .OrderBy(symbol => symbol.Index)
            .Select(symbol => ResolveSymbol(linkPlan.FirmwareProfileId, symbol, mode))
            .ToArray();
        return new ModuleImportResolutionPlan
        {
            FirmwareProfileId = linkPlan.FirmwareProfileId,
            ModuleVirtualPath = linkPlan.ModuleVirtualPath,
            ModuleHash = linkPlan.ModuleHash,
            Mode = mode,
            RelocationsSupported = linkPlan.CanApply,
            Bindings = bindings,
        };
    }

    private ImportBindingDecision ResolveSymbol(
        string profileId,
        LleDynamicSymbol symbol,
        ModuleResolutionMode mode)
    {
        var expectedHleType = symbol.Type == 1 ? (byte)1 : (byte)2;
        _hleSymbols.TryGetValue(new HleKey(symbol.Name, expectedHleType), out var hle);
        if (hle is null)
        {
            _hleSymbols.TryGetValue(new HleKey(GetSonyNid(symbol.Name), expectedHleType), out hle);
        }
        _lleSymbols.TryGetValue(new LleKey(profileId, symbol.Name), out var lle);
        if (lle is null && symbol.SonyIdentity is not null)
        {
            _contextualLleSymbols.TryGetValue(
                LleContextKey.Create(profileId, symbol.SonyIdentity, symbol.Type),
                out lle);
        }
        if (lle is null)
        {
            _uniqueLleNids.TryGetValue(new LleKey(profileId, GetSonyNid(symbol.Name)), out lle);
        }
        return mode switch
        {
            ModuleResolutionMode.Auto => ResolveAuto(symbol, hle, lle),
            ModuleResolutionMode.PreferHle => ResolvePreferHle(symbol, hle, lle),
            ModuleResolutionMode.PreferLle => ResolvePreferLle(symbol, hle, lle),
            ModuleResolutionMode.HleOnly => ResolveHleOnly(symbol, hle),
            ModuleResolutionMode.LleOnly => lle is null
                ? Unresolved(symbol, "import.lle-only.unresolved")
                : SelectLle(symbol, lle, usedFallback: false),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static string GetSonyNid(string symbolName)
    {
        var separator = symbolName.IndexOf('#');
        return separator <= 0 ? symbolName : symbolName[..separator];
    }

    private static ImportBindingDecision ResolveAuto(
        LleDynamicSymbol symbol,
        HleSymbolDescriptor? hle,
        LleExportDescriptor? lle)
    {
        if (hle?.Quality == HleImplementationQuality.CompleteStable)
        {
            return SelectHle(symbol, hle, usedFallback: false);
        }
        if (lle is not null)
        {
            return SelectLle(symbol, lle, usedFallback: false);
        }
        if (hle?.Quality == HleImplementationQuality.Partial)
        {
            return SelectHle(symbol, hle, usedFallback: false);
        }
        if (hle?.Quality == HleImplementationQuality.ControlledStub)
        {
            return SelectStub(symbol, hle, usedFallback: false);
        }
        return Unresolved(symbol, "import.auto.unresolved");
    }

    private static ImportBindingDecision ResolvePreferHle(
        LleDynamicSymbol symbol,
        HleSymbolDescriptor? hle,
        LleExportDescriptor? lle)
    {
        if (hle?.Quality is HleImplementationQuality.CompleteStable or HleImplementationQuality.Partial)
        {
            return SelectHle(symbol, hle, usedFallback: false);
        }
        if (lle is not null)
        {
            return SelectLle(symbol, lle, usedFallback: true);
        }
        if (hle?.Quality == HleImplementationQuality.ControlledStub)
        {
            return SelectStub(symbol, hle, usedFallback: true);
        }
        return Unresolved(symbol, "import.prefer-hle.unresolved");
    }

    private static ImportBindingDecision ResolvePreferLle(
        LleDynamicSymbol symbol,
        HleSymbolDescriptor? hle,
        LleExportDescriptor? lle)
    {
        if (lle is not null)
        {
            return SelectLle(symbol, lle, usedFallback: false);
        }
        if (hle?.Quality is HleImplementationQuality.CompleteStable or HleImplementationQuality.Partial)
        {
            return SelectHle(symbol, hle, usedFallback: true);
        }
        if (hle?.Quality == HleImplementationQuality.ControlledStub)
        {
            return SelectStub(symbol, hle, usedFallback: true);
        }
        return Unresolved(symbol, "import.prefer-lle.unresolved");
    }

    private static ImportBindingDecision ResolveHleOnly(
        LleDynamicSymbol symbol,
        HleSymbolDescriptor? hle)
    {
        if (hle?.Quality is HleImplementationQuality.CompleteStable or HleImplementationQuality.Partial)
        {
            return SelectHle(symbol, hle, usedFallback: false);
        }
        if (hle?.Quality == HleImplementationQuality.ControlledStub)
        {
            return SelectStub(symbol, hle, usedFallback: false);
        }
        return Unresolved(symbol, "import.hle-only.unresolved");
    }

    private static ImportBindingDecision SelectHle(
        LleDynamicSymbol symbol,
        HleSymbolDescriptor descriptor,
        bool usedFallback) => descriptor.RuntimeAddress is not null
        ? new ImportBindingDecision
        {
            SymbolIndex = symbol.Index,
            SymbolName = symbol.Name,
            Source = ImportBindingSource.HleData,
            ProviderModule = descriptor.ModuleName,
            HleDataRuntimeAddress = descriptor.RuntimeAddress,
            UsedFallback = usedFallback,
            ReasonCode = "import.hle-data.mapped",
            Reason = "A stable HLE data symbol provider is available.",
        }
        : new ImportBindingDecision
        {
            SymbolIndex = symbol.Index,
            SymbolName = symbol.Name,
            Source = ImportBindingSource.Hle,
            ProviderModule = descriptor.ModuleName,
            HleDispatchKey = descriptor.DispatchKey,
            UsedFallback = usedFallback,
            ReasonCode = descriptor.Quality == HleImplementationQuality.CompleteStable
                ? "import.hle.complete"
                : "import.hle.partial",
            Reason = descriptor.Quality == HleImplementationQuality.CompleteStable
                ? "A complete, stable HLE symbol provider is available."
                : "A partial HLE symbol provider was selected.",
        };

    private static ImportBindingDecision SelectLle(
        LleDynamicSymbol symbol,
        LleExportDescriptor descriptor,
        bool usedFallback) => new()
        {
            SymbolIndex = symbol.Index,
            SymbolName = symbol.Name,
            Source = ImportBindingSource.Lle,
            ProviderModule = descriptor.ModuleVirtualPath,
            LleRuntimeAddress = descriptor.RuntimeAddress,
            UsedFallback = usedFallback,
            ReasonCode = "import.lle.mapped",
            Reason = "A mapped LLE export from the same firmware profile is available.",
        };

    private static ImportBindingDecision SelectStub(
        LleDynamicSymbol symbol,
        HleSymbolDescriptor descriptor,
        bool usedFallback) => new()
        {
            SymbolIndex = symbol.Index,
            SymbolName = symbol.Name,
            Source = ImportBindingSource.ControlledStub,
            ProviderModule = descriptor.ModuleName,
            HleDispatchKey = descriptor.DispatchKey,
            UsedFallback = usedFallback,
            ReasonCode = "import.stub.controlled",
            Reason = "A controlled HLE stub was selected.",
        };

    private static ImportBindingDecision Unresolved(
        LleDynamicSymbol symbol,
        string reasonCode) => new()
        {
            SymbolIndex = symbol.Index,
            SymbolName = symbol.Name,
            Source = ImportBindingSource.Unresolved,
            UsedFallback = false,
            ReasonCode = reasonCode,
            Reason = "No permitted provider is available for this imported symbol.",
        };

    private static IReadOnlyDictionary<HleKey, HleSymbolDescriptor> BuildHleIndex(
        IEnumerable<HleSymbolDescriptor> descriptors)
    {
        var result = new Dictionary<HleKey, HleSymbolDescriptor>();
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ValidateProviderName(descriptor.ModuleName, nameof(descriptors));
            ValidateSymbolName(descriptor.SymbolName, nameof(descriptors));
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.DispatchKey);
            if (descriptor.DispatchKey.Any(char.IsControl) ||
                !Enum.IsDefined(descriptor.Quality) ||
                descriptor.SymbolType is not (1 or 2) ||
                (descriptor.SymbolType == 1) != (descriptor.RuntimeAddress is not null) ||
                descriptor.RuntimeAddress == 0 ||
                !result.TryAdd(new HleKey(descriptor.SymbolName, descriptor.SymbolType), descriptor))
            {
                throw new InvalidDataException($"Invalid or duplicate HLE symbol provider: {descriptor.SymbolName}");
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<LleKey, LleExportDescriptor> BuildLleIndex(
        IEnumerable<LleExportDescriptor> descriptors)
    {
        var validated = new List<LleExportDescriptor>();
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.FirmwareProfileId);
            ValidateGuestPath(descriptor.ModuleVirtualPath, nameof(descriptors));
            ValidateHash(descriptor.ModuleHash);
            ValidateSymbolName(descriptor.SymbolName, nameof(descriptors));
            if (descriptor.RuntimeAddress == 0 ||
                descriptor.Size > ulong.MaxValue - descriptor.RuntimeAddress ||
                !IsValidSonyIdentity(descriptor.SymbolName, descriptor.SymbolType, descriptor.SonyIdentity))
            {
                throw new InvalidDataException($"Invalid LLE symbol provider: {descriptor.SymbolName}");
            }
            validated.Add(descriptor);
        }
        return validated
            .GroupBy(descriptor => new LleKey(descriptor.FirmwareProfileId, descriptor.SymbolName))
            .Select(group => (group.Key, Providers: DistinctProviders(group)))
            .Where(group => group.Providers.Length == 1)
            .ToDictionary(group => group.Key, group => group.Providers[0]);
    }

    private static IReadOnlyDictionary<LleKey, LleExportDescriptor> BuildUniqueLleNidIndex(
        IEnumerable<LleExportDescriptor> descriptors) =>
        descriptors
            .GroupBy(descriptor => new LleKey(
                descriptor.FirmwareProfileId,
                GetSonyNid(descriptor.SymbolName)))
            .Select(group => (group.Key, Providers: DistinctProviders(group)))
            .Where(group => group.Providers.Length == 1)
            .ToDictionary(group => group.Key, group => group.Providers[0]);

    private static IReadOnlyDictionary<LleContextKey, LleExportDescriptor> BuildContextualLleIndex(
        IEnumerable<LleExportDescriptor> descriptors)
    {
        return descriptors
            .Where(item => item.SonyIdentity is not null)
            .GroupBy(descriptor => LleContextKey.Create(
                descriptor.FirmwareProfileId,
                descriptor.SonyIdentity!,
                descriptor.SymbolType))
            .Select(group => (group.Key, Providers: DistinctProviders(group)))
            .Where(group => group.Providers.Length == 1)
            .ToDictionary(group => group.Key, group => group.Providers[0]);
    }

    private static LleExportDescriptor[] DistinctProviders(
        IEnumerable<LleExportDescriptor> descriptors) => descriptors
        .DistinctBy(descriptor => new LleProviderKey(
            descriptor.FirmwareProfileId,
            descriptor.ModuleVirtualPath,
            descriptor.ModuleHash,
            descriptor.RuntimeAddress,
            descriptor.Size,
            descriptor.SymbolType))
        .Take(2)
        .ToArray();

    private static void ValidateLinkPlan(LleModuleLinkPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plan.FirmwareProfileId);
        ValidateGuestPath(plan.ModuleVirtualPath, nameof(plan));
        ValidateHash(plan.ModuleHash);
        if (plan.Metadata is null ||
            plan.ReferencedSymbols is null ||
            plan.ImportedSymbols is null ||
            plan.Relocations is null ||
            plan.UnsupportedRelocationTypes is null ||
            plan.ReferencedSymbols.Any(symbol =>
                symbol is null ||
                symbol.Name is null ||
                symbol.Name.Any(char.IsControl) ||
                !IsValidSonyIdentity(symbol.Name, symbol.Type, symbol.SonyIdentity)) ||
            plan.ReferencedSymbols.GroupBy(symbol => symbol.Index).Any(group => group.Count() > 1) ||
            plan.ImportedSymbols.Any(symbol =>
                symbol is null ||
                !symbol.IsUndefined ||
                string.IsNullOrWhiteSpace(symbol.Name)) ||
            plan.ImportedSymbols.GroupBy(symbol => symbol.Index).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException("The LLE module link plan has invalid imported symbols.");
        }
        var referenced = plan.ReferencedSymbols.ToDictionary(symbol => symbol.Index);
        if (plan.ImportedSymbols.Any(symbol =>
                !referenced.TryGetValue(symbol.Index, out var candidate) ||
                candidate != symbol))
        {
            throw new InvalidDataException("An imported symbol is absent from the referenced symbol catalog.");
        }
    }

    private static void ValidateProviderName(string moduleName, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName, parameterName);
        if (moduleName.Contains('/') || moduleName.Contains('\\') || moduleName.Any(char.IsControl))
        {
            throw new InvalidDataException($"Invalid provider module name: {moduleName}");
        }
    }

    private static void ValidateSymbolName(string symbolName, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolName, parameterName);
        if (symbolName.Any(char.IsControl))
        {
            throw new InvalidDataException("Symbol names cannot contain control characters.");
        }
    }

    private static void ValidateGuestPath(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!path.StartsWith("/", StringComparison.Ordinal) ||
            path.Length == 1 ||
            path.Contains('\\') ||
            path.Contains("//", StringComparison.Ordinal) ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(component => component is "." or ".."))
        {
            throw new InvalidDataException($"Invalid guest module path: {path}");
        }
    }

    private static void ValidateHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("An LLE provider has an invalid module hash.");
        }
    }

    private readonly record struct LleKey(string FirmwareProfileId, string SymbolName);

    private readonly record struct HleKey(string SymbolName, byte SymbolType);

    private readonly record struct LleProviderKey(
        string FirmwareProfileId,
        string ModuleVirtualPath,
        string ModuleHash,
        ulong RuntimeAddress,
        ulong Size,
        byte SymbolType);

    private readonly record struct LleContextKey(
        string FirmwareProfileId,
        string Nid,
        string LibraryName,
        ushort LibraryVersion,
        string ModuleName,
        ushort ModuleVersion,
        byte SymbolType)
    {
        public static LleContextKey Create(
            string firmwareProfileId,
            LleSonySymbolIdentity identity,
            byte symbolType) => new(
                firmwareProfileId,
                identity.Nid,
                identity.LibraryName,
                identity.LibraryVersion,
                identity.ModuleName,
                identity.ModuleVersion,
                symbolType);
    }

    private static bool IsValidSonyIdentity(
        string symbolName,
        byte symbolType,
        LleSonySymbolIdentity? identity)
    {
        if (identity is null)
        {
            return true;
        }
        return identity.Nid.Length == 11 &&
            identity.Nid.All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '-') &&
            string.Equals(identity.Nid, GetSonyNid(symbolName), StringComparison.Ordinal) &&
            symbolType is 1 or 2 &&
            !string.IsNullOrWhiteSpace(identity.LibraryName) &&
            !identity.LibraryName.Any(char.IsControl) &&
            !string.IsNullOrWhiteSpace(identity.ModuleName) &&
            !identity.ModuleName.Any(char.IsControl);
    }
}
