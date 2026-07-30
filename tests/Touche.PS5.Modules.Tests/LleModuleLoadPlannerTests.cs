// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Touche.Firmware;
using Touche.PS5.Modules;
using Xunit;

namespace Touche.PS5.Modules.Tests;

public sealed class LleModuleLoadPlannerTests : IDisposable
{
    private const string ModulePath = "/system/common/lib/libExample.sprx";
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"touche-lle-plan-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task BuildsVerifiedPlanWithoutMappingOrExecutingModule()
    {
        var imported = await ImportAsync(CreateElf());
        var module = Assert.Single(imported.Result.ModuleCatalog!.Modules);
        var decision = CreateDecision(module);
        var fileSystem = FirmwareVirtualFileSystem.Mount(
            imported.Store,
            imported.Result.Manifest.ProfileId);

        var plan = await new LleModuleLoadPlanner().BuildAsync(
            decision,
            imported.Result.ModuleCatalog,
            fileSystem);

        Assert.Equal(ModulePath, plan.ModuleVirtualPath);
        Assert.Equal(module.Sha256, plan.ModuleHash);
        Assert.Equal(3, plan.ElfType);
        Assert.Equal(0x1010UL, plan.EntryPoint);
        Assert.Equal(0x1000UL, plan.ImageVirtualStart);
        Assert.Equal(0x300UL, plan.ImageSize);
        Assert.False(plan.HasDynamicTable);
        var segment = Assert.Single(plan.Segments);
        Assert.Equal(0x200UL, segment.FileSize);
        Assert.Equal(0x300UL, segment.MemorySize);
        Assert.Equal(LleSegmentPermissions.Read | LleSegmentPermissions.Execute, segment.Permissions);
    }

    [Fact]
    public async Task RejectsCataloguedSegmentThatExceedsVerifiedObject()
    {
        var bytes = CreateElf();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64 + 32), 0x400);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64 + 40), 0x400);
        var imported = await ImportAsync(bytes);
        var artifact = Assert.Single(imported.Result.Manifest.Artifacts);
        var catalog = CreateTrustedCatalog(
            imported.Result.Manifest.ProfileId,
            artifact,
            programHeaderCount: 1,
            hasDynamicTable: false);
        var fileSystem = FirmwareVirtualFileSystem.Mount(imported.Store, catalog.ProfileId);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LleModuleLoadPlanner().BuildAsync(
                CreateDecision(Assert.Single(catalog.Modules)),
                catalog,
                fileSystem));

        Assert.Contains("exceeds", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsDecisionThatWasNotResolvedAsLle()
    {
        var imported = await ImportAsync(CreateElf());
        var catalog = imported.Result.ModuleCatalog!;
        var module = Assert.Single(catalog.Modules);
        var fileSystem = FirmwareVirtualFileSystem.Mount(imported.Store, catalog.ProfileId);
        var decision = CreateDecision(module) with
        {
            SelectedImplementation = ModuleImplementationKind.Hle,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LleModuleLoadPlanner().BuildAsync(decision, catalog, fileSystem));
    }

    [Fact]
    public async Task RejectsFileSystemMountedFromDifferentProfile()
    {
        var imported = await ImportAsync(CreateElf());
        var otherBytes = CreateElf();
        otherBytes[^1] = 1;
        var other = await ImportAsync(otherBytes);
        var catalog = imported.Result.ModuleCatalog!;
        var otherFileSystem = FirmwareVirtualFileSystem.Mount(
            other.Store,
            other.Result.Manifest.ProfileId);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LleModuleLoadPlanner().BuildAsync(
                CreateDecision(Assert.Single(catalog.Modules)),
                catalog,
                otherFileSystem));

        Assert.Contains("profile", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DetectsCasTamperingBeforeParsingPlan()
    {
        var imported = await ImportAsync(CreateElf());
        var artifact = Assert.Single(imported.Result.Manifest.Artifacts);
        var objectPath = Path.Combine(
            imported.Store,
            "objects",
            artifact.Sha256[..2],
            artifact.Sha256);
        var bytes = File.ReadAllBytes(objectPath);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(objectPath, bytes);
        var catalog = imported.Result.ModuleCatalog!;
        var fileSystem = FirmwareVirtualFileSystem.Mount(imported.Store, catalog.ProfileId);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LleModuleLoadPlanner().BuildAsync(
                CreateDecision(Assert.Single(catalog.Modules)),
                catalog,
                fileSystem));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    private async Task<(string Store, FirmwareImportResult Result)> ImportAsync(byte[] bytes)
    {
        var source = Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N"), "source");
        var store = Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N"), "store");
        var file = Path.Combine(source, "system", "common", "lib", "libExample.sprx");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
        var result = await new FirmwareDirectoryImporter(store).ImportAsync(source);
        return (store, result);
    }

    private static byte[] CreateElf()
    {
        var bytes = new byte[0x200];
        bytes[0] = 0x7f;
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'L';
        bytes[3] = (byte)'F';
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 62);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 0x1010);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 1);

        var programHeader = bytes.AsSpan(64, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(programHeader[4..], 5);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[8..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[16..], 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[32..], 0x200);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[40..], 0x300);
        BinaryPrimitives.WriteUInt64LittleEndian(programHeader[48..], 0x1000);
        return bytes;
    }

    private static FirmwareModuleCatalog CreateTrustedCatalog(
        string profileId,
        FirmwareArtifact artifact,
        int programHeaderCount,
        bool hasDynamicTable) => new()
        {
            ProfileId = profileId,
            ContentHash = new string('a', 64),
            Modules =
            [
                new FirmwareModule(
                    artifact.VirtualPath,
                    artifact.Sha256,
                    FirmwareModuleFormat.Elf64,
                    FirmwareModuleState.Parseable,
                    "x86-64",
                    0x1010,
                    programHeaderCount,
                    hasDynamicTable,
                    [],
                    null),
            ],
        };

    private static ModuleResolutionDecision CreateDecision(FirmwareModule module) => new()
    {
        ModuleName = Path.GetFileName(module.VirtualPath),
        RequestedMode = ModuleResolutionMode.LleOnly,
        EffectiveMode = ModuleResolutionMode.LleOnly,
        SelectedImplementation = ModuleImplementationKind.Lle,
        ModuleVirtualPath = module.VirtualPath,
        ModuleHash = module.Sha256,
        OverrideApplied = false,
        UsedFallback = false,
        ReasonCode = "module.lle.compatible",
        Reason = "Test-compatible module.",
    };

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
