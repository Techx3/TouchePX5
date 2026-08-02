// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Runtime;
using SharpEmu.HLE;
using Touche.PS5.Modules;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class HleImportCatalogAdapterTests
{
    [Fact]
    public void CreatesNidAndUniqueNameDescriptorsForTargetGeneration()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
        [
            CreateExport("libExample", "nid-gen5", "example", Generation.Gen5),
            CreateExport("libLegacy", "nid-gen4", "legacy", Generation.Gen4),
        ]);

        var descriptors = new HleImportCatalogAdapter().CreateDescriptors(
            manager,
            [new HleModuleDescriptor("libExample.sprx", HleImplementationQuality.CompleteStable)]);

        Assert.Equal(2, descriptors.Count);
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal("libExample", descriptor.ModuleName);
            Assert.Equal("nid-gen5", descriptor.DispatchKey);
            Assert.Equal(HleImplementationQuality.CompleteStable, descriptor.Quality);
        });
        Assert.Equal(["example", "nid-gen5"], descriptors.Select(descriptor => descriptor.SymbolName));
    }

    [Fact]
    public void OmitsAmbiguousNamesButKeepsEveryNid()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
        [
            CreateExport("libOne", "nid-one", "sharedName", Generation.Gen5),
            CreateExport("libTwo", "nid-two", "sharedName", Generation.Gen5),
        ]);

        var descriptors = new HleImportCatalogAdapter().CreateDescriptors(manager);

        Assert.Equal(["nid-one", "nid-two"], descriptors.Select(descriptor => descriptor.SymbolName));
        Assert.All(descriptors, descriptor => Assert.Equal(HleImplementationQuality.Partial, descriptor.Quality));
    }

    [Fact]
    public void CreatesDirectDescriptorsForKnownHleDataSymbols()
    {
        var descriptors = new HleImportCatalogAdapter().CreateDataDescriptors();

        Assert.Contains(descriptors, descriptor => descriptor.SymbolName == "ZT4ODD2Ts9o");
        Assert.All(descriptors, descriptor =>
        {
            Assert.Equal((byte)1, descriptor.SymbolType);
            Assert.NotNull(descriptor.RuntimeAddress);
            Assert.NotEqual(0UL, descriptor.RuntimeAddress!.Value);
            Assert.Equal(HleImplementationQuality.CompleteStable, descriptor.Quality);
        });
    }

    [Fact]
    public void RejectsDuplicateNormalizedModuleQualities()
    {
        var manager = new ModuleManager();

        Assert.Throws<InvalidDataException>(() => new HleImportCatalogAdapter().CreateDescriptors(
            manager,
            [
                new HleModuleDescriptor("libExample", HleImplementationQuality.Partial),
                new HleModuleDescriptor("LIBEXAMPLE.sprx", HleImplementationQuality.CompleteStable),
            ]));
    }

    [Fact]
    public void RegistrySnapshotIsDeterministicAndDetached()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
        [
            CreateExport("libTwo", "nid-two", "two", Generation.Gen5),
            CreateExport("libOne", "nid-one", "one", Generation.Gen5),
        ]);

        var first = manager.GetExports();
        manager.RegisterExports([CreateExport("libThree", "nid-three", "three", Generation.Gen5)]);

        Assert.Equal(["nid-one", "nid-two"], first.Select(export => export.Nid));
        Assert.Equal(["nid-one", "nid-three", "nid-two"], manager.GetExports().Select(export => export.Nid));
    }

    private static ExportedFunction CreateExport(
        string libraryName,
        string nid,
        string name,
        Generation target) => new(libraryName, nid, name, target, static _ => 0);
}
