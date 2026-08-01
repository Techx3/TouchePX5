// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using Touche.Firmware;

namespace Touche.PS5.Modules;

/// <summary>
/// Produces deterministic HLE/LLE plans. It does not load, map or execute a
/// firmware module.
/// </summary>
public sealed class HybridModuleResolver
{
    private readonly FirmwareModuleCatalog? _catalog;
    private readonly IReadOnlyDictionary<string, HleModuleDescriptor> _hleModules;
    private readonly IReadOnlyDictionary<CompatibilityKey, LleCompatibilityRecord> _compatibility;
    private readonly IReadOnlyList<GameModuleResolutionOverride> _gameOverrides;

    public HybridModuleResolver(
        FirmwareModuleCatalog? catalog,
        IEnumerable<HleModuleDescriptor>? hleModules = null,
        IEnumerable<LleCompatibilityRecord>? compatibilityRecords = null,
        IEnumerable<GameModuleResolutionOverride>? gameOverrides = null)
    {
        _catalog = catalog;
        _hleModules = BuildHleIndex(hleModules ?? []);
        _compatibility = BuildCompatibilityIndex(compatibilityRecords ?? []);
        _gameOverrides = BuildOverrideList(gameOverrides ?? []);
    }

    public HybridModuleResolver(FirmwareModuleCatalog? catalog, ModuleResolutionPolicy policy)
        : this(
            catalog,
            ValidatePolicy(policy).HleModules,
            policy.LleCompatibility,
            policy.GameOverrides)
    {
    }

    public ModuleResolutionDecision Resolve(ModuleResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var moduleLookup = FindModule(request.ModuleName);
        var canonicalName = moduleLookup.Module is null
            ? Path.GetFileName(request.ModuleName)
            : Path.GetFileName(moduleLookup.Module.ProvidesVirtualPath ?? moduleLookup.Module.VirtualPath);
        _hleModules.TryGetValue(canonicalName, out var hle);
        var (effectiveMode, overrideApplied) = GetEffectiveMode(request, canonicalName);
        var lle = EvaluateLle(request, moduleLookup);

        return effectiveMode switch
        {
            ModuleResolutionMode.Auto => ResolveAuto(request, effectiveMode, overrideApplied, moduleLookup, hle, lle),
            ModuleResolutionMode.PreferHle => ResolvePreferHle(request, effectiveMode, overrideApplied, moduleLookup, hle, lle),
            ModuleResolutionMode.PreferLle => ResolvePreferLle(request, effectiveMode, overrideApplied, moduleLookup, hle, lle),
            ModuleResolutionMode.HleOnly => ResolveHleOnly(request, effectiveMode, overrideApplied, moduleLookup, hle, lle),
            ModuleResolutionMode.LleOnly => ResolveLleOnly(request, effectiveMode, overrideApplied, moduleLookup, lle),
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported module resolution mode."),
        };
    }

    public IReadOnlyList<ModuleResolutionDecision> ResolveMany(
        IEnumerable<ModuleResolutionRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);

