// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using Touche.PS5.Modules;
using Xunit;

namespace Touche.PS5.Modules.Tests;

public sealed class HybridImportResolverTests
{
    private const string ProfileId = "ps5-extracted-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ModuleHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SymbolName = "exampleNid";

    [Fact]
    public void AutoPrefersCompleteHleSymbolOverMappedLleExport()
    {
        var resolver = CreateResolver(HleImplementationQuality.CompleteStable, includeLle: true);

        var result = resolver.Resolve(CreateLinkPlan());

        var binding = Assert.Single(result.Bindings);
        Assert.Equal(ImportBindingSource.Hle, binding.Source);
        Assert.Equal("dispatch-example", binding.HleDispatchKey);
        Assert.True(result.CanLink);
    }

    [Fact]
    public void AutoPrefersMappedLleExportOverPartialHleSymbol()
    {
        var resolver = CreateResolver(HleImplementationQuality.Partial, includeLle: true);

        var binding = Assert.Single(resolver.Resolve(CreateLinkPlan()).Bindings);

        Assert.Equal(ImportBindingSource.Lle, binding.Source);
        Assert.Equal(0x9000UL, binding.LleRuntimeAddress);
    }

    [Fact]
    public void PreferLleFallsBackToHleWhenNoMappedExportExists()
    {
        var resolver = CreateResolver(HleImplementationQuality.CompleteStable, includeLle: false);

        var binding = Assert.Single(
            resolver.Resolve(CreateLinkPlan(), ModuleResolutionMode.PreferLle).Bindings);

        Assert.Equal(ImportBindingSource.Hle, binding.Source);
        Assert.True(binding.UsedFallback);
    }

    [Fact]
    public void HleOnlyCanSelectControlledStubExplicitly()
    {
        var resolver = CreateResolver(HleImplementationQuality.ControlledStub, includeLle: true);

        var binding = Assert.Single(
            resolver.Resolve(CreateLinkPlan(), ModuleResolutionMode.HleOnly).Bindings);

        Assert.Equal(ImportBindingSource.ControlledStub, binding.Source);
        Assert.False(binding.UsedFallback);
    }

    [Fact]
    public void DecoratedSonyImportUsesBareHleNid()
    {
        var resolver = CreateResolver(HleImplementationQuality.CompleteStable, includeLle: false);

        var binding = Assert.Single(resolver.Resolve(CreateLinkPlan($"{SymbolName}#A#B")).Bindings);

        Assert.Equal(ImportBindingSource.Hle, binding.Source);
        Assert.Equal("dispatch-example", binding.HleDispatchKey);
    }

    [Fact]
    public void ObjectImportUsesDirectHleDataProvider()
    {
        var descriptor = CreateHle(HleImplementationQuality.CompleteStable) with
        {
            SymbolType = 1,
            RuntimeAddress = 0x1234,
        };
        var imported = new LleDynamicSymbol(1, $"{SymbolName}#A#B", 1, 1, 0, 0, 0, 0);
        var plan = CreateLinkPlan(imported.Name) with
        {
            ReferencedSymbols = [imported],
            ImportedSymbols = [imported],
        };

        var binding = Assert.Single(new HybridImportResolver([descriptor]).Resolve(plan).Bindings);

        Assert.Equal(ImportBindingSource.HleData, binding.Source);
        Assert.Equal(0x1234UL, binding.HleDataRuntimeAddress);
        Assert.Null(binding.HleDispatchKey);
    }

    [Fact]
    public void FunctionImportRejectsHleDataProvider()
    {
        var descriptor = CreateHle(HleImplementationQuality.CompleteStable) with
        {
            SymbolType = 1,
            RuntimeAddress = 0x1234,
        };

        var binding = Assert.Single(new HybridImportResolver([descriptor]).Resolve(CreateLinkPlan()).Bindings);

        Assert.Equal(ImportBindingSource.Unresolved, binding.Source);
    }

    [Fact]
    public void DecoratedSonyImportUsesUniqueBareLleNid()
    {
        var descriptor = CreateLle() with
        {
            SymbolName = $"{SymbolName}#B#C",
            RuntimeAddress = 0x1234,
        };
        var resolver = new HybridImportResolver(lleSymbols: [descriptor]);

        var binding = Assert.Single(
            resolver.Resolve(CreateLinkPlan($"{SymbolName}#A#B")).Bindings);

        Assert.Equal(ImportBindingSource.Lle, binding.Source);
        Assert.Equal(0x1234UL, binding.LleRuntimeAddress);
    }

