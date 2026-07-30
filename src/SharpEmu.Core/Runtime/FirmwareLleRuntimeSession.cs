// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Touche.Firmware;
using Touche.PS5.Modules;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Experimental hybrid provider session. It only publishes firmware exports
/// for imports that have no HLE implementation in the active registry.
/// </summary>
internal sealed class FirmwareLleRuntimeSession : IDisposable
{
    private const int MaximumScannedModules = 512;
    private const ulong ModuleArenaStart = 0x0000_6800_0000_0000;
    private const ulong ModuleAlignment = 0x1_0000;

    private readonly string _storeRoot;
    private readonly string _profileId;
    private readonly IModuleManager _moduleManager;
    private readonly ISymbolCatalog _symbolCatalog;
    private readonly CoreLleGuestMemoryTransactionFactory _memoryTransactions;
    private readonly CoreLleGuestLinkTransactionFactory _linkTransactions;
    private bool _loaded;
    private bool _disposed;

    public FirmwareLleRuntimeSession(
        string storeRoot,
        string profileId,
        IVirtualMemory memory,
        IModuleManager moduleManager,
        ISymbolCatalog symbolCatalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        _storeRoot = Path.GetFullPath(storeRoot);
        _profileId = profileId;
        _moduleManager = moduleManager ?? throw new ArgumentNullException(nameof(moduleManager));
        _symbolCatalog = symbolCatalog ?? throw new ArgumentNullException(nameof(symbolCatalog));
        _memoryTransactions = new CoreLleGuestMemoryTransactionFactory(memory);
        _linkTransactions = new CoreLleGuestLinkTransactionFactory(memory);
    }

    public async Task<FirmwareLleLoadSummary> LoadMissingProvidersAsync(
        SelfImage image,
        IDictionary<ulong, string> importStubs,
        IDictionary<string, ulong> runtimeSymbols,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(importStubs);
        ArgumentNullException.ThrowIfNull(runtimeSymbols);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_loaded)
        {
            throw new InvalidOperationException("The firmware LLE session has already been loaded.");
        }
        _loaded = true;

        var targets = BuildMissingTargetIndex(image.ImportStubs.Values);
        if (targets.Count == 0)
        {
            return new FirmwareLleLoadSummary(0, 0, 0, 0, 0);
        }

        var repository = new FirmwareProfileRepository(_storeRoot);
        var catalog = repository.GetModuleCatalog(_profileId);
        var fileSystem = FirmwareVirtualFileSystem.Mount(_storeRoot, _profileId);
        var candidates = await DiscoverCandidatesAsync(
            catalog,
            fileSystem,
            targets.Keys,
            cancellationToken).ConfigureAwait(false);
        var uniqueProviders = SelectUniqueProviders(candidates, targets.Keys, out var ambiguousTargets);
        if (uniqueProviders.Count == 0)
        {
            return new FirmwareLleLoadSummary(
                targets.Values.SelectMany(value => value).Distinct(StringComparer.Ordinal).Count(),
                candidates.Count,
                0,
                0,
                ambiguousTargets);
        }

