// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Touche.PS5.Modules;
using Xunit;

namespace Touche.PS5.Modules.Tests;

public sealed class LleModuleLinkerTests
{
    private const string ProfileId = "ps5-extracted-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ModulePath = "/system/common/lib/libConsumer.sprx";
    private const string ModuleHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task MaterializesHleImportAndCommitsRelativeRelocations()
    {
        var factory = new RecordingLinkTransactionFactory(hleAddress: 0xf000);
        var imported = CreateImportedSymbol();
        var linkPlan = CreateLinkPlan(
            [imported],
            [
                new LleRelocation(LleRelocationTableKind.ProcedureLinkage, 0x1100, 1, 7, 0),
                new LleRelocation(LleRelocationTableKind.Rela, 0x1108, 0, 8, 0x1234),
            ]);
        var resolution = CreateResolution(
            new ImportBindingDecision
            {
                SymbolIndex = 1,
                SymbolName = imported.Name,
                Source = ImportBindingSource.Hle,
                ProviderModule = "libHle",
                HleDispatchKey = "dispatch-nid",
                ReasonCode = "test.hle",
                Reason = "Test HLE binding.",
            });

        var result = await new LleModuleLinker().LinkAsync(
            CreateLoadPlan(),
            CreateMappedModule(),
            linkPlan,
            resolution,
            factory);

        Assert.True(factory.Transaction.Committed);
        Assert.False(factory.Transaction.RolledBack);
        var thunk = Assert.Single(factory.Transaction.Thunks);
        Assert.Equal("dispatch-nid", thunk.DispatchKey);
        Assert.False(thunk.ControlledStub);
        Assert.Equal(0xf000UL, Assert.Single(result.Imports).RuntimeAddress);
        Assert.Equal(2, factory.Transaction.Writes.Count);
        Assert.Equal(0xf000UL, ReadUInt64(factory.Transaction.Writes[0].Data));
        Assert.Equal(0x8234UL, ReadUInt64(factory.Transaction.Writes[1].Data));
        Assert.Equal([0x8100UL, 0x8108UL], result.Relocations.Select(item => item.RuntimeAddress));
    }

    [Fact]
    public async Task AppliesPcRelativeBindingToMappedLleExport()
    {
        var factory = new RecordingLinkTransactionFactory();
        var imported = CreateImportedSymbol();
        var linkPlan = CreateLinkPlan(
            [imported],
            [new LleRelocation(LleRelocationTableKind.Rela, 0x1100, 1, 2, 0)]);
        var resolution = CreateResolution(
            new ImportBindingDecision
            {
                SymbolIndex = 1,
                SymbolName = imported.Name,
                Source = ImportBindingSource.Lle,
                ProviderModule = "/system/common/lib/libProvider.sprx",
                LleRuntimeAddress = 0x9000,
                ReasonCode = "test.lle",
                Reason = "Test LLE binding.",
            });

        var result = await new LleModuleLinker().LinkAsync(
            CreateLoadPlan(),
            CreateMappedModule(),
            linkPlan,
            resolution,
            factory);

        Assert.Empty(factory.Transaction.Thunks);
        Assert.Equal(0xf00, BinaryPrimitives.ReadInt32LittleEndian(Assert.Single(factory.Transaction.Writes).Data));
        Assert.Equal(0xf00UL, Assert.Single(result.Relocations).EncodedValue);
    }

    [Fact]
    public async Task RollsBackThunksAndWritesWhenStagingFails()
    {
        var factory = new RecordingLinkTransactionFactory(hleAddress: 0xf000, failOnWrite: 2);
        var imported = CreateImportedSymbol();
        var linkPlan = CreateLinkPlan(
            [imported],
            [
                new LleRelocation(LleRelocationTableKind.ProcedureLinkage, 0x1100, 1, 7, 0),
                new LleRelocation(LleRelocationTableKind.Rela, 0x1108, 0, 8, 0x1000),
            ]);

        await Assert.ThrowsAsync<IOException>(() => new LleModuleLinker().LinkAsync(
            CreateLoadPlan(),
            CreateMappedModule(),
            linkPlan,
            CreateResolution(CreateHleBinding(imported)),
            factory));

        Assert.False(factory.Transaction.Committed);
        Assert.True(factory.Transaction.RolledBack);
        Assert.Empty(factory.Transaction.Thunks);
        Assert.Empty(factory.Transaction.Writes);
    }

    [Fact]
    public async Task RejectsMismatchedBindingsBeforeOpeningTransaction()
    {
        var factory = new RecordingLinkTransactionFactory();
        var imported = CreateImportedSymbol();
        var invalidBinding = CreateHleBinding(imported) with { SymbolName = "another-symbol" };

        await Assert.ThrowsAsync<InvalidDataException>(() => new LleModuleLinker().LinkAsync(
            CreateLoadPlan(),
            CreateMappedModule(),
            CreateLinkPlan([imported], []),
            CreateResolution(invalidBinding),
            factory));

        Assert.Equal(0, factory.BeginCount);
    }

