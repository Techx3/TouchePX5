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
        var discovery = await DiscoverCandidatesAsync(
            catalog,
            fileSystem,
            targets.Keys,
            cancellationToken).ConfigureAwait(false);
        var candidates = discovery.Candidates;
        Console.Error.WriteLine(
            $"[FIRMWARE-LLE][INFO] export audit: catalog={catalog.Modules.Count}, " +
            $"eligible={discovery.EligibleModules}, scanned={discovery.ScannedModules}, " +
            $"planned={discovery.PlannedModules}, linked={discovery.LinkedModules}, " +
            $"export_modules={discovery.ModulesWithExports}, exports={discovery.ExportedSymbols}, " +
            $"matches={discovery.MatchedSymbols}, rejected={discovery.RejectedModules}");
        if (IsExportAuditEnabled())
        {
            foreach (var sample in discovery.Samples)
            {
                Console.Error.WriteLine($"[FIRMWARE-LLE][AUDIT] {sample}");
            }
        }
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
        var pending = BuildDependencyClosure(
                uniqueProviders.Values.Select(provider => provider.Candidate),
                discovery.AllCandidates,
                hleSymbols)
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
        if (IsExportAuditEnabled() && pending.Count != 0)
        {
            foreach (var candidate in pending.Take(12))
            {
                var resolution = new HybridImportResolver(hleSymbols, availableLleExports)
                    .Resolve(candidate.LinkPlan, ModuleResolutionMode.Auto);
                var unresolved = resolution.Bindings
                    .Where(binding => binding.Source == ImportBindingSource.Unresolved)
                    .Select(binding => binding.SymbolName)
                    .Take(8)
                    .ToArray();
                Console.Error.WriteLine(
                    $"[FIRMWARE-LLE][AUDIT] deferred={candidate.Module.VirtualPath} " +
                    $"imports={candidate.LinkPlan.ImportedSymbols.Count} " +
                    $"unresolved={resolution.Bindings.Count(binding => binding.Source == ImportBindingSource.Unresolved)} " +
                    $"unsupported={string.Join(',', candidate.LinkPlan.UnsupportedRelocationTypes)} " +
                    $"sample={string.Join(',', unresolved)}");
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

    private static async Task<CandidateDiscovery> DiscoverCandidatesAsync(
        FirmwareModuleCatalog catalog,
        IFirmwareVirtualFileSystem fileSystem,
        IEnumerable<string> targets,
        CancellationToken cancellationToken)
    {
        var targetSet = targets.ToHashSet(StringComparer.Ordinal);
        var result = new List<Candidate>();
        var allCandidates = new List<Candidate>();
        var samples = new List<string>();
        var eligibleModules = catalog.Modules
            .Where(module =>
                module.Format == FirmwareModuleFormat.Elf64 &&
                module.State is (FirmwareModuleState.Parseable or
                    FirmwareModuleState.LleCompatible or
                    FirmwareModuleState.MissingDependencies) &&
                module.HasDynamicTable)
            .OrderBy(module => module.State == FirmwareModuleState.MissingDependencies ? 0 : 1)
            .ThenBy(module => module.VirtualPath, StringComparer.Ordinal)
            .ToArray();
        var eligible = eligibleModules
            .Take(MaximumScannedModules)
            .ToArray();
        var plannedModules = 0;
        var linkedModules = 0;
        var modulesWithExports = 0;
        var exportedSymbols = 0;
        var matchedSymbols = 0;
        var rejectedModules = 0;
        foreach (var module in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var decision = CreateExplicitDecision(module);
                var loadPlan = await new LleModuleLoadPlanner().BuildHybridAsync(
                    decision,
                    catalog,
                    fileSystem,
                    cancellationToken).ConfigureAwait(false);
                plannedModules++;
                var linkPlan = await new LleModuleLinkPlanner().BuildAsync(
                    loadPlan,
                    fileSystem,
                    cancellationToken).ConfigureAwait(false);
                linkedModules++;
                AddSample(
                    samples,
                    $"module={module.VirtualPath} exports={linkPlan.ExportedSymbols.Count} " +
                    $"str=0x{linkPlan.Metadata.StringTableLocation:x}/0x{linkPlan.Metadata.StringTableSize:x} " +
                    $"sym=0x{linkPlan.Metadata.SymbolTableLocation:x}/0x{linkPlan.Metadata.SymbolTableSize:x} " +
                    $"rela=0x{linkPlan.Metadata.RelaLocation:x}/0x{linkPlan.Metadata.RelaSize:x} " +
                    $"jmp=0x{linkPlan.Metadata.ProcedureLinkageLocation:x}/0x{linkPlan.Metadata.ProcedureLinkageSize:x}");
                if (linkPlan.ExportedSymbols.Count != 0)
                {
                    modulesWithExports++;
                    exportedSymbols += linkPlan.ExportedSymbols.Count;
                    AddSample(samples, $"export sample={string.Join(',', linkPlan.ExportedSymbols.Take(4).Select(symbol => symbol.Name))}");
                }
                var moduleMatches = linkPlan.ExportedSymbols.Count(symbol =>
                    targetSet.Contains(symbol.Name) ||
                    targetSet.Contains(GetSonyNid(symbol.Name)));
                matchedSymbols += moduleMatches;
                if (linkPlan.CanApply)
                {
                    var candidate = new Candidate(module, loadPlan, linkPlan);
                    allCandidates.Add(candidate);
                    if (moduleMatches != 0)
                    {
                        result.Add(candidate);
                    }
                }
            }
            catch (Exception exception) when (exception is
                InvalidDataException or
                InvalidOperationException or
                IOException or
                NotSupportedException or
                OverflowException)
            {
                rejectedModules++;
                AddSample(
                    samples,
                    $"rejected={module.VirtualPath} reason={exception.GetType().Name}: {exception.Message}");
            }
        }
        if (samples.Count == 0)
        {
            AddSample(samples, "No module produced an export sample or rejection detail.");
        }
        return new CandidateDiscovery(
            result,
            allCandidates,
            eligibleModules.Length,
            eligible.Length,
            plannedModules,
            linkedModules,
            modulesWithExports,
            exportedSymbols,
            matchedSymbols,
            rejectedModules,
            samples);
    }

    private static IReadOnlyList<Candidate> BuildDependencyClosure(
        IEnumerable<Candidate> roots,
        IReadOnlyList<Candidate> allCandidates,
        IReadOnlyList<HleSymbolDescriptor> hleSymbols)
    {
        var hleNames = hleSymbols
            .Select(symbol => symbol.SymbolName)
            .ToHashSet(StringComparer.Ordinal);
        var selected = roots
            .DistinctBy(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal)
            .ToDictionary(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal);
        var queue = new Queue<Candidate>(selected.Values);
        while (queue.TryDequeue(out var candidate))
        {
            foreach (var import in candidate.LinkPlan.ImportedSymbols)
            {
                if (hleNames.Contains(import.Name) ||
                    hleNames.Contains(GetSonyNid(import.Name)) ||
                    HasUniqueNidProvider(import.Name, selected.Values))
                {
                    continue;
                }
                var importNid = GetSonyNid(import.Name);
                var providers = allCandidates
                    .Where(provider => provider.LinkPlan.ExportedSymbols.Any(export =>
                        string.Equals(GetSonyNid(export.Name), importNid, StringComparison.Ordinal)))
                    .DistinctBy(provider => provider.Module.VirtualPath, StringComparer.Ordinal)
                    .Take(2)
                    .ToArray();
                if (providers.Length != 1 ||
                    !selected.TryAdd(providers[0].Module.VirtualPath, providers[0]))
                {
                    continue;
                }
                queue.Enqueue(providers[0]);
            }
        }
        return selected.Values.ToArray();
    }

    private static bool HasUniqueNidProvider(string symbolName, IEnumerable<Candidate> candidates)
    {
        var nid = GetSonyNid(symbolName);
        return candidates
            .Where(candidate => candidate.LinkPlan.ExportedSymbols.Any(export =>
                string.Equals(GetSonyNid(export.Name), nid, StringComparison.Ordinal)))
            .Select(candidate => candidate.Module.VirtualPath)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() == 1;
    }

    private static bool IsExportAuditEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_FIRMWARE_LLE_EXPORT_AUDIT"),
            "1",
            StringComparison.Ordinal);

    private static void AddSample(ICollection<string> samples, string sample)
    {
        const int maximumSamples = 24;
        if (samples.Count < maximumSamples)
        {
            samples.Add(sample);
        }
    }

    private static Dictionary<string, ProviderSelection> SelectUniqueProviders(
        IReadOnlyList<Candidate> candidates,
        IEnumerable<string> targets,
        out int ambiguousTargets)
    {
        var result = new Dictionary<string, ProviderSelection>(StringComparer.Ordinal);
        ambiguousTargets = 0;
        foreach (var target in targets)
        {
            var providers = candidates
                .SelectMany(candidate => candidate.LinkPlan.ExportedSymbols
                    .Where(symbol => MatchesTarget(symbol.Name, target))
                    .Select(symbol => new ProviderSelection(candidate, symbol.Name)))
                .DistinctBy(
                    provider => (provider.Candidate.Module.VirtualPath, provider.ExportSymbolName))
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
        IReadOnlyDictionary<string, ProviderSelection> uniqueProviders,
        IReadOnlyDictionary<string, HashSet<string>> targets,
        IDictionary<string, ulong> runtimeSymbols)
    {
        foreach (var target in targets)
        {
            if (!uniqueProviders.TryGetValue(target.Key, out var provider) ||
                provider.Candidate.Module.VirtualPath != candidate.Module.VirtualPath)
            {
                continue;
            }
            var export = exports.Single(item =>
                string.Equals(item.SymbolName, provider.ExportSymbolName, StringComparison.Ordinal));
            runtimeSymbols.TryAdd(export.SymbolName, export.RuntimeAddress);
            runtimeSymbols.TryAdd(target.Key, export.RuntimeAddress);
            foreach (var alias in target.Value)
            {
                runtimeSymbols.TryAdd(alias, export.RuntimeAddress);
            }
        }
    }

    private static bool MatchesTarget(string symbolName, string target) =>
        string.Equals(symbolName, target, StringComparison.Ordinal) ||
        string.Equals(GetSonyNid(symbolName), target, StringComparison.Ordinal);

    private static string GetSonyNid(string symbolName)
    {
        var separator = symbolName.IndexOf('#');
        return separator <= 0 ? symbolName : symbolName[..separator];
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

    private sealed record ProviderSelection(Candidate Candidate, string ExportSymbolName);

    private sealed record CandidateDiscovery(
        IReadOnlyList<Candidate> Candidates,
        IReadOnlyList<Candidate> AllCandidates,
        int EligibleModules,
        int ScannedModules,
        int PlannedModules,
        int LinkedModules,
        int ModulesWithExports,
        int ExportedSymbols,
        int MatchedSymbols,
        int RejectedModules,
        IReadOnlyList<string> Samples);
}

internal sealed record FirmwareLleLoadSummary(
    int MissingImports,
    int CandidateModules,
    int LoadedModules,
    int PublishedImports,
    int AmbiguousImports,
    int DeferredModules = 0);
