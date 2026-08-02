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
    private const int MaximumScannedModules = 1024;
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
            foreach (var candidate in discovery.AllCandidates.Where(candidate =>
                         IsCoreExportDiagnosticModule(candidate.Module.VirtualPath)))
            {
                var exports = candidate.LinkPlan.ExportedSymbols
                    .Take(12)
                    .Select(symbol =>
                    {
                        var context = symbol.SonyIdentity is null
                            ? "unknown"
                            : $"{symbol.SonyIdentity.LibraryName}@{symbol.SonyIdentity.LibraryVersion:x4}/" +
                              $"{symbol.SonyIdentity.ModuleName}@{symbol.SonyIdentity.ModuleVersion:x4}";
                        return $"{symbol.Name}[type={symbol.Type},ctx={context}]";
                    });
                Console.Error.WriteLine(
                    $"[FIRMWARE-LLE][AUDIT] core={candidate.Module.VirtualPath} " +
                    $"imports={candidate.LinkPlan.ImportedSymbols.Count} " +
                    $"exports={candidate.LinkPlan.ExportedSymbols.Count} " +
                    $"sample={string.Join(',', exports)}");
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

        var hleCatalog = new HleImportCatalogAdapter();
        var hleSymbols = hleCatalog.CreateDescriptors(
                _moduleManager,
                [new HleModuleDescriptor(
                    "libSceDbgThreadSanitizer",
                    HleImplementationQuality.ControlledStub)],
                defaultQuality: HleImplementationQuality.Partial,
                target: Generation.Gen5)
            .Concat(hleCatalog.CreateDataDescriptors())
            .OrderBy(symbol => symbol.SymbolName, StringComparer.Ordinal)
            .ToArray();
        var selectedCandidates = BuildDependencyClosure(
                uniqueProviders.Values.Select(provider => provider.Candidate),
                discovery.AllCandidates,
                hleSymbols)
            .OrderBy(candidate => candidate.Module.Dependencies.Count)
            .ThenBy(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal)
            .ToList();
        var mappedCandidates = new List<MappedCandidate>(selectedCandidates.Count);
        var cursor = ModuleArenaStart;
        var nextTlsModuleId = 1U;
        foreach (var candidate in selectedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                mapped = mapped with { TlsModuleId = nextTlsModuleId++ };
                var exports = new LleExportCatalogAdapter().CreateDescriptors(
                    candidate.LoadPlan,
                    mapped,
                    candidate.LinkPlan);
                mappedCandidates.Add(new MappedCandidate(candidate, mapped, exports));
                cursor = AlignUp(checked(runtimeImageStart + candidate.LoadPlan.ImageSize), ModuleAlignment);
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
                    ReleaseMappedModule(mapped);
                }
                Console.Error.WriteLine(
                    $"[FIRMWARE-LLE][WARN] Provider mapping rejected: {candidate.Module.VirtualPath} " +
                    $"({exception.GetType().Name}: {exception.Message})");
            }
        }

        var availableLleExports = mappedCandidates.SelectMany(item => item.Exports).ToArray();
        var resolver = new HybridImportResolver(hleSymbols, availableLleExports);
        var resolutions = mappedCandidates.ToDictionary(
            item => item.Candidate.Module.VirtualPath,
            item => resolver.Resolve(item.Candidate.LinkPlan, ModuleResolutionMode.Auto),
            StringComparer.Ordinal);
        var viablePaths = resolutions
            .Where(item => item.Value.CanLink)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        PruneUnavailableLleDependencies(viablePaths, resolutions, "pre-link");

        var linkedCandidates = new Dictionary<string, LinkedCandidate>(StringComparer.Ordinal);
        foreach (var item in mappedCandidates)
        {
            var path = item.Candidate.Module.VirtualPath;
            if (!viablePaths.Contains(path))
            {
                ReleaseMappedModule(item.MappedModule);
                continue;
            }
            try
            {
                var linked = await new LleModuleLinker().LinkAsync(
                    item.Candidate.LoadPlan,
                    item.MappedModule,
                    item.Candidate.LinkPlan,
                    resolutions[path],
                    _linkTransactions,
                    cancellationToken).ConfigureAwait(false);
                linkedCandidates.Add(path, new LinkedCandidate(item, linked));
            }
            catch (Exception exception) when (exception is
                InvalidDataException or
                InvalidOperationException or
                IOException or
                NotSupportedException or
                OverflowException)
            {
                ReleaseMappedModule(item.MappedModule);
                Console.Error.WriteLine(
                    $"[FIRMWARE-LLE][WARN] Provider link rejected: {path} " +
                    $"({exception.GetType().Name}: {exception.Message})");
            }
        }

        var linkedPaths = linkedCandidates.Keys.ToHashSet(StringComparer.Ordinal);
        PruneUnavailableLleDependencies(linkedPaths, resolutions, "post-link");
        foreach (var path in linkedCandidates.Keys.Where(path => !linkedPaths.Contains(path)).ToArray())
        {
            ReleaseMappedModule(linkedCandidates[path].Mapped.MappedModule);
            linkedCandidates.Remove(path);
        }

        var publishedImports = 0;
        foreach (var item in linkedCandidates.Values)
        {
            PublishProviderExports(
                item.Mapped.Candidate,
                item.Mapped.Exports,
                uniqueProviders,
                targets,
                runtimeSymbols);
            foreach (var import in item.Linked.Imports.Where(import => import.HleDispatchKey is not null))
            {
                importStubs[import.RuntimeAddress] = import.HleDispatchKey!;
            }
            publishedImports += item.Mapped.Exports.Count(export => runtimeSymbols.ContainsKey(export.SymbolName));
        }

        var pending = selectedCandidates
            .Where(candidate => !linkedCandidates.ContainsKey(candidate.Module.VirtualPath))
            .ToList();
        var finalLleExports = linkedCandidates.Values.SelectMany(item => item.Mapped.Exports).ToArray();
        if (IsExportAuditEnabled() && pending.Count != 0)
        {
            foreach (var candidate in pending
                         .OrderBy(candidate => GetDiagnosticRank(candidate.Module.VirtualPath))
                         .ThenBy(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal)
                         .Take(24))
            {
                var resolution = resolutions.TryGetValue(candidate.Module.VirtualPath, out var initialResolution)
                    ? initialResolution
                    : new HybridImportResolver(hleSymbols, finalLleExports)
                        .Resolve(candidate.LinkPlan, ModuleResolutionMode.Auto);
                var unresolved = resolution.Bindings
                    .Where(binding => binding.Source == ImportBindingSource.Unresolved)
                    .Select(binding =>
                    {
                        var symbol = candidate.LinkPlan.ImportedSymbols.Single(item => item.Index == binding.SymbolIndex);
                        var context = symbol.SonyIdentity is null
                            ? "unknown"
                            : $"{symbol.SonyIdentity.LibraryName}@{symbol.SonyIdentity.LibraryVersion:x4}/" +
                              $"{symbol.SonyIdentity.ModuleName}@{symbol.SonyIdentity.ModuleVersion:x4}";
                        var providers = availableLleExports
                            .Where(export => ExportMatchesImport(export, symbol))
                            .Select(export => $"{export.ModuleVirtualPath}@0x{export.RuntimeAddress:x}")
                            .Distinct(StringComparer.Ordinal)
                            .Take(4);
                        return $"{binding.SymbolName}[type={symbol.Type},bind={symbol.Binding},ctx={context}," +
                               $"providers={string.Join('|', providers)}]";
                    })
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
            linkedCandidates.Count,
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
            .OrderBy(module => module.State == FirmwareModuleState.MissingDependencies ? 1 : 0)
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
                if (IsExportAuditEnabled() && IsCoreExportDiagnosticModule(module.VirtualPath))
                {
                    Console.Error.WriteLine(
                        $"[FIRMWARE-LLE][AUDIT] planned-core={module.VirtualPath} " +
                        $"imports={linkPlan.ImportedSymbols.Count} exports={linkPlan.ExportedSymbols.Count} " +
                        $"relocations={linkPlan.Relocations.Count} " +
                        $"unsupported={string.Join(',', linkPlan.UnsupportedRelocationTypes)} " +
                        $"str=0x{linkPlan.Metadata.StringTableLocation:x}/0x{linkPlan.Metadata.StringTableSize:x} " +
                        $"sym=0x{linkPlan.Metadata.SymbolTableLocation:x}/0x{linkPlan.Metadata.SymbolTableSize:x}");
                }
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
            .Select(symbol => (symbol.SymbolName, symbol.SymbolType))
            .ToHashSet();
        var selected = roots
            .DistinctBy(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal)
            .ToDictionary(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal);
        var queue = new Queue<Candidate>(selected.Values);
        while (queue.TryDequeue(out var candidate))
        {
            foreach (var import in candidate.LinkPlan.ImportedSymbols)
            {
                var expectedHleType = import.Type == 1 ? (byte)1 : (byte)2;
                if (hleNames.Contains((import.Name, expectedHleType)) ||
                    hleNames.Contains((GetSonyNid(import.Name), expectedHleType)) ||
                    FindSelectedProvider(import, selected.Values) is not null)
                {
                    continue;
                }
                var provider = FindSelectedProvider(import, allCandidates);
                if (provider is null ||
                    !selected.TryAdd(provider.Module.VirtualPath, provider))
                {
                    continue;
                }
                queue.Enqueue(provider);
            }
        }
        return selected.Values.ToArray();
    }

    private static Candidate? FindSelectedProvider(
        LleDynamicSymbol import,
        IEnumerable<Candidate> candidates)
    {
        var available = candidates.ToArray();
        var exact = available.Where(candidate => candidate.LinkPlan.ExportedSymbols.Any(export =>
            export.Type == import.Type &&
            string.Equals(export.Name, import.Name, StringComparison.Ordinal)));
        var contextual = import.SonyIdentity is null
            ? []
            : available.Where(candidate => candidate.LinkPlan.ExportedSymbols.Any(export =>
                export.Type == import.Type && SonyContextsMatch(import.SonyIdentity, export.SonyIdentity)));
        var bareNid = available.Where(candidate => candidate.LinkPlan.ExportedSymbols.Any(export =>
            export.Type == import.Type &&
            string.Equals(GetSonyNid(export.Name), GetSonyNid(import.Name), StringComparison.Ordinal)));
        return SelectPreferredCandidate(exact)
            ?? SelectPreferredCandidate(contextual)
            ?? SelectPreferredCandidate(bareNid);
    }

    private static bool SonyContextsMatch(
        LleSonySymbolIdentity expected,
        LleSonySymbolIdentity? candidate) =>
        candidate is not null &&
        string.Equals(expected.Nid, candidate.Nid, StringComparison.Ordinal) &&
        string.Equals(expected.LibraryName, candidate.LibraryName, StringComparison.Ordinal) &&
        expected.LibraryVersion == candidate.LibraryVersion &&
        string.Equals(expected.ModuleName, candidate.ModuleName, StringComparison.Ordinal) &&
        expected.ModuleVersion == candidate.ModuleVersion;

    private static bool ExportMatchesImport(
        LleExportDescriptor export,
        LleDynamicSymbol import) =>
        export.SymbolType == import.Type &&
        (string.Equals(export.SymbolName, import.Name, StringComparison.Ordinal) ||
         (import.SonyIdentity is not null && SonyContextsMatch(import.SonyIdentity, export.SonyIdentity)) ||
         string.Equals(GetSonyNid(export.SymbolName), GetSonyNid(import.Name), StringComparison.Ordinal));

    private static Candidate? SelectPreferredCandidate(IEnumerable<Candidate> candidates)
    {
        var ranked = candidates
            .DistinctBy(candidate => candidate.Module.VirtualPath, StringComparer.Ordinal)
            .Select(candidate => (Candidate: candidate, Rank: GetProviderRank(candidate)))
            .OrderBy(item => item.Rank.State)
            .ThenBy(item => item.Rank.Dependencies)
            .ThenBy(item => item.Rank.Location)
            .ThenBy(item => item.Candidate.Module.VirtualPath, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (ranked.Length == 0)
        {
            return null;
        }
        return ranked.Length == 1 || ranked[0].Rank != ranked[1].Rank
            ? ranked[0].Candidate
            : null;
    }

    private static (int State, int Dependencies, int Canonical, int Location) GetProviderRank(Candidate candidate) => (
        candidate.Module.State switch
        {
            FirmwareModuleState.LleCompatible => 0,
            FirmwareModuleState.Parseable => 1,
            _ => 2,
        },
        candidate.Module.Dependencies.Count,
        IsCanonicalProviderModule(candidate) ? 0 : 1,
        candidate.Module.VirtualPath.StartsWith("/system/common/lib/", StringComparison.Ordinal) ? 0 : 1);

    private static bool IsCanonicalProviderModule(Candidate candidate)
    {
        var fileName = Path.GetFileNameWithoutExtension(candidate.Module.VirtualPath);
        return candidate.LinkPlan.ExportedSymbols.Any(symbol =>
            symbol.SonyIdentity is not null &&
            string.Equals(
                symbol.SonyIdentity.ModuleName,
                fileName,
                StringComparison.Ordinal));
    }

    private static int GetDiagnosticRank(string virtualPath) => virtualPath switch
    {
        "/system/common/lib/libkernel.sprx" => 0,
        "/system/common/lib/libSceLibcInternal.sprx" => 1,
        "/lib/libkernel.sprx" => 2,
        "/lib/libkernel_sys.sprx" => 3,
        "/lib/libSceLibcInternal.sprx" => 4,
        "/lib/libSceAgcDriver.sprx" => 5,
        _ when virtualPath.StartsWith("/system/common/lib/", StringComparison.Ordinal) => 6,
        _ => 7,
    };

    private static bool IsCoreExportDiagnosticModule(string virtualPath) =>
        GetDiagnosticRank(virtualPath) < 5 ||
        virtualPath.Contains("libSceGnmDriver", StringComparison.Ordinal);

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
                .Select(provider => (Provider: provider, Rank: GetProviderRank(provider.Candidate)))
                .OrderBy(item => item.Rank.State)
                .ThenBy(item => item.Rank.Dependencies)
                .ThenBy(item => item.Rank.Location)
                .ThenBy(item => item.Provider.Candidate.Module.VirtualPath, StringComparer.Ordinal)
                .ThenBy(item => item.Provider.ExportSymbolName, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (providers.Length == 1)
            {
                result[target] = providers[0].Provider;
            }
            else if (providers.Length > 1 && providers[0].Rank != providers[1].Rank)
            {
                result[target] = providers[0].Provider;
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

    private static void PruneUnavailableLleDependencies(
        HashSet<string> availablePaths,
        IReadOnlyDictionary<string, ModuleImportResolutionPlan> resolutions,
        string phase)
    {
        bool removed;
        do
        {
            removed = false;
            foreach (var path in availablePaths.ToArray())
            {
                if (!resolutions.TryGetValue(path, out var resolution))
                {
                    availablePaths.Remove(path);
                    removed = true;
                    if (IsExportAuditEnabled())
                    {
                        Console.Error.WriteLine(
                            $"[FIRMWARE-LLE][AUDIT] prune phase={phase} module={path} reason=no-resolution");
                    }
                    continue;
                }

                var unavailable = resolution.Bindings.FirstOrDefault(binding =>
                    binding.Source == ImportBindingSource.Lle &&
                    (binding.ProviderModule is null ||
                     !availablePaths.Contains(binding.ProviderModule)));
                if (unavailable is not null)
                {
                    availablePaths.Remove(path);
                    removed = true;
                    if (IsExportAuditEnabled())
                    {
                        Console.Error.WriteLine(
                            $"[FIRMWARE-LLE][AUDIT] prune phase={phase} module={path} " +
                            $"symbol={unavailable.SymbolName} provider={unavailable.ProviderModule ?? "<none>"}");
                    }
                }
            }
        }
        while (removed);
    }

    private void ReleaseMappedModule(LleMappedModule mappedModule)
    {
        _ = _linkTransactions.TryReleaseModule(
            mappedModule.ModuleVirtualPath,
            mappedModule.RuntimeImageStart);
        _ = _memoryTransactions.TryReleaseModule(
            mappedModule.ModuleVirtualPath,
            mappedModule.RuntimeImageStart);
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

    private sealed record MappedCandidate(
        Candidate Candidate,
        LleMappedModule MappedModule,
        IReadOnlyList<LleExportDescriptor> Exports);

    private sealed record LinkedCandidate(
        MappedCandidate Mapped,
        LleLinkedModule Linked);

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