    [Fact]
    public void DecoratedSonyImportRejectsAmbiguousBareLleNid()
    {
        var first = CreateLle() with { SymbolName = $"{SymbolName}#B#C" };
        var second = CreateLle() with
        {
            ModuleVirtualPath = "/system/common/lib/libSecond.sprx",
            ModuleHash = new string('c', 64),
            SymbolName = $"{SymbolName}#C#D",
            RuntimeAddress = 0xa000,
        };
        var resolver = new HybridImportResolver(lleSymbols: [first, second]);

        var binding = Assert.Single(
            resolver.Resolve(CreateLinkPlan($"{SymbolName}#A#B")).Bindings);

        Assert.Equal(ImportBindingSource.Unresolved, binding.Source);
    }

    [Fact]
    public void ContextualSonyImportSelectsMatchingProviderFromAmbiguousNid()
    {
        const string nid = "LwG8g3niqwA";
        var matchingIdentity = new LleSonySymbolIdentity(
            nid, 0, "libkernel", 1, 1, "libkernel", 0x0101);
        var otherIdentity = new LleSonySymbolIdentity(
            nid, 2, "libOther", 1, 3, "libOther", 1);
        var matching = CreateLle() with
        {
            SymbolName = $"{nid}#C#A",
            RuntimeAddress = 0x1234,
            SymbolType = 2,
            SonyIdentity = matchingIdentity,
        };
        var other = CreateLle() with
        {
            ModuleVirtualPath = "/system/common/lib/libOther.sprx",
            ModuleHash = new string('c', 64),
            SymbolName = $"{nid}#D#A",
            RuntimeAddress = 0x5678,
            SymbolType = 2,
            SonyIdentity = otherIdentity,
        };
        var imported = new LleDynamicSymbol(1, $"{nid}#A#B", 1, 2, 0, 0, 0, 0)
        {
            SonyIdentity = matchingIdentity,
        };
        var linkPlan = CreateLinkPlan(imported.Name) with
        {
            ReferencedSymbols = [imported],
            ImportedSymbols = [imported],
        };

        var binding = Assert.Single(
            new HybridImportResolver(lleSymbols: [matching, other]).Resolve(linkPlan).Bindings);

        Assert.Equal(ImportBindingSource.Lle, binding.Source);
        Assert.Equal(0x1234UL, binding.LleRuntimeAddress);
    }

    [Fact]
    public void ContextualSonyImportPrefersCanonicalModuleOverCompatibilityVariant()
    {
        const string nid = "LwG8g3niqwA";
        var identity = new LleSonySymbolIdentity(
            nid, 0, "libkernel", 1, 1, "libkernel", 0x0101);
        var canonical = CreateLle() with
        {
            ModuleVirtualPath = "/lib/libkernel.sprx",
            SymbolName = $"{nid}#I#A",
            RuntimeAddress = 0x1234,
            SymbolType = 2,
            SonyIdentity = identity,
        };
        var compatibility = CreateLle() with
        {
            ModuleVirtualPath = "/lib/libkernel_sys.sprx",
            ModuleHash = new string('c', 64),
            SymbolName = $"{nid}#J#A",
            RuntimeAddress = 0x5678,
            SymbolType = 2,
            SonyIdentity = identity,
        };
        var imported = new LleDynamicSymbol(1, $"{nid}#A#B", 1, 2, 0, 0, 0, 0)
        {
            SonyIdentity = identity,
        };
        var linkPlan = CreateLinkPlan(imported.Name) with
        {
            ReferencedSymbols = [imported],
            ImportedSymbols = [imported],
        };

        var binding = Assert.Single(
            new HybridImportResolver(lleSymbols: [compatibility, canonical]).Resolve(linkPlan).Bindings);

        Assert.Equal(ImportBindingSource.Lle, binding.Source);
        Assert.Equal("/lib/libkernel.sprx", binding.ProviderModule);
        Assert.Equal(0x1234UL, binding.LleRuntimeAddress);
    }