        var hleSymbols = new HleImportCatalogAdapter().CreateDescriptors(
            _moduleManager,
            defaultQuality: HleImplementationQuality.Partial,
            target: Generation.Gen5);
        var availableLleExports = new List<LleExportDescriptor>();
        var pending = uniqueProviders.Values
            .DistinctBy(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal)
            .OrderBy(candidate => candidate.Module.Dependencies.Count)
            .ThenBy(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal)
            .ToList();
        var loadedModules = 0;
        var publishedImports = 0;
        var cursor = ModuleArenaStart;
        var progress = true;
        while (pending.Count != 0 && progress)
        {
            progress = false;
            foreach (var candidate in pending.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolution = new HybridImportResolver(hleSymbols, availableLleExports)
                    .Resolve(candidate.LinkPlan, ModuleResolutionMode.Auto);
                if (!resolution.CanLink)
                {
                    continue;
                }

                var runtimeImageStart = checked(
                    AlignUp(cursor, ModuleAlignment) +
                    (candidate.LoadPlan.ImageVirtualStart & 0xfff));
                LleMappedModule? mapped = null;
                try
                {
                    mapped = await new LleModuleMapper().MapAsync(
                        candidate.LoadPlan,
                        runtimeImageStart,
                        fileSystem,
                        _memoryTransactions,
                        cancellationToken).ConfigureAwait(false);
                    var linked = await new LleModuleLinker().LinkAsync(
                        candidate.LoadPlan,
                        mapped,
                        candidate.LinkPlan,
                        resolution,
                        _linkTransactions,
                        cancellationToken).ConfigureAwait(false);
                    var exports = new LleExportCatalogAdapter().CreateDescriptors(
                        candidate.LoadPlan,
                        mapped,
                        candidate.LinkPlan);
                    availableLleExports.AddRange(exports);
                    PublishProviderExports(candidate, exports, uniqueProviders, targets, runtimeSymbols);
                    foreach (var import in linked.Imports.Where(import => import.HleDispatchKey is not null))
                    {
                        importStubs[import.RuntimeAddress] = import.HleDispatchKey!;
                    }
                    cursor = AlignUp(checked(runtimeImageStart + candidate.LoadPlan.ImageSize), ModuleAlignment);
                    loadedModules++;
                    publishedImports += exports.Count(export => runtimeSymbols.ContainsKey(export.SymbolName));
                    pending.Remove(candidate);
                    progress = true;
                }
                catch (Exception exception) when (exception is
                    InvalidDataException or
                    InvalidOperationException or
                    IOException or
                    NotSupportedException or
                    OverflowException)
                {
                    if (mapped is not null)
                    {
                        _ = _linkTransactions.TryReleaseModule(
                            mapped.ModuleVirtualPath,
                            mapped.RuntimeImageStart);
                        _ = _memoryTransactions.TryReleaseModule(
                            mapped.ModuleVirtualPath,
                            mapped.RuntimeImageStart);
                    }
                    Console.Error.WriteLine(
                        $"[FIRMWARE-LLE][WARN] Provider rejected: {candidate.Module.VirtualPath} " +
                        $"({exception.GetType().Name}: {exception.Message})");
                    pending.Remove(candidate);
                }
            }
        }

