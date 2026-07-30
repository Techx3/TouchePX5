// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using Touche.Firmware;
using Touche.PS5.Modules;
using Xunit;

namespace Touche.PS5.Modules.Tests;

public sealed class HybridModuleResolverTests
{
    private const string ProfileId = "ps5-extracted-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CoreVersion = "0.0.3";
    private const string ModuleHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void AutoPrefersCompleteHleOverCompatibleLle()
    {
        var resolver = CreateResolver(HleImplementationQuality.CompleteStable, compatible: true);

        var decision = resolver.Resolve(CreateRequest(ModuleResolutionMode.Auto));

        Assert.Equal(ModuleImplementationKind.Hle, decision.SelectedImplementation);
        Assert.Equal("module.hle.complete", decision.ReasonCode);
        Assert.False(decision.UsedFallback);
    }

    [Fact]
    public void AutoPrefersCompatibleLleOverPartialHle()
    {
        var resolver = CreateResolver(HleImplementationQuality.Partial, compatible: true);

        var decision = resolver.Resolve(CreateRequest(ModuleResolutionMode.Auto));

        Assert.Equal(ModuleImplementationKind.Lle, decision.SelectedImplementation);
        Assert.Equal(ModuleHash, decision.ModuleHash);
    }

    [Fact]
    public void PreferLleFallsBackToHleForIncompatibleTitle()
    {
        var resolver = CreateResolver(
            HleImplementationQuality.CompleteStable,
            compatible: true,
            incompatibleTitles: ["PPSA00001"]);

        var decision = resolver.Resolve(CreateRequest(ModuleResolutionMode.PreferLle, "PPSA00001"));

        Assert.Equal(ModuleImplementationKind.Hle, decision.SelectedImplementation);
        Assert.True(decision.UsedFallback);
        Assert.Equal(ModuleResolutionMode.PreferLle, decision.EffectiveMode);
    }

