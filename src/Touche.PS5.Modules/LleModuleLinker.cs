// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace Touche.PS5.Modules;

/// <summary>
/// Materializes resolved imports and applies verified ELF64 RELA records through
/// a core-provided transaction. No write survives unless the whole link commits.
/// </summary>
public sealed class LleModuleLinker
{
    private const ushort ShnAbsolute = 0xfff1;

    public async Task<LleLinkedModule> LinkAsync(
        LleModuleLoadPlan loadPlan,
        LleMappedModule mappedModule,
        LleModuleLinkPlan linkPlan,
        ModuleImportResolutionPlan resolutionPlan,
        ILleGuestLinkTransactionFactory transactionFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadPlan);
        ArgumentNullException.ThrowIfNull(mappedModule);
        ArgumentNullException.ThrowIfNull(linkPlan);
        ArgumentNullException.ThrowIfNull(resolutionPlan);
        ArgumentNullException.ThrowIfNull(transactionFactory);
        ValidatePlans(loadPlan, mappedModule, linkPlan, resolutionPlan);

        var relocationTargets = ValidateRelocationTargets(loadPlan, mappedModule, linkPlan.Relocations);
        await using var transaction = await transactionFactory.BeginAsync(
            mappedModule.ModuleVirtualPath,
            mappedModule.RuntimeImageStart,
            mappedModule.ImageSize,
            cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            throw new InvalidOperationException("The guest memory core returned no link transaction.");
        }

        var imports = await MaterializeImportsAsync(
            resolutionPlan.Bindings,
            transaction,
            cancellationToken).ConfigureAwait(false);
        var importAddresses = imports.ToDictionary(imported => imported.SymbolIndex, imported => imported.RuntimeAddress);
        var symbols = linkPlan.ReferencedSymbols.ToDictionary(symbol => symbol.Index);
        var patches = new List<RelocationPatch>(linkPlan.Relocations.Count);
        foreach (var item in relocationTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var patch = CreatePatch(loadPlan, mappedModule, item.Relocation, item.RuntimeAddress, symbols, importAddresses);
            if (patch is not null)
            {
                patches.Add(patch);
            }
        }

