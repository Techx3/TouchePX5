// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using Touche.Firmware;

namespace Touche.PS5.Modules;

/// <summary>
/// Inspects verified dynamic metadata and relocations. It never resolves a
/// symbol, writes guest memory or invokes module initialization code.
/// </summary>
public sealed class LleModuleLinkPlanner
{
    private const int DynamicEntrySize = 16;
    private const int RelaEntrySize = 24;
    private const int SymbolEntrySize = 24;
    private const int MaximumSymbolNameBytes = 4096;
    private const ulong MaximumDynamicTableBytes = 1024 * 1024;
    private const ulong MaximumRelocationTableBytes = 16 * 1024 * 1024;
    private const ulong MaximumStringTableBytes = 16 * 1024 * 1024;
    private const ulong MaximumSymbolTableBytes = 16 * 1024 * 1024;

    private const long DtNull = 0;
    private const long DtPltRelSize = 2;
    private const long DtStrTab = 5;
    private const long DtSymTab = 6;
    private const long DtRela = 7;
    private const long DtRelaSize = 8;
    private const long DtRelaEntrySize = 9;
    private const long DtStrSize = 10;
    private const long DtSymbolEntrySize = 11;
    private const long DtPltRelKind = 20;
    private const long DtJmpRel = 23;
    private const long DtSceJmpRel = 0x61000029;
    private const long DtScePltRelSize = 0x6100002D;
    private const long DtSceRela = 0x6100002F;
    private const long DtSceRelaSize = 0x61000031;
    private const long DtSceStrTab = 0x61000035;
    private const long DtSceStrSize = 0x61000037;
    private const long DtSceSymTab = 0x61000039;
    private const long DtSceSymTabSize = 0x6100003F;

    private static readonly HashSet<uint> SupportedRelocationTypes =
    [
        0, 1, 2, 4, 6, 7, 8, 10, 11, 16, 17, 18, 24, 32, 33, 38,
    ];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<LleModuleLinkPlan> BuildAsync(
        LleModuleLoadPlan loadPlan,
        IFirmwareVirtualFileSystem fileSystem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadPlan);
        ArgumentNullException.ThrowIfNull(fileSystem);
        var dynamicTable = ValidatePlan(loadPlan, fileSystem.ProfileId);

        await using var handle = await fileSystem.OpenReadAsync(
            loadPlan.ModuleVirtualPath,
            cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            throw new FileNotFoundException("The planned firmware module is not mounted.", loadPlan.ModuleVirtualPath);
        }
        if (!string.Equals(handle.Artifact.Sha256, loadPlan.ModuleHash, StringComparison.Ordinal) ||
            !string.Equals(handle.Artifact.VirtualPath, loadPlan.ModuleVirtualPath, StringComparison.Ordinal) ||
            handle.Artifact.Kind != FirmwareArtifactKind.ElfOrSelf)
        {
            throw new InvalidDataException("The mounted artifact does not match the LLE load plan.");
        }

        var dynamicBytes = await ReadFileRangeAsync(
            handle.Content,
            dynamicTable.FileOffset,
            dynamicTable.FileSize,
            MaximumDynamicTableBytes,
            cancellationToken).ConfigureAwait(false);
        var entries = ParseDynamicEntries(dynamicBytes);
        ValidateEntrySizes(entries);
        var metadata = new LleDynamicLinkMetadata(
            GetPreferred(entries, DtStrTab, DtSceStrTab),
            GetPreferred(entries, DtStrSize, DtSceStrSize),
            GetPreferred(entries, DtSymTab, DtSceSymTab),
            GetPreferred(entries, missingStandardTag: null, sceTag: DtSceSymTabSize),
            GetPreferred(entries, DtRela, DtSceRela),
            GetPreferred(entries, DtRelaSize, DtSceRelaSize),
            GetPreferred(entries, DtJmpRel, DtSceJmpRel),
            GetPreferred(entries, DtPltRelSize, DtScePltRelSize));