    [Fact]
    public void LleOnlyRejectsModuleWithMissingDependencies()
    {
        var resolver = CreateResolver(
            hleQuality: null,
            compatible: true,
            moduleState: FirmwareModuleState.MissingDependencies);

        var decision = resolver.Resolve(CreateRequest(ModuleResolutionMode.LleOnly));

        Assert.Equal(ModuleImplementationKind.Unresolved, decision.SelectedImplementation);
        Assert.Contains("MissingDependencies", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void GameOverrideChangesEffectiveModeWithoutChangingRequestedMode()
    {
        var resolver = CreateResolver(HleImplementationQuality.Partial, compatible: true);
        var request = CreateRequest(ModuleResolutionMode.Auto, "PPSA00002") with
        {
            GameOverrides =
            [
                new GameModuleResolutionOverride(
                    "PPSA00002",
                    "libExample.sprx",
                    ModuleResolutionMode.LleOnly),
            ],
        };

        var decision = resolver.Resolve(request);

        Assert.Equal(ModuleResolutionMode.Auto, decision.RequestedMode);
        Assert.Equal(ModuleResolutionMode.LleOnly, decision.EffectiveMode);
        Assert.True(decision.OverrideApplied);
        Assert.Equal(ModuleImplementationKind.Lle, decision.SelectedImplementation);
    }

    [Fact]
    public void AutoUsesControlledStubBeforeLeavingImportUnresolved()
    {
        var withStub = CreateResolver(HleImplementationQuality.ControlledStub, compatible: false);
        var withoutStub = CreateResolver(hleQuality: null, compatible: false);

        Assert.Equal(
            ModuleImplementationKind.Stub,
            withStub.Resolve(CreateRequest(ModuleResolutionMode.Auto)).SelectedImplementation);
        Assert.Equal(
            ModuleImplementationKind.Unresolved,
            withoutStub.Resolve(CreateRequest(ModuleResolutionMode.Auto)).SelectedImplementation);
    }

    [Fact]
    public void AmbiguousFileNameRequiresAbsoluteGuestPath()
    {
        var catalog = CreateCatalog(
            CreateModule("/system/common/lib/libExample.sprx"),
            CreateModule("/system/priv/lib/libExample.sprx"));
        var resolver = new HybridModuleResolver(catalog, [], [CreateCompatibility()]);

        var ambiguous = resolver.Resolve(CreateRequest(ModuleResolutionMode.LleOnly));
        var exact = resolver.Resolve(CreateRequest(ModuleResolutionMode.LleOnly) with
        {
            ModuleName = "/system/common/lib/libExample.sprx",
        });

        Assert.Equal(ModuleImplementationKind.Unresolved, ambiguous.SelectedImplementation);
        Assert.Contains("absolute guest path", ambiguous.Reason, StringComparison.Ordinal);
        Assert.Equal(ModuleImplementationKind.Lle, exact.SelectedImplementation);
    }

    [Fact]
    public void DuplicateCompatibilityRecordsAreRejected()
    {
        var record = CreateCompatibility();
        var duplicate = record with { ModuleHash = record.ModuleHash.ToUpperInvariant() };

        Assert.Throws<InvalidDataException>(() => new HybridModuleResolver(
            CreateCatalog(CreateModule("/system/lib/libExample.sprx")),
            [],
            [record, duplicate]));
    }

    [Fact]
    public void ResolveManyRejectsNullRequests()
    {
        var resolver = CreateResolver(HleImplementationQuality.CompleteStable, compatible: true);

        Assert.Throws<ArgumentNullException>(() => resolver.ResolveMany(null!));
    }

    [Theory]
    [InlineData("/system//lib/example.sprx")]
    [InlineData("/system/../lib/example.sprx")]
    [InlineData("system/lib/example.sprx")]
    [InlineData("/")]
    public void UnsafeOrNonCanonicalModuleNamesAreRejected(string moduleName)
    {
        var resolver = CreateResolver(HleImplementationQuality.CompleteStable, compatible: true);

        Assert.Throws<InvalidDataException>(() => resolver.Resolve(
            CreateRequest(ModuleResolutionMode.Auto) with { ModuleName = moduleName }));
    }

    private static HybridModuleResolver CreateResolver(
        HleImplementationQuality? hleQuality,
        bool compatible,
        FirmwareModuleState moduleState = FirmwareModuleState.Parseable,
        IReadOnlyList<string>? incompatibleTitles = null)
    {
        var hle = hleQuality is null
            ? Array.Empty<HleModuleDescriptor>()
            : [new HleModuleDescriptor("libExample.sprx", hleQuality.Value)];
        var compatibility = compatible
            ?
            [
                CreateCompatibility(incompatibleTitles),
            ]
            : Array.Empty<LleCompatibilityRecord>();
        return new HybridModuleResolver(
            CreateCatalog(CreateModule("/system/common/lib/libExample.sprx", moduleState)),
            hle,
            compatibility);
    }

    private static FirmwareModuleCatalog CreateCatalog(params FirmwareModule[] modules) => new()
    {
        ProfileId = ProfileId,
        ContentHash = new string('c', 64),
        Modules = modules,
    };

    private static FirmwareModule CreateModule(
        string path,
        FirmwareModuleState state = FirmwareModuleState.Parseable) => new(
            path,
            ModuleHash,
            FirmwareModuleFormat.Elf64,
            state,
            "x86-64",
            0,
            0,
            false,
            [],
            null);

    private static LleCompatibilityRecord CreateCompatibility(
        IReadOnlyList<string>? incompatibleTitles = null) => new()
        {
            ModuleHash = ModuleHash,
            FirmwareProfileId = ProfileId,
            CoreVersion = CoreVersion,
            Status = LleCompatibilityStatus.Compatible,
            KnownIncompatibleTitles = incompatibleTitles ?? [],
        };

    private static ModuleResolutionRequest CreateRequest(
        ModuleResolutionMode mode,
        string? titleId = null) => new()
        {
            ModuleName = "libExample.sprx",
            RequestedMode = mode,
            TitleId = titleId,
            FirmwareProfileId = ProfileId,
            CoreVersion = CoreVersion,
        };
}