        return requests
            .Select(Resolve)
            .OrderBy(decision => decision.ModuleName, StringComparer.Ordinal)
            .ToArray();
    }

    private ModuleResolutionDecision ResolveAuto(
        ModuleResolutionRequest request,
        ModuleResolutionMode effectiveMode,
        bool overrideApplied,
        ModuleLookup module,
        HleModuleDescriptor? hle,
        LleEvaluation lle)
    {
        if (hle?.Quality == HleImplementationQuality.CompleteStable)
        {
            return SelectHle(request, effectiveMode, overrideApplied, module, hle, usedFallback: false);
        }
        if (lle.IsEligible)
        {
            return SelectLle(request, effectiveMode, overrideApplied, module.Module!, usedFallback: false, lle);
        }
        if (hle?.Quality == HleImplementationQuality.Partial)
        {
            return SelectHle(request, effectiveMode, overrideApplied, module, hle, usedFallback: false);
        }
        if (hle?.Quality == HleImplementationQuality.ControlledStub)
        {
            return SelectStub(request, effectiveMode, overrideApplied, module, hle, usedFallback: false);
        }
        return Unresolved(request, effectiveMode, overrideApplied, module, lle, "module.auto.unresolved");
    }

    private ModuleResolutionDecision ResolvePreferHle(
        ModuleResolutionRequest request,
        ModuleResolutionMode effectiveMode,
        bool overrideApplied,
        ModuleLookup module,
        HleModuleDescriptor? hle,
        LleEvaluation lle)
    {
        if (hle?.Quality is HleImplementationQuality.CompleteStable or HleImplementationQuality.Partial)
        {
            return SelectHle(request, effectiveMode, overrideApplied, module, hle, usedFallback: false);
        }
        if (lle.IsEligible)
        {
            return SelectLle(request, effectiveMode, overrideApplied, module.Module!, usedFallback: true, lle);
        }
        if (hle?.Quality == HleImplementationQuality.ControlledStub)
        {
            return SelectStub(request, effectiveMode, overrideApplied, module, hle, usedFallback: true);
        }
        return Unresolved(request, effectiveMode, overrideApplied, module, lle, "module.prefer-hle.unresolved");
    }

    private ModuleResolutionDecision ResolvePreferLle(
        ModuleResolutionRequest request,
        ModuleResolutionMode effectiveMode,
        bool overrideApplied,
        ModuleLookup module,
        HleModuleDescriptor? hle,
        LleEvaluation lle)
    {
        if (lle.IsEligible)
        {
            return SelectLle(request, effectiveMode, overrideApplied, module.Module!, usedFallback: false, lle);
        }
        if (hle?.Quality is HleImplementationQuality.CompleteStable or HleImplementationQuality.Partial)
        {
            return SelectHle(request, effectiveMode, overrideApplied, module, hle, usedFallback: true);
        }
        if (hle?.Quality == HleImplementationQuality.ControlledStub)
        {
            return SelectStub(request, effectiveMode, overrideApplied, module, hle, usedFallback: true);
        }
        return Unresolved(request, effectiveMode, overrideApplied, module, lle, "module.prefer-lle.unresolved");
    }

    private ModuleResolutionDecision ResolveHleOnly(
        ModuleResolutionRequest request,
        ModuleResolutionMode effectiveMode,
        bool overrideApplied,
        ModuleLookup module,
        HleModuleDescriptor? hle,
        LleEvaluation lle)
    {
        if (hle?.Quality is HleImplementationQuality.CompleteStable or HleImplementationQuality.Partial)
        {
            return SelectHle(request, effectiveMode, overrideApplied, module, hle, usedFallback: false);
        }
        if (hle?.Quality == HleImplementationQuality.ControlledStub)
        {
            return SelectStub(request, effectiveMode, overrideApplied, module, hle, usedFallback: false);
        }
        return Unresolved(request, effectiveMode, overrideApplied, module, lle, "module.hle-only.unresolved");
    }

    private ModuleResolutionDecision ResolveLleOnly(
        ModuleResolutionRequest request,
        ModuleResolutionMode effectiveMode,
        bool overrideApplied,
        ModuleLookup module,
        LleEvaluation lle) =>
        lle.IsEligible
            ? SelectLle(request, effectiveMode, overrideApplied, module.Module!, usedFallback: false, lle)
            : Unresolved(request, effectiveMode, overrideApplied, module, lle, "module.lle-only.unresolved");

    private static ModuleResolutionDecision SelectHle(
        ModuleResolutionRequest request,
        ModuleResolutionMode effectiveMode,
        bool overrideApplied,
        ModuleLookup module,
        HleModuleDescriptor hle,
        bool usedFallback) => new()
        {
            ModuleName = request.ModuleName,
            RequestedMode = request.RequestedMode,
            EffectiveMode = effectiveMode,
            SelectedImplementation = ModuleImplementationKind.Hle,
            ModuleVirtualPath = module.Module?.VirtualPath,
            ModuleHash = module.Module?.Sha256,
            OverrideApplied = overrideApplied,
            UsedFallback = usedFallback,
            ReasonCode = hle.Quality == HleImplementationQuality.CompleteStable
                ? "module.hle.complete"
                : "module.hle.partial",
            Reason = hle.Quality == HleImplementationQuality.CompleteStable
                ? "A complete, stable HLE implementation is available."
                : "A partial HLE implementation was selected.",
        };

    private static ModuleResolutionDecision SelectLle(
        ModuleResolutionRequest request,
        ModuleResolutionMode effectiveMode,
        bool overrideApplied,
        FirmwareModule module,
        bool usedFallback,
        LleEvaluation lle) => new()
        {
            ModuleName = request.ModuleName,
            RequestedMode = request.RequestedMode,
            EffectiveMode = effectiveMode,
            SelectedImplementation = ModuleImplementationKind.Lle,
            ModuleVirtualPath = module.VirtualPath,
            ModuleHash = module.Sha256,
            OverrideApplied = overrideApplied,
            UsedFallback = usedFallback,
            ReasonCode = "module.lle.compatible",
            Reason = lle.Reason,
        };

    private static ModuleResolutionDecision SelectStub(
        ModuleResolutionRequest request,
        ModuleResolutionMode effectiveMode,
        bool overrideApplied,
        ModuleLookup module,
        HleModuleDescriptor hle,
        bool usedFallback) => new()
        {
            ModuleName = request.ModuleName,
            RequestedMode = request.RequestedMode,
            EffectiveMode = effectiveMode,
            SelectedImplementation = ModuleImplementationKind.Stub,
            ModuleVirtualPath = module.Module?.VirtualPath,
            ModuleHash = module.Module?.Sha256,
            OverrideApplied = overrideApplied,
            UsedFallback = usedFallback,
            ReasonCode = "module.stub.controlled",
            Reason = $"A controlled stub is registered for {hle.ModuleName}.",
        };

    private static ModuleResolutionDecision Unresolved(
        ModuleResolutionRequest request,
        ModuleResolutionMode effectiveMode,
        bool overrideApplied,
        ModuleLookup module,
        LleEvaluation lle,
        string reasonCode) => new()
        {
            ModuleName = request.ModuleName,
            RequestedMode = request.RequestedMode,
            EffectiveMode = effectiveMode,
            SelectedImplementation = ModuleImplementationKind.Unresolved,
            ModuleVirtualPath = module.Module?.VirtualPath,
            ModuleHash = module.Module?.Sha256,
            OverrideApplied = overrideApplied,
            UsedFallback = false,
            ReasonCode = reasonCode,
            Reason = module.Ambiguous
                ? "Multiple firmware modules share this file name; use an absolute guest path."
                : $"No permitted implementation is available. LLE: {lle.Reason}",
        };

    private LleEvaluation EvaluateLle(ModuleResolutionRequest request, ModuleLookup lookup)
    {
        if (lookup.Ambiguous)
        {
            return new LleEvaluation(false, "The firmware module name is ambiguous.");
        }
        var module = lookup.Module;
        if (module is null)
        {
            return new LleEvaluation(false, "No catalogued firmware module was found.");
        }
        if (_catalog is null || !string.Equals(_catalog.ProfileId, request.FirmwareProfileId, StringComparison.Ordinal))
        {
            return new LleEvaluation(false, "The requested firmware profile does not match the module catalog.");
        }
        if (module.Format != FirmwareModuleFormat.Elf64)
        {
            return new LleEvaluation(false, "Only decrypted ELF64 modules can be considered for LLE.");
        }
        if (module.State is not FirmwareModuleState.Parseable and not FirmwareModuleState.LleCompatible)
        {
            return new LleEvaluation(false, $"Module catalog state is {module.State}.");
        }

        if (string.IsNullOrWhiteSpace(module.Sha256) ||
            module.Sha256.Length != 64 ||
            !module.Sha256.All(char.IsAsciiHexDigit))
        {
            return new LleEvaluation(false, "The catalogued module hash is invalid.");
        }

        var key = new CompatibilityKey(
            module.Sha256.ToLowerInvariant(),
            request.FirmwareProfileId,
            request.CoreVersion);
        if (!_compatibility.TryGetValue(key, out var compatibility))
        {
            return new LleEvaluation(false, "No compatibility record exists for this module hash, firmware and core version.");
        }
        if (!string.IsNullOrWhiteSpace(request.TitleId) &&
            compatibility.KnownIncompatibleTitles.Contains(request.TitleId, StringComparer.OrdinalIgnoreCase))
        {
            return new LleEvaluation(false, $"Title {request.TitleId} is explicitly incompatible with this LLE module.");
        }
        if (compatibility.Status != LleCompatibilityStatus.Compatible)
        {
            return new LleEvaluation(
                false,
                compatibility.Reason ?? $"Compatibility status is {compatibility.Status}.");
        }

        return new LleEvaluation(
            true,
            compatibility.Reason ?? "The module hash is explicitly compatible with this firmware and core version.");
    }

    private ModuleLookup FindModule(string requestedName)
    {
        if (_catalog?.Modules is null)
        {
            return default;
        }
        if (requestedName.StartsWith("/", StringComparison.Ordinal))
        {
            return SelectPreferredModule(_catalog.Modules.Where(module =>
                string.Equals(
                    module.ProvidesVirtualPath ?? module.VirtualPath,
                    requestedName,
                    StringComparison.Ordinal)));
        }

        return SelectPreferredModule(_catalog.Modules
            .Where(module => string.Equals(
                Path.GetFileName(module.ProvidesVirtualPath ?? module.VirtualPath),
                requestedName,
                StringComparison.Ordinal)));
    }

    private static ModuleLookup SelectPreferredModule(IEnumerable<FirmwareModule> candidates)
    {
        var matches = candidates.ToArray();
        if (matches
            .Select(module => module.ProvidesVirtualPath ?? module.VirtualPath)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() > 1)
        {
            return new ModuleLookup(null, Ambiguous: true);
        }

        var overrides = matches
            .Where(module => module.ProvidesVirtualPath is not null)
            .Take(2)
            .ToArray();
        if (overrides.Length == 1)
        {
            return new ModuleLookup(overrides[0], Ambiguous: false);
        }
        if (overrides.Length > 1)
        {
            return new ModuleLookup(null, Ambiguous: true);
        }

        return matches.Length switch
        {
            0 => default,
            1 => new ModuleLookup(matches[0], Ambiguous: false),
            _ => new ModuleLookup(null, Ambiguous: true),
        };
    }

    private (ModuleResolutionMode Mode, bool OverrideApplied) GetEffectiveMode(
        ModuleResolutionRequest request,
        string canonicalName)
    {
        if (string.IsNullOrWhiteSpace(request.TitleId))
        {
            return (request.RequestedMode, false);
        }

        var requestOverride = FindOverride(request.GameOverrides, request, canonicalName);
        if (requestOverride is not null)
        {
            return (requestOverride.Mode, true);
        }

        var policyOverride = FindOverride(_gameOverrides, request, canonicalName);
        return policyOverride is null
            ? (request.RequestedMode, false)
            : (policyOverride.Mode, true);
    }

    private static GameModuleResolutionOverride? FindOverride(
        IEnumerable<GameModuleResolutionOverride> overrides,
        ModuleResolutionRequest request,
        string canonicalName)
    {
        var matches = overrides.Where(item =>
                string.Equals(item.TitleId, request.TitleId, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(item.ModuleName, request.ModuleName, StringComparison.Ordinal) ||
                 string.Equals(item.ModuleName, canonicalName, StringComparison.Ordinal)))
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException(
                $"Multiple module resolution overrides match {request.TitleId}/{request.ModuleName}."),
        };
    }

    private static ModuleResolutionPolicy ValidatePolicy(ModuleResolutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.SchemaVersion != ModuleResolutionPolicy.CurrentSchemaVersion ||
            policy.HleModules is null ||
            policy.LleCompatibility is null ||
            policy.GameOverrides is null)
        {
            throw new InvalidDataException("The module resolution policy is invalid or unsupported.");
        }
        return policy;
    }

    private static IReadOnlyDictionary<string, HleModuleDescriptor> BuildHleIndex(
        IEnumerable<HleModuleDescriptor> descriptors)
    {
        var result = new Dictionary<string, HleModuleDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ValidateSimpleModuleName(descriptor.ModuleName, nameof(descriptors));
            if (!Enum.IsDefined(descriptor.Quality))
            {
                throw new InvalidDataException($"Invalid HLE quality for {descriptor.ModuleName}.");
            }
            if (!result.TryAdd(descriptor.ModuleName, descriptor))
            {
                throw new InvalidDataException($"Duplicate HLE module descriptor: {descriptor.ModuleName}");
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<CompatibilityKey, LleCompatibilityRecord> BuildCompatibilityIndex(
        IEnumerable<LleCompatibilityRecord> records)
    {
        var result = new Dictionary<CompatibilityKey, LleCompatibilityRecord>();
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (string.IsNullOrWhiteSpace(record.ModuleHash) ||
                record.ModuleHash.Length != 64 ||
                !record.ModuleHash.All(char.IsAsciiHexDigit))
            {
                throw new InvalidDataException($"Invalid LLE compatibility module hash: {record.ModuleHash}");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(record.FirmwareProfileId);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.CoreVersion);
            if (!Enum.IsDefined(record.Status) ||
                record.KnownCompatibleTitles is null ||
                record.KnownIncompatibleTitles is null ||
                record.KnownCompatibleTitles.Any(string.IsNullOrWhiteSpace) ||
                record.KnownIncompatibleTitles.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException($"Invalid LLE compatibility record for {record.ModuleHash}.");
            }
            var overlap = record.KnownCompatibleTitles.Intersect(
                record.KnownIncompatibleTitles,
                StringComparer.OrdinalIgnoreCase);
            if (overlap.Any())
            {
                throw new InvalidDataException(
                    $"A title cannot be both compatible and incompatible for module {record.ModuleHash}.");
            }

            var key = new CompatibilityKey(
                record.ModuleHash.ToLowerInvariant(),
                record.FirmwareProfileId,
                record.CoreVersion);
            if (!result.TryAdd(key, record))
            {
                throw new InvalidDataException(
                    $"Duplicate LLE compatibility record for {record.ModuleHash}/{record.FirmwareProfileId}/{record.CoreVersion}.");
            }
        }
        return result;
    }

    private static IReadOnlyList<GameModuleResolutionOverride> BuildOverrideList(
        IEnumerable<GameModuleResolutionOverride> overrides)
    {
        var result = new List<GameModuleResolutionOverride>();
        var keys = new HashSet<(string TitleId, string ModuleName)>(
            OverrideKeyComparer.Instance);
        foreach (var item in overrides)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.TitleId);
            ValidateModuleName(item.ModuleName, nameof(overrides));
            if (!Enum.IsDefined(item.Mode))
            {
                throw new InvalidDataException(
                    $"Invalid module override mode for {item.TitleId}/{item.ModuleName}.");
            }
            if (!keys.Add((item.TitleId, item.ModuleName)))
            {
                throw new InvalidDataException(
                    $"Duplicate module resolution override for {item.TitleId}/{item.ModuleName}.");
            }
            result.Add(item);
        }
        return result.ToArray();
    }

    private static void ValidateRequest(ModuleResolutionRequest request)
    {
        ValidateModuleName(request.ModuleName, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FirmwareProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CoreVersion);
        if (!Enum.IsDefined(request.RequestedMode) || request.GameOverrides is null)
        {
            throw new ArgumentException("The resolution mode and game overrides must be valid.", nameof(request));
        }
        foreach (var item in request.GameOverrides)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.TitleId);
            ValidateModuleName(item.ModuleName, nameof(request));
            if (!Enum.IsDefined(item.Mode))
            {
                throw new InvalidDataException($"Invalid module override mode for {item.TitleId}/{item.ModuleName}.");
            }
        }
    }

    private static void ValidateModuleName(string moduleName, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName, parameterName);
        var absolute = moduleName.StartsWith("/", StringComparison.Ordinal);
        if (moduleName.Contains('\\') ||
            moduleName.Contains('\0') ||
            moduleName.Contains("//", StringComparison.Ordinal) ||
            (absolute && (moduleName.Length == 1 || moduleName.EndsWith("/", StringComparison.Ordinal))) ||
            moduleName.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(component => component is "." or "..") ||
            (!absolute && moduleName.Contains('/')))
        {
            throw new InvalidDataException($"Invalid module name or guest path: {moduleName}");
        }
    }

    private static void ValidateSimpleModuleName(string moduleName, string parameterName)
    {
        ValidateModuleName(moduleName, parameterName);
        if (moduleName.StartsWith("/", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"HLE module descriptors require a file name, not a guest path: {moduleName}");
        }
    }

    private readonly record struct CompatibilityKey(string Hash, string FirmwareProfileId, string CoreVersion);

    private sealed class OverrideKeyComparer : IEqualityComparer<(string TitleId, string ModuleName)>
    {
        public static OverrideKeyComparer Instance { get; } = new();

        public bool Equals(
            (string TitleId, string ModuleName) x,
            (string TitleId, string ModuleName) y) =>
            string.Equals(x.TitleId, y.TitleId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ModuleName, y.ModuleName, StringComparison.Ordinal);

        public int GetHashCode((string TitleId, string ModuleName) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.TitleId),
                StringComparer.Ordinal.GetHashCode(value.ModuleName));
    }

    private readonly record struct ModuleLookup(FirmwareModule? Module, bool Ambiguous);

    private readonly record struct LleEvaluation(bool IsEligible, string Reason);
}