        ValidateTablePair(metadata.RelaLocation, metadata.RelaSize, "RELA");
        ValidateTablePair(
            metadata.ProcedureLinkageLocation,
            metadata.ProcedureLinkageSize,
            "procedure linkage");
        var relocations = new List<LleRelocation>();
        await ReadRelocationsAsync(
            loadPlan,
            handle.Content,
            handle.Artifact.Size,
            metadata.RelaLocation,
            metadata.RelaSize,
            LleRelocationTableKind.Rela,
            relocations,
            cancellationToken).ConfigureAwait(false);
        await ReadRelocationsAsync(
            loadPlan,
            handle.Content,
            handle.Artifact.Size,
            metadata.ProcedureLinkageLocation,
            metadata.ProcedureLinkageSize,
            LleRelocationTableKind.ProcedureLinkage,
            relocations,
            cancellationToken).ConfigureAwait(false);

        var duplicates = relocations
            .GroupBy(item => (item.TargetVirtualAddress, item.SymbolIndex, item.Type, item.Addend))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            throw new InvalidDataException("The ELF contains duplicate relocation records.");
        }
        var unsupported = relocations
            .Select(item => item.Type)
            .Where(type => !SupportedRelocationTypes.Contains(type))
            .Distinct()
            .Order()
            .ToArray();
        var symbolResult = await ReadReferencedSymbolsAsync(
            loadPlan,
            handle.Content,
            handle.Artifact.Size,
            metadata,
            relocations,
            cancellationToken).ConfigureAwait(false);
        if (metadata.SymbolTableSize == 0 && symbolResult.EffectiveSymbolTableSize != 0)
        {
            metadata = metadata with { SymbolTableSize = symbolResult.EffectiveSymbolTableSize };
        }
        return new LleModuleLinkPlan
        {
            FirmwareProfileId = loadPlan.FirmwareProfileId,
            ModuleVirtualPath = loadPlan.ModuleVirtualPath,
            ModuleHash = loadPlan.ModuleHash,
            Metadata = metadata,
            Relocations = relocations.ToArray(),
            ReferencedSymbols = symbolResult.Symbols,
            ImportedSymbols = symbolResult.Symbols.Where(symbol => symbol.IsUndefined).ToArray(),
            UnsupportedRelocationTypes = unsupported,
        };
    }

    private static LleDynamicTable ValidatePlan(LleModuleLoadPlan plan, string mountedProfileId)
    {
        if (!string.Equals(plan.FirmwareProfileId, mountedProfileId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(plan.ModuleVirtualPath) ||
            string.IsNullOrWhiteSpace(plan.ModuleHash) ||
            plan.Segments is null ||
            plan.Segments.Count == 0 ||
            !plan.HasDynamicTable ||
            plan.DynamicTable is null ||
            plan.DynamicTable.FileSize is 0 or > MaximumDynamicTableBytes ||
            plan.DynamicTable.FileSize % DynamicEntrySize != 0)
        {
            throw new InvalidDataException("The LLE plan has no valid dynamic table for this firmware profile.");
        }
        return plan.DynamicTable;
    }

    private static Dictionary<long, ulong> ParseDynamicEntries(ReadOnlySpan<byte> bytes)
    {
        var entries = new Dictionary<long, ulong>();
        var terminated = false;
        for (var offset = 0; offset < bytes.Length; offset += DynamicEntrySize)
        {
            var tag = BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + sizeof(long))..]);
            if (tag == DtNull)
            {
                terminated = true;
                break;
            }
            if (entries.TryGetValue(tag, out var existing) && existing != value)
            {
                throw new InvalidDataException($"ELF dynamic tag 0x{tag:X} has conflicting values.");
            }
            entries[tag] = value;
        }
        if (!terminated)
        {
            throw new InvalidDataException("The ELF dynamic table is not terminated.");
        }
        return entries;
    }

    private static void ValidateEntrySizes(IReadOnlyDictionary<long, ulong> entries)
    {
        if (entries.TryGetValue(DtRelaEntrySize, out var relaEntrySize) && relaEntrySize != RelaEntrySize)
        {
            throw new InvalidDataException("Only ELF64 RELA entries are supported.");
        }
        if (entries.TryGetValue(DtSymbolEntrySize, out var symbolEntrySize) && symbolEntrySize != 24)
        {
            throw new InvalidDataException("The ELF symbol entry size is unsupported.");
        }
        if (entries.TryGetValue(DtPltRelKind, out var pltRelKind) && pltRelKind != DtRela)
        {
            throw new InvalidDataException("Only RELA procedure linkage relocations are supported.");
        }
    }

    private static ulong GetPreferred(
        IReadOnlyDictionary<long, ulong> entries,
        long? missingStandardTag,
        long sceTag)
    {
        if (entries.TryGetValue(sceTag, out var sceValue))
        {
            return sceValue;
        }
        return missingStandardTag is not null && entries.TryGetValue(missingStandardTag.Value, out var value)
            ? value
            : 0;
    }

    private static void ValidateTablePair(ulong location, ulong size, string name)
    {
        if ((location == 0) != (size == 0) || size > MaximumRelocationTableBytes || size % RelaEntrySize != 0)
        {
            throw new InvalidDataException($"The ELF {name} relocation table is invalid.");
        }
    }

    private static async Task ReadRelocationsAsync(
        LleModuleLoadPlan plan,
        Stream stream,
        long artifactSize,
        ulong location,
        ulong size,
        LleRelocationTableKind tableKind,
        ICollection<LleRelocation> destination,
        CancellationToken cancellationToken)
    {
        if (size == 0)
        {
            return;
        }
        var fileOffset = ResolveFileOffset(plan, location, size, artifactSize);
        var bytes = await ReadFileRangeAsync(
            stream,
            fileOffset,
            size,
            MaximumRelocationTableBytes,
            cancellationToken).ConfigureAwait(false);
        for (var offset = 0; offset < bytes.Length; offset += RelaEntrySize)
        {
            var item = bytes.AsSpan(offset, RelaEntrySize);
            var target = BinaryPrimitives.ReadUInt64LittleEndian(item);
            var info = BinaryPrimitives.ReadUInt64LittleEndian(item[8..]);
            var addend = BinaryPrimitives.ReadInt64LittleEndian(item[16..]);
            var type = (uint)info;
            var symbolIndex = (uint)(info >> 32);
            ValidateRelocationTarget(plan, target, type);
            destination.Add(new LleRelocation(tableKind, target, symbolIndex, type, addend));
        }
    }

    private static ulong ResolveFileOffset(
        LleModuleLoadPlan plan,
        ulong location,
        ulong size,
        long artifactSize)
    {
        foreach (var segment in plan.Segments)
        {
            if (location < segment.VirtualAddress)
            {
                continue;
            }
            var relative = location - segment.VirtualAddress;
            if (relative <= segment.FileSize && size <= segment.FileSize - relative)
            {
                return checked(segment.FileOffset + relative);
            }
        }
        if (artifactSize >= 0 && location <= (ulong)artifactSize && size <= (ulong)artifactSize - location)
        {
            return location;
        }
        throw new InvalidDataException("An ELF link table is outside verified loadable data.");
    }

    private static void ValidateRelocationTarget(LleModuleLoadPlan plan, ulong target, uint type)
    {
        if (type == 0)
        {
            return;
        }
        var width = type is 2 or 4 or 10 or 11 or 32 ? 4UL : 8UL;
        if (target < plan.ImageVirtualStart ||
            target - plan.ImageVirtualStart >= plan.ImageSize ||
            width > plan.ImageSize - (target - plan.ImageVirtualStart))
        {
            throw new InvalidDataException("An ELF relocation target is outside the planned image span.");
        }
    }

    private static async Task<SymbolReadResult> ReadReferencedSymbolsAsync(
        LleModuleLoadPlan plan,
        Stream stream,
        long artifactSize,
        LleDynamicLinkMetadata metadata,
        IReadOnlyList<LleRelocation> relocations,
        CancellationToken cancellationToken)
    {
        var indices = relocations
            .Where(item =>
                item.SymbolIndex != 0 &&
                SupportedRelocationTypes.Contains(item.Type) &&
                RequiresSymbol(item.Type))
            .Select(item => item.SymbolIndex)
            .Distinct()
            .Order()
            .ToArray();
        if (indices.Length == 0)
        {
            return new SymbolReadResult([], 0);
        }
        if (metadata.StringTableLocation == 0 ||
            metadata.StringTableSize is 0 or > MaximumStringTableBytes ||
            metadata.SymbolTableLocation == 0)
        {
            throw new InvalidDataException("Referenced ELF symbols require valid string and symbol tables.");
        }

        var requiredSymbolBytes = checked(((ulong)indices[^1] + 1) * SymbolEntrySize);
        var symbolTableSize = metadata.SymbolTableSize == 0
            ? requiredSymbolBytes
            : metadata.SymbolTableSize;
        if (symbolTableSize < requiredSymbolBytes ||
            symbolTableSize > MaximumSymbolTableBytes ||
            symbolTableSize % SymbolEntrySize != 0)
        {
            throw new InvalidDataException("The ELF symbol table cannot contain every referenced symbol.");
        }

        var stringOffset = ResolveFileOffset(
            plan,
            metadata.StringTableLocation,
            metadata.StringTableSize,
            artifactSize);
        var symbolOffset = ResolveFileOffset(
            plan,
            metadata.SymbolTableLocation,
            symbolTableSize,
            artifactSize);
        var strings = await ReadFileRangeAsync(
            stream,
            stringOffset,
            metadata.StringTableSize,
            MaximumStringTableBytes,
            cancellationToken).ConfigureAwait(false);
        var symbols = await ReadFileRangeAsync(
            stream,
            symbolOffset,
            symbolTableSize,
            MaximumSymbolTableBytes,
            cancellationToken).ConfigureAwait(false);

        var result = new List<LleDynamicSymbol>(indices.Length);
        foreach (var index in indices)
        {
            var entryOffset = checked((int)((ulong)index * SymbolEntrySize));
            var entry = symbols.AsSpan(entryOffset, SymbolEntrySize);
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var info = entry[4];
            var other = entry[5];
            var sectionIndex = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var name = ReadSymbolName(strings, nameOffset);
            if (sectionIndex == 0 && string.IsNullOrEmpty(name))
            {
                throw new InvalidDataException("An imported ELF symbol has no name.");
            }
            result.Add(new LleDynamicSymbol(
                index,
                name,
                (byte)(info >> 4),
                (byte)(info & 0x0f),
                (byte)(other & 0x03),
                sectionIndex,
                value,
                size));
        }
        return new SymbolReadResult(result.ToArray(), symbolTableSize);
    }

    private static string ReadSymbolName(ReadOnlySpan<byte> stringTable, uint nameOffset)
    {
        if (nameOffset >= stringTable.Length)
        {
            throw new InvalidDataException("An ELF symbol name is outside the string table.");
        }
        var remaining = stringTable[(int)nameOffset..];
        var maximum = Math.Min(remaining.Length, MaximumSymbolNameBytes);
        var terminator = remaining[..maximum].IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new InvalidDataException("An ELF symbol name is not terminated within its bounds.");
        }
        string name;
        try
        {
            name = StrictUtf8.GetString(remaining[..terminator]);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("An ELF symbol name is not valid UTF-8.", exception);
        }
        if (name.Any(char.IsControl))
        {
            throw new InvalidDataException("An ELF symbol name contains control characters.");
        }
        return name;
    }

    private static bool RequiresSymbol(uint relocationType) =>
        relocationType is not (0 or 8 or 16 or 38);

    private static async Task<byte[]> ReadFileRangeAsync(
        Stream stream,
        ulong offset,
        ulong size,
        ulong maximumSize,
        CancellationToken cancellationToken)
    {
        if (!stream.CanRead ||
            !stream.CanSeek ||
            offset > long.MaxValue ||
            size > maximumSize ||
            size > int.MaxValue ||
            offset > (ulong)stream.Length ||
            size > (ulong)stream.Length - offset)
        {
            throw new InvalidDataException("ELF link metadata exceeds the verified firmware object.");
        }
        var bytes = new byte[(int)size];
        stream.Position = (long)offset;
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private sealed record SymbolReadResult(
        IReadOnlyList<LleDynamicSymbol> Symbols,
        ulong EffectiveSymbolTableSize);
}
