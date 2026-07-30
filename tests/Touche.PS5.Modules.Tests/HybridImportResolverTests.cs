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

    private static LleModuleLinkPlan CreateLinkPlan() => new()
    {
        FirmwareProfileId = ProfileId,
        ModuleVirtualPath = "/system/common/lib/libConsumer.sprx",
        ModuleHash = ModuleHash,
        Metadata = new LleDynamicLinkMetadata(0, 0, 0, 0, 0, 0, 0, 0),
        Relocations = [],
        ReferencedSymbols =
        [
            new LleDynamicSymbol(1, SymbolName, 1, 2, 0, 0, 0, 0),
        ],
        ImportedSymbols =
        [
            new LleDynamicSymbol(1, SymbolName, 1, 2, 0, 0, 0, 0),
        ],
        UnsupportedRelocationTypes = [],
    };
}