        foreach (var patch in patches.OrderBy(patch => patch.RuntimeAddress))
        {
            await transaction.StageWriteAsync(
                patch.RuntimeAddress,
                patch.Data,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new LleLinkedModule
        {
            FirmwareProfileId = mappedModule.FirmwareProfileId,
            ModuleVirtualPath = mappedModule.ModuleVirtualPath,
            ModuleHash = mappedModule.ModuleHash,
            RuntimeImageStart = mappedModule.RuntimeImageStart,
            Imports = imports,
            Relocations = patches
                .OrderBy(patch => patch.RuntimeAddress)
                .Select(patch => new LleAppliedRelocation(
                    patch.RuntimeAddress,
                    patch.Type,
                    patch.Data.Length,
                    patch.EncodedValue))
                .ToArray(),
        };
    }

    private static async Task<IReadOnlyList<LleMaterializedImport>> MaterializeImportsAsync(
        IReadOnlyList<ImportBindingDecision> bindings,
        ILleGuestLinkTransaction transaction,
        CancellationToken cancellationToken)
    {
        var imports = new List<LleMaterializedImport>(bindings.Count);
        foreach (var binding in bindings.OrderBy(binding => binding.SymbolIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong address;
            switch (binding.Source)
            {
                case ImportBindingSource.HleData:
                    address = binding.HleDataRuntimeAddress!.Value;
                    break;
                case ImportBindingSource.Lle:
                    address = binding.LleRuntimeAddress!.Value;
                    break;
                case ImportBindingSource.Hle:
                case ImportBindingSource.ControlledStub:
                    address = await transaction.StageHleThunkAsync(
                        binding.HleDispatchKey!,
                        binding.Source == ImportBindingSource.ControlledStub,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Import '{binding.SymbolName}' is unresolved.");
            }
            if (address == 0)
            {
                throw new InvalidDataException($"Import '{binding.SymbolName}' resolved to address zero.");
            }
            imports.Add(new LleMaterializedImport(
                binding.SymbolIndex,
                binding.SymbolName,
                binding.Source,
                address,
                binding.HleDispatchKey));
        }
        return imports;
    }

    private static RelocationPatch? CreatePatch(
        LleModuleLoadPlan loadPlan,
        LleMappedModule mappedModule,
        LleRelocation relocation,
        ulong runtimeAddress,
        IReadOnlyDictionary<uint, LleDynamicSymbol> symbols,
        IReadOnlyDictionary<uint, ulong> importAddresses)
    {
        if (relocation.Type == 0)
        {
            return null;
        }

        var symbol = relocation.Type == 16 ? null : GetSymbol(relocation, symbols);
        var symbolAddress = symbol is null
            ? 0UL
            : ResolveSymbolAddress(loadPlan, mappedModule, symbol, importAddresses);
        var symbolSize = symbol?.Size ?? 0;
        var addend = (Int128)relocation.Addend;
        var place = (Int128)runtimeAddress;
        var loadBias = (Int128)mappedModule.RuntimeImageStart - loadPlan.ImageVirtualStart;
        return relocation.Type switch
        {
            1 => Unsigned64(runtimeAddress, relocation.Type, (Int128)symbolAddress + addend),
            2 or 4 => Signed32(runtimeAddress, relocation.Type, (Int128)symbolAddress + addend - place),
            6 or 7 => RequireZeroAddend(
                relocation,
                Unsigned64(runtimeAddress, relocation.Type, symbolAddress)),
            8 or 38 => RequireNoSymbol(
                relocation,
                Unsigned64(runtimeAddress, relocation.Type, loadBias + addend)),
            10 => Unsigned32(runtimeAddress, relocation.Type, (Int128)symbolAddress + addend),
            11 => Signed32(runtimeAddress, relocation.Type, (Int128)symbolAddress + addend),
            16 => RequireZeroAddend(
                relocation,
                Unsigned64(runtimeAddress, relocation.Type, RequireTlsModuleId(mappedModule))),
            24 => Signed64(runtimeAddress, relocation.Type, (Int128)symbolAddress + addend - place),
            32 => Unsigned32(runtimeAddress, relocation.Type, (Int128)symbolSize + addend),
            33 => Unsigned64(runtimeAddress, relocation.Type, (Int128)symbolSize + addend),
            _ => throw new NotSupportedException($"ELF relocation type {relocation.Type} is not supported."),
        };
    }

    private static LleDynamicSymbol? GetSymbol(
        LleRelocation relocation,
        IReadOnlyDictionary<uint, LleDynamicSymbol> symbols)
    {
        if (relocation.SymbolIndex == 0)
        {
            return null;
        }
        if (!symbols.TryGetValue(relocation.SymbolIndex, out var symbol))
        {
            throw new InvalidDataException($"Relocation references missing symbol {relocation.SymbolIndex}.");
        }
        return symbol;
    }

    private static ulong ResolveSymbolAddress(
        LleModuleLoadPlan loadPlan,
        LleMappedModule mappedModule,
        LleDynamicSymbol symbol,
        IReadOnlyDictionary<uint, ulong> importAddresses)
    {
        if (symbol.IsUndefined)
        {
            if (!importAddresses.TryGetValue(symbol.Index, out var importedAddress))
            {
                throw new InvalidDataException($"Imported symbol {symbol.Index} has no materialized binding.");
            }
            return importedAddress;
        }
        if (symbol.SectionIndex == ShnAbsolute)
        {
            return symbol.Value;
        }
        if (symbol.Value < loadPlan.ImageVirtualStart ||
            symbol.Value - loadPlan.ImageVirtualStart >= loadPlan.ImageSize)
        {
            throw new InvalidDataException($"Defined symbol {symbol.Index} is outside the mapped image.");
        }
        return checked(mappedModule.RuntimeImageStart + (symbol.Value - loadPlan.ImageVirtualStart));
    }

    private static IReadOnlyList<ValidatedRelocation> ValidateRelocationTargets(
        LleModuleLoadPlan loadPlan,
        LleMappedModule mappedModule,
        IReadOnlyList<LleRelocation> relocations)
    {
        var result = new List<ValidatedRelocation>(relocations.Count);
        foreach (var relocation in relocations)
        {
            ArgumentNullException.ThrowIfNull(relocation);
            var width = GetWidth(relocation.Type);
            if (width == 0)
            {
                result.Add(new ValidatedRelocation(relocation, 0, 0));
                continue;
            }
            if (relocation.TargetVirtualAddress < loadPlan.ImageVirtualStart ||
                relocation.TargetVirtualAddress - loadPlan.ImageVirtualStart >= loadPlan.ImageSize)
            {
                throw new InvalidDataException("A relocation target is outside the planned image.");
            }
            var runtimeAddress = checked(
                mappedModule.RuntimeImageStart +
                (relocation.TargetVirtualAddress - loadPlan.ImageVirtualStart));
            if (!mappedModule.Segments.Any(segment =>
                    runtimeAddress >= segment.RuntimeAddress &&
                    (ulong)width <= segment.MemorySize - (runtimeAddress - segment.RuntimeAddress)))
            {
                throw new InvalidDataException("A relocation target is outside the mapped segments.");
            }
            result.Add(new ValidatedRelocation(relocation, runtimeAddress, width));
        }

        var ordered = result.Where(item => item.Width != 0).OrderBy(item => item.RuntimeAddress).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            if (ordered[index].RuntimeAddress < previous.RuntimeAddress + (ulong)previous.Width)
            {
                throw new InvalidDataException("Relocation writes overlap.");
            }
        }
        return result;
    }

    private static int GetWidth(uint type) => type switch
    {
        0 => 0,
        2 or 4 or 10 or 11 or 32 => 4,
        1 or 6 or 7 or 8 or 16 or 24 or 33 or 38 => 8,
        _ => throw new NotSupportedException($"ELF relocation type {type} is not supported."),
    };

    private static void ValidatePlans(
        LleModuleLoadPlan loadPlan,
        LleMappedModule mappedModule,
        LleModuleLinkPlan linkPlan,
        ModuleImportResolutionPlan resolutionPlan)
    {
        if (!IsValidIdentity(loadPlan.FirmwareProfileId, loadPlan.ModuleVirtualPath, loadPlan.ModuleHash) ||
            !SameIdentity(loadPlan.FirmwareProfileId, loadPlan.ModuleVirtualPath, loadPlan.ModuleHash, mappedModule) ||
            !SameIdentity(loadPlan.FirmwareProfileId, loadPlan.ModuleVirtualPath, loadPlan.ModuleHash, linkPlan) ||
            !string.Equals(loadPlan.FirmwareProfileId, resolutionPlan.FirmwareProfileId, StringComparison.Ordinal) ||
            !string.Equals(loadPlan.ModuleVirtualPath, resolutionPlan.ModuleVirtualPath, StringComparison.Ordinal) ||
            !string.Equals(loadPlan.ModuleHash, resolutionPlan.ModuleHash, StringComparison.Ordinal) ||
            mappedModule.RuntimeImageStart == 0 ||
            mappedModule.ImageVirtualStart != loadPlan.ImageVirtualStart ||
            mappedModule.ImageSize != loadPlan.ImageSize ||
            mappedModule.Segments is null ||
            linkPlan.Relocations is null ||
            linkPlan.ReferencedSymbols is null ||
            linkPlan.ImportedSymbols is null ||
            resolutionPlan.Bindings is null ||
            !linkPlan.CanApply ||
            !resolutionPlan.CanLink)
        {
            throw new InvalidDataException("The mapped module and link plans are inconsistent or cannot be linked.");
        }

        ValidateMappedSegments(loadPlan, mappedModule);

        if (linkPlan.ReferencedSymbols.Any(symbol => symbol is null) ||
            linkPlan.ImportedSymbols.Any(symbol => symbol is null) ||
            resolutionPlan.Bindings.Any(binding => binding is null) ||
            linkPlan.ReferencedSymbols.GroupBy(symbol => symbol.Index).Any(group => group.Count() != 1) ||
            linkPlan.ImportedSymbols.GroupBy(symbol => symbol.Index).Any(group => group.Count() != 1) ||
            resolutionPlan.Bindings.GroupBy(binding => binding.SymbolIndex).Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("The link plans contain null or duplicate symbol records.");
        }

        var imported = linkPlan.ImportedSymbols.ToDictionary(symbol => symbol.Index);
        var referenced = linkPlan.ReferencedSymbols.ToDictionary(symbol => symbol.Index);
        if (imported.Any(item =>
                !referenced.TryGetValue(item.Key, out var symbol) ||
                symbol != item.Value ||
                !item.Value.IsUndefined) ||
            resolutionPlan.Bindings.Count != imported.Count ||
            resolutionPlan.Bindings.Any(binding =>
                !imported.TryGetValue(binding.SymbolIndex, out var symbol) ||
                !string.Equals(binding.SymbolName, symbol.Name, StringComparison.Ordinal) ||
                !IsValidBinding(binding)))
        {
            throw new InvalidDataException("The import binding plan does not match the module imports.");
        }
    }

    private static void ValidateMappedSegments(
        LleModuleLoadPlan loadPlan,
        LleMappedModule mappedModule)
    {
        if (loadPlan.ImageSize == 0 ||
            loadPlan.ImageSize > ulong.MaxValue - loadPlan.ImageVirtualStart ||
            mappedModule.ImageSize > ulong.MaxValue - mappedModule.RuntimeImageStart ||
            loadPlan.Segments is null ||
            loadPlan.Segments.Count == 0 ||
            mappedModule.Segments.Count != loadPlan.Segments.Count ||
            loadPlan.Segments.Any(segment => segment is null) ||
            mappedModule.Segments.Any(segment => segment is null) ||
            loadPlan.Segments.GroupBy(segment => segment.ProgramHeaderIndex).Any(group => group.Count() != 1) ||
            mappedModule.Segments.GroupBy(segment => segment.ProgramHeaderIndex).Any(group => group.Count() != 1))
        {
            throw new InvalidDataException("The mapped LLE segment catalog is invalid.");
        }

        var mappedByIndex = mappedModule.Segments.ToDictionary(segment => segment.ProgramHeaderIndex);
        foreach (var segment in loadPlan.Segments)
        {
            if (segment.VirtualAddress < loadPlan.ImageVirtualStart ||
                segment.MemorySize == 0 ||
                segment.VirtualAddress - loadPlan.ImageVirtualStart >= loadPlan.ImageSize ||
                segment.MemorySize > loadPlan.ImageSize - (segment.VirtualAddress - loadPlan.ImageVirtualStart) ||
                (segment.Permissions & ~(
                    LleSegmentPermissions.Read |
                    LleSegmentPermissions.Write |
                    LleSegmentPermissions.Execute)) != 0 ||
                !mappedByIndex.TryGetValue(segment.ProgramHeaderIndex, out var mapped) ||
                mapped.RuntimeAddress != checked(
                    mappedModule.RuntimeImageStart +
                    (segment.VirtualAddress - loadPlan.ImageVirtualStart)) ||
                mapped.MemorySize != segment.MemorySize ||
                mapped.Permissions != segment.Permissions)
            {
                throw new InvalidDataException("The mapped LLE segments do not match the verified load plan.");
            }
        }
    }

    private static bool IsValidBinding(ImportBindingDecision binding) => binding.Source switch
    {
        ImportBindingSource.Hle or ImportBindingSource.ControlledStub =>
            !string.IsNullOrWhiteSpace(binding.HleDispatchKey) &&
            !binding.HleDispatchKey.Any(char.IsControl) &&
            binding.HleDataRuntimeAddress is null &&
            binding.LleRuntimeAddress is null,
        ImportBindingSource.HleData =>
            binding.HleDataRuntimeAddress is not null and not 0 &&
            binding.HleDispatchKey is null &&
            binding.LleRuntimeAddress is null,
        ImportBindingSource.Lle =>
            binding.LleRuntimeAddress is not null and not 0 &&
            binding.HleDispatchKey is null &&
            binding.HleDataRuntimeAddress is null,
        _ => false,
    };

    private static bool SameIdentity(
        string profileId,
        string moduleVirtualPath,
        string moduleHash,
        LleMappedModule module) =>
        string.Equals(profileId, module.FirmwareProfileId, StringComparison.Ordinal) &&
        string.Equals(moduleVirtualPath, module.ModuleVirtualPath, StringComparison.Ordinal) &&
        string.Equals(moduleHash, module.ModuleHash, StringComparison.Ordinal);

    private static bool SameIdentity(
        string profileId,
        string moduleVirtualPath,
        string moduleHash,
        LleModuleLinkPlan plan) =>
        string.Equals(profileId, plan.FirmwareProfileId, StringComparison.Ordinal) &&
        string.Equals(moduleVirtualPath, plan.ModuleVirtualPath, StringComparison.Ordinal) &&
        string.Equals(moduleHash, plan.ModuleHash, StringComparison.Ordinal);

    private static bool IsValidIdentity(
        string profileId,
        string moduleVirtualPath,
        string moduleHash) =>
        !string.IsNullOrWhiteSpace(profileId) &&
        !string.IsNullOrWhiteSpace(moduleVirtualPath) &&
        moduleVirtualPath.StartsWith("/", StringComparison.Ordinal) &&
        moduleHash is { Length: 64 } &&
        moduleHash.All(char.IsAsciiHexDigit);

    private static RelocationPatch RequireZeroAddend(LleRelocation relocation, RelocationPatch patch)
    {
        if (relocation.Addend != 0)
        {
            throw new InvalidDataException($"Relocation type {relocation.Type} requires a zero addend.");
        }
        return patch;
    }

    private static uint RequireTlsModuleId(LleMappedModule mappedModule)
    {
        if (mappedModule.TlsModuleId == 0)
        {
            throw new InvalidDataException("R_X86_64_DTPMOD64 requires a registered TLS module identifier.");
        }
        return mappedModule.TlsModuleId;
    }

    private static RelocationPatch RequireNoSymbol(LleRelocation relocation, RelocationPatch patch)
    {
        if (relocation.SymbolIndex != 0)
        {
            throw new InvalidDataException($"Relative relocation type {relocation.Type} cannot reference a symbol.");
        }
        return patch;
    }

    private static RelocationPatch Unsigned32(ulong address, uint type, Int128 value)
    {
        if (value < uint.MinValue || value > uint.MaxValue)
        {
            throw new OverflowException($"Relocation type {type} does not fit in 32 unsigned bits.");
        }
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        return new RelocationPatch(address, type, (ulong)value, bytes);
    }

    private static RelocationPatch Signed32(ulong address, uint type, Int128 value)
    {
        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new OverflowException($"Relocation type {type} does not fit in 32 signed bits.");
        }
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, (int)value);
        return new RelocationPatch(address, type, unchecked((uint)(int)value), bytes);
    }

    private static RelocationPatch Unsigned64(ulong address, uint type, Int128 value)
    {
        if (value < ulong.MinValue || value > ulong.MaxValue)
        {
            throw new OverflowException($"Relocation type {type} does not fit in 64 unsigned bits.");
        }
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)value);
        return new RelocationPatch(address, type, (ulong)value, bytes);
    }

    private static RelocationPatch Signed64(ulong address, uint type, Int128 value)
    {
        if (value < long.MinValue || value > long.MaxValue)
        {
            throw new OverflowException($"Relocation type {type} does not fit in 64 signed bits.");
        }
        var encoded = unchecked((ulong)(long)value);
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, encoded);
        return new RelocationPatch(address, type, encoded, bytes);
    }

    private sealed record ValidatedRelocation(LleRelocation Relocation, ulong RuntimeAddress, int Width);

    private sealed record RelocationPatch(ulong RuntimeAddress, uint Type, ulong EncodedValue, byte[] Data);
}