    [Fact]
    public async Task RejectsOverlappingRelocationWritesBeforeOpeningTransaction()
    {
        var factory = new RecordingLinkTransactionFactory();
        var linkPlan = CreateLinkPlan(
            [],
            [
                new LleRelocation(LleRelocationTableKind.Rela, 0x1100, 0, 8, 0x1000),
                new LleRelocation(LleRelocationTableKind.Rela, 0x1104, 0, 8, 0x2000),
            ]);

        await Assert.ThrowsAsync<InvalidDataException>(() => new LleModuleLinker().LinkAsync(
            CreateLoadPlan(),
            CreateMappedModule(),
            linkPlan,
            CreateResolution(),
            factory));

        Assert.Equal(0, factory.BeginCount);
    }

    private static ImportBindingDecision CreateHleBinding(LleDynamicSymbol symbol) => new()
    {
        SymbolIndex = symbol.Index,
        SymbolName = symbol.Name,
        Source = ImportBindingSource.Hle,
        ProviderModule = "libHle",
        HleDispatchKey = "dispatch-nid",
        ReasonCode = "test.hle",
        Reason = "Test HLE binding.",
    };

    private static LleDynamicSymbol CreateImportedSymbol() =>
        new(1, "exampleNid", 1, 2, 0, 0, 0, 0);

    private static LleModuleLoadPlan CreateLoadPlan() => new()
    {
        FirmwareProfileId = ProfileId,
        ModuleVirtualPath = ModulePath,
        ModuleHash = ModuleHash,
        ElfType = 3,
        EntryPoint = 0x1010,
        ImageVirtualStart = 0x1000,
        ImageSize = 0x1000,
        HasDynamicTable = true,
        DynamicTable = new LleDynamicTable(1, 0x200, 0x80, 0x1200, 0x80),
        Segments =
        [
            new LleLoadSegment(0, 0, 0x1000, 0x1000, 0x1000, 0x1000, LleSegmentPermissions.Read),
        ],
    };

    private static LleMappedModule CreateMappedModule() => new()
    {
        FirmwareProfileId = ProfileId,
        ModuleVirtualPath = ModulePath,
        ModuleHash = ModuleHash,
        RuntimeImageStart = 0x8000,
        ImageVirtualStart = 0x1000,
        RuntimeEntryPoint = 0x8010,
        ImageSize = 0x1000,
        Segments =
        [
            new LleMappedSegment(0, 0x8000, 0x1000, LleSegmentPermissions.Read),
        ],
    };

    private static LleModuleLinkPlan CreateLinkPlan(
        IReadOnlyList<LleDynamicSymbol> symbols,
        IReadOnlyList<LleRelocation> relocations) => new()
        {
            FirmwareProfileId = ProfileId,
            ModuleVirtualPath = ModulePath,
            ModuleHash = ModuleHash,
            Metadata = new LleDynamicLinkMetadata(0, 0, 0, 0, 0, 0, 0, 0),
            Relocations = relocations,
            ReferencedSymbols = symbols,
            ImportedSymbols = symbols.Where(symbol => symbol.IsUndefined).ToArray(),
            UnsupportedRelocationTypes = [],
        };

    private static ModuleImportResolutionPlan CreateResolution(
        params ImportBindingDecision[] bindings) => new()
        {
            FirmwareProfileId = ProfileId,
            ModuleVirtualPath = ModulePath,
            ModuleHash = ModuleHash,
            Mode = ModuleResolutionMode.Auto,
            RelocationsSupported = true,
            Bindings = bindings,
        };

    private static ulong ReadUInt64(byte[] bytes) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes);

    private sealed class RecordingLinkTransactionFactory(
        ulong hleAddress = 0xf000,
        int failOnWrite = 0) : ILleGuestLinkTransactionFactory
    {
        public int BeginCount { get; private set; }

        public RecordingLinkTransaction Transaction { get; } = new(hleAddress, failOnWrite);

        public ValueTask<ILleGuestLinkTransaction> BeginAsync(
            string moduleVirtualPath,
            ulong runtimeImageStart,
            ulong imageSize,
            CancellationToken cancellationToken = default)
        {
            BeginCount++;
            return ValueTask.FromResult<ILleGuestLinkTransaction>(Transaction);
        }
    }

    private sealed class RecordingLinkTransaction(ulong hleAddress, int failOnWrite)
        : ILleGuestLinkTransaction
    {
        private int _writeAttempts;

        public List<StagedThunk> Thunks { get; } = [];

        public List<StagedWrite> Writes { get; } = [];

        public bool Committed { get; private set; }

        public bool RolledBack { get; private set; }

        public ValueTask<ulong> StageHleThunkAsync(
            string dispatchKey,
            bool controlledStub,
            CancellationToken cancellationToken = default)
        {
            Thunks.Add(new StagedThunk(dispatchKey, controlledStub));
            return ValueTask.FromResult(hleAddress);
        }

        public ValueTask StageWriteAsync(
            ulong runtimeAddress,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            _writeAttempts++;
            if (_writeAttempts == failOnWrite)
            {
                throw new IOException("Injected relocation write failure.");
            }
            Writes.Add(new StagedWrite(runtimeAddress, data.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            Committed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!Committed)
            {
                Thunks.Clear();
                Writes.Clear();
                RolledBack = true;
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed record StagedThunk(string DispatchKey, bool ControlledStub);

    private sealed record StagedWrite(ulong RuntimeAddress, byte[] Data);
}
