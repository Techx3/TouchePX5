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
        Assert.Equal(imported.Result.Manifest.ProfileId, plan.FirmwareProfileId);
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
    public async Task CatalogsVerifiedDynamicRelocationsWithoutApplyingThem()
    {
        var imported = await ImportAsync(CreateDynamicElf(relocationType: 8));
        var catalog = imported.Result.ModuleCatalog!;
        var module = Assert.Single(catalog.Modules);
        var fileSystem = FirmwareVirtualFileSystem.Mount(imported.Store, catalog.ProfileId);
        var loadPlan = await new LleModuleLoadPlanner().BuildAsync(
            CreateDecision(module),
            catalog,
            fileSystem);

        var linkPlan = await new LleModuleLinkPlanner().BuildAsync(loadPlan, fileSystem);

        Assert.NotNull(loadPlan.DynamicTable);
        Assert.Equal(0x1300UL, linkPlan.Metadata.RelaLocation);
        Assert.Equal(24UL, linkPlan.Metadata.RelaSize);
        var relocation = Assert.Single(linkPlan.Relocations);
        Assert.Equal(0x1100UL, relocation.TargetVirtualAddress);
        Assert.Equal(1U, relocation.SymbolIndex);
        Assert.Equal(8U, relocation.Type);
        Assert.True(linkPlan.CanApply);
    }

    [Fact]
    public async Task ReportsUnsupportedRelocationsBeforeMemoryIsModified()
    {
        var imported = await ImportAsync(CreateDynamicElf(relocationType: 37));
        var catalog = imported.Result.ModuleCatalog!;
        var module = Assert.Single(catalog.Modules);
        var fileSystem = FirmwareVirtualFileSystem.Mount(imported.Store, catalog.ProfileId);
        var loadPlan = await new LleModuleLoadPlanner().BuildAsync(
            CreateDecision(module),
            catalog,
            fileSystem);

        var linkPlan = await new LleModuleLinkPlanner().BuildAsync(loadPlan, fileSystem);

        Assert.False(linkPlan.CanApply);
        Assert.Equal([37U], linkPlan.UnsupportedRelocationTypes);
    }

    [Fact]
    public async Task MapsEverySegmentAndCommitsOnlyAfterSuccessfulStaging()
    {
        var imported = await ImportAsync(CreateElf());
        var catalog = imported.Result.ModuleCatalog!;
        var module = Assert.Single(catalog.Modules);
        var fileSystem = FirmwareVirtualFileSystem.Mount(imported.Store, catalog.ProfileId);
        var plan = await new LleModuleLoadPlanner().BuildAsync(
            CreateDecision(module),
            catalog,
            fileSystem);
        var factory = new RecordingTransactionFactory();

        var mapped = await new LleModuleMapper().MapAsync(
            plan,
            runtimeImageStart: 0x8000,
            fileSystem,
            factory);

        Assert.True(factory.Transaction.Committed);
        Assert.False(factory.Transaction.RolledBack);
        Assert.Equal(0x8010UL, mapped.RuntimeEntryPoint);
        Assert.Equal(0x8000UL, Assert.Single(mapped.Segments).RuntimeAddress);
        var staged = Assert.Single(factory.Transaction.Segments);
        Assert.Equal(0x200, staged.InitialData.Length);
        Assert.Equal(0x300UL, staged.MemorySize);
    }

    [Fact]
    public async Task RollsBackTransactionWhenSegmentStagingFails()
    {
        var imported = await ImportAsync(CreateElf());
        var catalog = imported.Result.ModuleCatalog!;
        var module = Assert.Single(catalog.Modules);
        var fileSystem = FirmwareVirtualFileSystem.Mount(imported.Store, catalog.ProfileId);
        var plan = await new LleModuleLoadPlanner().BuildAsync(
            CreateDecision(module),
            catalog,
            fileSystem);
        var factory = new RecordingTransactionFactory(failDuringStage: true);

        await Assert.ThrowsAsync<IOException>(() => new LleModuleMapper().MapAsync(
            plan,
            runtimeImageStart: 0x8000,
            fileSystem,
            factory));

        Assert.False(factory.Transaction.Committed);
        Assert.True(factory.Transaction.RolledBack);
        Assert.Empty(factory.Transaction.Segments);
    }

    [Fact]
    public async Task RollsBackStagedSegmentsWhenCommitFails()
    {
        var imported = await ImportAsync(CreateElf());
        var catalog = imported.Result.ModuleCatalog!;
        var module = Assert.Single(catalog.Modules);
        var fileSystem = FirmwareVirtualFileSystem.Mount(imported.Store, catalog.ProfileId);
        var plan = await new LleModuleLoadPlanner().BuildAsync(
            CreateDecision(module),
            catalog,
            fileSystem);
        var factory = new RecordingTransactionFactory(failDuringCommit: true);

        await Assert.ThrowsAsync<IOException>(() => new LleModuleMapper().MapAsync(
            plan,
            runtimeImageStart: 0x8000,
            fileSystem,
            factory));

        Assert.False(factory.Transaction.Committed);
        Assert.True(factory.Transaction.RolledBack);
        Assert.Empty(factory.Transaction.Segments);
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

    private static byte[] CreateDynamicElf(uint relocationType)
    {
        var bytes = new byte[0x400];
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
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 2);

        var load = bytes.AsSpan(64, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(load, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(load[4..], 5);
        BinaryPrimitives.WriteUInt64LittleEndian(load[8..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(load[16..], 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(load[32..], 0x400);
        BinaryPrimitives.WriteUInt64LittleEndian(load[40..], 0x500);
        BinaryPrimitives.WriteUInt64LittleEndian(load[48..], 0x1000);

        var dynamicHeader = bytes.AsSpan(120, 56);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(dynamicHeader[4..], 4);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[8..], 0x200);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[16..], 0x1200);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[32..], 64);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[40..], 64);
        BinaryPrimitives.WriteUInt64LittleEndian(dynamicHeader[48..], 8);

        WriteDynamicEntry(bytes, 0x200, 7, 0x1300);
        WriteDynamicEntry(bytes, 0x210, 8, 24);
        WriteDynamicEntry(bytes, 0x220, 9, 24);
        WriteDynamicEntry(bytes, 0x230, 0, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x300), 0x1100);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(0x308),
            ((ulong)1 << 32) | relocationType);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(0x310), 4);
        return bytes;
    }

    private static void WriteDynamicEntry(byte[] bytes, int offset, long tag, ulong value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), tag);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8), value);
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

    private sealed class RecordingTransactionFactory(
        bool failDuringStage = false,
        bool failDuringCommit = false)
        : ILleGuestMemoryTransactionFactory
    {
        public RecordingTransaction Transaction { get; } = new(failDuringStage, failDuringCommit);

        public ValueTask<ILleGuestMemoryTransaction> BeginAsync(
            string moduleVirtualPath,
            ulong runtimeImageStart,
            ulong imageSize,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ILleGuestMemoryTransaction>(Transaction);
    }

    private sealed class RecordingTransaction(bool failDuringStage, bool failDuringCommit)
        : ILleGuestMemoryTransaction
    {
        public List<StagedSegment> Segments { get; } = [];

        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public ValueTask StageSegmentAsync(
            ulong runtimeAddress,
            ulong memorySize,
            ulong sourceFileOffset,
            ReadOnlyMemory<byte> initialData,
            LleSegmentPermissions finalPermissions,
            CancellationToken cancellationToken = default)
        {
            if (failDuringStage)
            {
                throw new IOException("Injected staging failure.");
            }
            Segments.Add(new StagedSegment(
                runtimeAddress,
                memorySize,
                initialData.ToArray(),
                finalPermissions));
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            if (failDuringCommit)
            {
                throw new IOException("Injected commit failure.");
            }
            Committed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!Committed)
            {
                Segments.Clear();
                RolledBack = true;
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record StagedSegment(
        ulong RuntimeAddress,
        ulong MemorySize,
        byte[] InitialData,
        LleSegmentPermissions Permissions);

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
