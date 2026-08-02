// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Core.Runtime;
using Touche.PS5.Modules;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GnmDiagnosticExportsTests
{
    [Theory]
    [InlineData("HRyNHoAjb6E", "sceGnmIsCoredumpValid")]
    [InlineData("O-7nHKgcNSQ", "sceGnmGetCoredumpProtectionFaultTimestamp")]
    public void Gen5RegistryExposesConservativeCoredumpQueries(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceGnmDriver", export.LibraryName);
    }

    [Fact]
    public void FirmwareSanitizerFallbackIsRegisteredAsControlledStub()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("iCzcQSfuX-E", out var export));
        Assert.Equal("libSceDbgThreadSanitizer", export.LibraryName);

        var descriptor = Assert.Single(new HleImportCatalogAdapter().CreateDescriptors(
            manager,
            [new HleModuleDescriptor(
                "libSceDbgThreadSanitizer",
                HleImplementationQuality.ControlledStub)]),
            item => item.SymbolName == "iCzcQSfuX-E");
        Assert.Equal(HleImplementationQuality.ControlledStub, descriptor.Quality);
    }
}