    [Fact]
    public void RepeatedContextualRowsForSameProviderRemainResolvable()
    {
        const string nid = "LwG8g3niqwA";
        var identity = new LleSonySymbolIdentity(
            nid, 0, "libkernel", 1, 1, "libkernel", 0x0101);
        var descriptor = CreateLle() with
        {
            SymbolName = $"{nid}#C#A",
            RuntimeAddress = 0x1234,
            SymbolType = 2,
            SonyIdentity = identity,
        };
        var imported = new LleDynamicSymbol(1, $"{nid}#A#B", 1, 2, 0, 0, 0, 0)
        {
            SonyIdentity = identity,
        };
        var linkPlan = CreateLinkPlan(imported.Name) with
        {
            ReferencedSymbols = [imported],
            ImportedSymbols = [imported],
        };

        var binding = Assert.Single(
            new HybridImportResolver(lleSymbols: [descriptor, descriptor]).Resolve(linkPlan).Bindings);

        Assert.Equal(ImportBindingSource.Lle, binding.Source);
        Assert.Equal(0x1234UL, binding.LleRuntimeAddress);
    }

    [Fact]
    public void LleExportFromDifferentFirmwareProfileIsNotEligible()
    {
        var descriptor = CreateLle() with { FirmwareProfileId = "another-profile" };
        var resolver = new HybridImportResolver(lleSymbols: [descriptor]);

        var result = resolver.Resolve(CreateLinkPlan(), ModuleResolutionMode.LleOnly);

        Assert.Equal(ImportBindingSource.Unresolved, Assert.Single(result.Bindings).Source);
        Assert.False(result.CanLink);
    }

    [Fact]
    public void UnsupportedRelocationsPreventLinkEvenWhenEveryImportResolves()
    {
        var resolver = CreateResolver(HleImplementationQuality.CompleteStable, includeLle: false);
        var linkPlan = CreateLinkPlan() with { UnsupportedRelocationTypes = [37] };

        var result = resolver.Resolve(linkPlan);

        Assert.All(result.Bindings, binding => Assert.NotEqual(ImportBindingSource.Unresolved, binding.Source));
        Assert.False(result.RelocationsSupported);
        Assert.False(result.CanLink);
    }

    [Fact]
    public void DuplicateHleProvidersAreRejected()
    {
        var descriptor = CreateHle(HleImplementationQuality.CompleteStable);

        Assert.Throws<InvalidDataException>(() => new HybridImportResolver(
            hleSymbols: [descriptor, descriptor],
            lleSymbols: []));
    }

    [Fact]
    public void ImportedSymbolMustExistInReferencedSymbolCatalog()
    {
        var resolver = CreateResolver(HleImplementationQuality.CompleteStable, includeLle: false);
        var linkPlan = CreateLinkPlan() with { ReferencedSymbols = [] };

        Assert.Throws<InvalidDataException>(() => resolver.Resolve(linkPlan));
    }

    private static HybridImportResolver CreateResolver(
        HleImplementationQuality hleQuality,
        bool includeLle) => new(
            [CreateHle(hleQuality)],
            includeLle ? [CreateLle()] : []);

    private static HleSymbolDescriptor CreateHle(HleImplementationQuality quality) => new(
        "libExample.sprx",
        SymbolName,
        "dispatch-example",
        quality);

    private static LleExportDescriptor CreateLle() => new(
        ProfileId,
        "/system/common/lib/libProvider.sprx",
        ModuleHash,
        SymbolName,
        0x9000,
        16);

    private static LleModuleLinkPlan CreateLinkPlan(string symbolName = SymbolName) => new()
    {
        FirmwareProfileId = ProfileId,
        ModuleVirtualPath = "/system/common/lib/libConsumer.sprx",
        ModuleHash = ModuleHash,
        Metadata = new LleDynamicLinkMetadata(0, 0, 0, 0, 0, 0, 0, 0),
        Relocations = [],
        ReferencedSymbols =
        [
            new LleDynamicSymbol(1, symbolName, 1, 2, 0, 0, 0, 0),
        ],
        ImportedSymbols =
        [
            new LleDynamicSymbol(1, symbolName, 1, 2, 0, 0, 0, 0),
        ],
        UnsupportedRelocationTypes = [],
    };
}