        return new FirmwareLleLoadSummary(
            targets.Values.SelectMany(value => value).Distinct(StringComparer.Ordinal).Count(),
            candidates.Count,
            loadedModules,
            publishedImports,
            ambiguousTargets,
            pending.Count);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _linkTransactions.Dispose();
        _memoryTransactions.Dispose();
    }

    private Dictionary<string, HashSet<string>> BuildMissingTargetIndex(IEnumerable<string> importNids)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var nid in importNids.Where(nid => !string.IsNullOrWhiteSpace(nid)).Distinct(StringComparer.Ordinal))
        {
            if (_moduleManager.TryGetExport(nid, out _))
            {
                continue;
            }
            AddTarget(result, nid, nid);
            if (_symbolCatalog.TryGetByNid(nid, out var symbol) && !string.IsNullOrWhiteSpace(symbol.ExportName))
            {
                AddTarget(result, symbol.ExportName, nid);
            }
        }
        return result;
    }

    private static void AddTarget(
        IDictionary<string, HashSet<string>> targets,
        string symbolName,
        string nid)
    {
        if (!targets.TryGetValue(symbolName, out var aliases))
        {
            aliases = new HashSet<string>(StringComparer.Ordinal);
            targets.Add(symbolName, aliases);
        }
        aliases.Add(nid);
    }

    private static async Task<IReadOnlyList<Candidate>> DiscoverCandidatesAsync(
        FirmwareModuleCatalog catalog,
        IFirmwareVirtualFileSystem fileSystem,
        IEnumerable<string> targets,
        CancellationToken cancellationToken)
    {
        var targetSet = targets.ToHashSet(StringComparer.Ordinal);
        var result = new List<Candidate>();
        var eligible = catalog.Modules
            .Where(module =>
                module.Format == FirmwareModuleFormat.Elf64 &&
                module.State is FirmwareModuleState.Parseable or FirmwareModuleState.LleCompatible &&
                module.HasDynamicTable)
            .OrderBy(module => module.VirtualPath, StringComparer.Ordinal)
            .Take(MaximumScannedModules)
            .ToArray();
        foreach (var module in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var decision = CreateExplicitDecision(module);
                var loadPlan = await new LleModuleLoadPlanner().BuildAsync(
                    decision,
                    catalog,
                    fileSystem,
                    cancellationToken).ConfigureAwait(false);
                var linkPlan = await new LleModuleLinkPlanner().BuildAsync(
                    loadPlan,
                    fileSystem,
                    cancellationToken).ConfigureAwait(false);
                if (linkPlan.CanApply && linkPlan.ExportedSymbols.Any(symbol => targetSet.Contains(symbol.Name)))
                {
                    result.Add(new Candidate(module, loadPlan, linkPlan));
                }
            }
            catch (Exception exception) when (exception is
                InvalidDataException or
                InvalidOperationException or
                IOException or
                NotSupportedException or
                OverflowException)
            {
                // A malformed or unsupported module is simply ineligible as a provider.
            }
        }
        return result;
    }

    private static Dictionary<string, Candidate> SelectUniqueProviders(
        IReadOnlyList<Candidate> candidates,
        IEnumerable<string> targets,
        out int ambiguousTargets)
    {
        var result = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        ambiguousTargets = 0;
        foreach (var target in targets)
        {
            var providers = candidates
                .Where(candidate => candidate.LinkPlan.ExportedSymbols.Any(symbol =>
                    string.Equals(symbol.Name, target, StringComparison.Ordinal)))
                .DistinctBy(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (providers.Length == 1)
            {
                result[target] = providers[0];
            }
            else if (providers.Length > 1)
            {
                ambiguousTargets++;
            }
        }
        return result;
    }

    private static void PublishProviderExports(
        Candidate candidate,
        IReadOnlyList<LleExportDescriptor> exports,
        IReadOnlyDictionary<string, Candidate> uniqueProviders,
        IReadOnlyDictionary<string, HashSet<string>> targets,
        IDictionary<string, ulong> runtimeSymbols)
    {
        foreach (var export in exports)
        {
            if (!uniqueProviders.TryGetValue(export.SymbolName, out var provider) ||
                provider.Module.VirtualPath != candidate.Module.VirtualPath ||
                !targets.TryGetValue(export.SymbolName, out var aliases))
            {
                continue;
            }
            runtimeSymbols.TryAdd(export.SymbolName, export.RuntimeAddress);
            foreach (var alias in aliases)
            {
                runtimeSymbols.TryAdd(alias, export.RuntimeAddress);
            }
        }
    }

    private static ModuleResolutionDecision CreateExplicitDecision(FirmwareModule module) => new()
    {
        ModuleName = Path.GetFileName(module.VirtualPath),
        RequestedMode = ModuleResolutionMode.LleOnly,
        EffectiveMode = ModuleResolutionMode.LleOnly,
        SelectedImplementation = ModuleImplementationKind.Lle,
        ModuleVirtualPath = module.VirtualPath,
        ModuleHash = module.Sha256,
        OverrideApplied = false,
        UsedFallback = false,
        ReasonCode = "module.lle.explicit-missing-provider",
        Reason = "The user enabled a verified firmware profile for a missing HLE provider.",
    };

    private static ulong AlignUp(ulong value, ulong alignment) =>
        checked((value + alignment - 1) & ~(alignment - 1));

    private sealed record Candidate(
        FirmwareModule Module,
        LleModuleLoadPlan LoadPlan,
        LleModuleLinkPlan LinkPlan);
}

internal sealed record FirmwareLleLoadSummary(
    int MissingImports,
    int CandidateModules,
    int LoadedModules,
    int PublishedImports,
    int AmbiguousImports,
    int DeferredModules = 0);
