// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using Touche.Firmware;

namespace Touche.PS5.Modules;

/// <summary>
/// Verifies and parses a selected firmware ELF into an immutable mapping plan.
/// It does not reserve guest memory, apply relocations or execute guest code.
/// </summary>
public sealed class LleModuleLoadPlanner
{
    private const int ElfHeaderSize = 64;
    private const int ProgramHeaderSize = 56;
    private const int MaximumProgramHeaders = 4096;
    private const ulong MaximumImageSpan = 16UL * 1024 * 1024 * 1024;
    private const uint ProgramTypeLoad = 1;
    private const uint ProgramTypeDynamic = 2;
    private const uint KnownProgramFlags = 7;

    public async Task<LleModuleLoadPlan> BuildAsync(
        ModuleResolutionDecision decision,
        FirmwareModuleCatalog catalog,
        IFirmwareVirtualFileSystem fileSystem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(fileSystem);
        var module = ValidateSelection(decision, catalog);
        if (!string.Equals(fileSystem.ProfileId, catalog.ProfileId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The mounted firmware profile does not match the module catalog.");
        }

        await using var handle = await fileSystem.OpenReadAsync(
            module.VirtualPath,
            cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            throw new FileNotFoundException("The selected firmware module is not mounted.", module.VirtualPath);
        }
        if (!string.Equals(handle.Artifact.VirtualPath, module.VirtualPath, StringComparison.Ordinal) ||
            !string.Equals(handle.Artifact.Sha256, module.Sha256, StringComparison.Ordinal) ||
            handle.Artifact.Kind != FirmwareArtifactKind.ElfOrSelf)
        {
            throw new InvalidDataException("The mounted firmware artifact does not match the selected module.");
        }

        return await ParseAsync(module, handle.Content, handle.Artifact.Size, cancellationToken)
            .ConfigureAwait(false);
    }

    private static FirmwareModule ValidateSelection(
        ModuleResolutionDecision decision,
        FirmwareModuleCatalog catalog)
    {
        if (decision.SelectedImplementation != ModuleImplementationKind.Lle ||
            string.IsNullOrWhiteSpace(decision.ModuleVirtualPath) ||
            string.IsNullOrWhiteSpace(decision.ModuleHash))
        {
            throw new InvalidOperationException("Only a resolved LLE decision can produce a load plan.");
        }
        if (catalog.SchemaVersion != FirmwareModuleCatalog.CurrentSchemaVersion ||
            string.IsNullOrWhiteSpace(catalog.ProfileId) ||
            catalog.Modules is null)
        {
            throw new InvalidDataException("The firmware module catalog is invalid or unsupported.");
        }

        var matches = catalog.Modules.Where(candidate =>
                string.Equals(candidate.VirtualPath, decision.ModuleVirtualPath, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException("The selected firmware module is missing or duplicated in its catalog.");
        }

        var module = matches[0];
        if (!string.Equals(module.Sha256, decision.ModuleHash, StringComparison.Ordinal) ||
            module.Format != FirmwareModuleFormat.Elf64 ||
            !string.Equals(module.Architecture, "x86-64", StringComparison.Ordinal) ||
            module.State is not FirmwareModuleState.Parseable and not FirmwareModuleState.LleCompatible)
        {
            throw new InvalidDataException("The selected firmware module is not eligible for an LLE load plan.");
        }
        return module;
    }

    private static async Task<LleModuleLoadPlan> ParseAsync(
        FirmwareModule module,
        Stream stream,
        long artifactSize,
        CancellationToken cancellationToken)
    {
        if (!stream.CanRead || !stream.CanSeek || artifactSize < ElfHeaderSize || stream.Length != artifactSize)
        {
            throw new InvalidDataException("The firmware module stream is not a valid seekable ELF source.");
        }

        var header = new byte[ElfHeaderSize];
        await ReadExactlyAtAsync(stream, 0, header, cancellationToken).ConfigureAwait(false);
        ValidateElfIdentity(header);
        var elfType = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16));
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18));
        var elfVersion = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var entryPoint = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24));
        var programHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32));
        var elfHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(52));
        var programHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(54));
        var programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(56));
        if (elfType == 0 ||
            machine != 62 ||
            elfVersion != 1 ||
            elfHeaderSize != ElfHeaderSize ||
            programHeaderEntrySize != ProgramHeaderSize ||
            programHeaderCount is 0 or > MaximumProgramHeaders ||
            programHeaderCount != module.ProgramHeaderCount)
        {
            throw new InvalidDataException("The firmware module has unsupported or inconsistent ELF metadata.");
        }

        var tableSize = checked((ulong)programHeaderCount * ProgramHeaderSize);
        EnsureFileRange(programHeaderOffset, tableSize, artifactSize, "program header table");
        var table = new byte[checked((int)tableSize)];
        await ReadExactlyAtAsync(stream, programHeaderOffset, table, cancellationToken).ConfigureAwait(false);

        var segments = new List<LleLoadSegment>();
        var hasDynamicTable = false;
        var imageStart = ulong.MaxValue;
        var imageEnd = 0UL;
        for (var index = 0; index < programHeaderCount; index++)
        {
            var item = table.AsSpan(index * ProgramHeaderSize, ProgramHeaderSize);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(item);
            if (type == ProgramTypeDynamic)
            {
                hasDynamicTable = true;
            }
            if (type != ProgramTypeLoad)
            {
                continue;
            }

            var flags = BinaryPrimitives.ReadUInt32LittleEndian(item[4..]);
            var fileOffset = BinaryPrimitives.ReadUInt64LittleEndian(item[8..]);
            var virtualAddress = BinaryPrimitives.ReadUInt64LittleEndian(item[16..]);
            var fileSize = BinaryPrimitives.ReadUInt64LittleEndian(item[32..]);
            var memorySize = BinaryPrimitives.ReadUInt64LittleEndian(item[40..]);
            var alignment = BinaryPrimitives.ReadUInt64LittleEndian(item[48..]);
            if ((flags & ~KnownProgramFlags) != 0 || fileSize > memorySize)
            {
                throw new InvalidDataException($"ELF load segment {index} has invalid sizes or flags.");
            }
            if (memorySize == 0)
            {
                continue;
            }
            EnsureFileRange(fileOffset, fileSize, artifactSize, $"load segment {index}");
            if (alignment > 1 &&
                (!IsPowerOfTwo(alignment) || virtualAddress % alignment != fileOffset % alignment))
            {
                throw new InvalidDataException($"ELF load segment {index} has invalid alignment.");
            }

            if (memorySize > ulong.MaxValue - virtualAddress)
            {
                throw new InvalidDataException($"ELF load segment {index} exceeds the guest address space.");
            }
            var segmentEnd = virtualAddress + memorySize;
            imageStart = Math.Min(imageStart, virtualAddress);
            imageEnd = Math.Max(imageEnd, segmentEnd);
            segments.Add(new LleLoadSegment(
                index,
                fileOffset,
                fileSize,
                virtualAddress,
                memorySize,
                alignment,
                ConvertPermissions(flags)));
        }

        if (segments.Count == 0 || imageEnd <= imageStart || imageEnd - imageStart > MaximumImageSpan)
        {
            throw new InvalidDataException("The firmware module has no safe, loadable ELF image span.");
        }
        if (hasDynamicTable != module.HasDynamicTable)
        {
            throw new InvalidDataException("The firmware module dynamic table does not match its catalog.");
        }
        if (entryPoint != 0 && !segments.Any(segment =>
                entryPoint >= segment.VirtualAddress &&
                entryPoint - segment.VirtualAddress < segment.MemorySize &&
                (segment.Permissions & LleSegmentPermissions.Execute) != 0))
        {
            throw new InvalidDataException("The ELF entry point is outside executable load segments.");
        }

        return new LleModuleLoadPlan
        {
            ModuleVirtualPath = module.VirtualPath,
            ModuleHash = module.Sha256,
            ElfType = elfType,
            EntryPoint = entryPoint,
            ImageVirtualStart = imageStart,
            ImageSize = imageEnd - imageStart,
            HasDynamicTable = hasDynamicTable,
            Segments = segments.ToArray(),
        };
    }

    private static void ValidateElfIdentity(ReadOnlySpan<byte> header)
    {
        if (header[0] != 0x7f || header[1] != (byte)'E' || header[2] != (byte)'L' || header[3] != (byte)'F' ||
            header[4] != 2 || header[5] != 1 || header[6] != 1)
        {
            throw new InvalidDataException("Only little-endian ELF64 firmware modules are supported.");
        }
    }

    private static async Task ReadExactlyAtAsync(
        Stream stream,
        ulong offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (offset > long.MaxValue)
        {
            throw new InvalidDataException("ELF file offset exceeds the supported range.");
        }
        stream.Position = (long)offset;
        await stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureFileRange(ulong offset, ulong size, long fileSize, string description)
    {
        if (fileSize < 0 || offset > (ulong)fileSize || size > (ulong)fileSize - offset)
        {
            throw new InvalidDataException($"ELF {description} exceeds the verified firmware object.");
        }
    }

    private static bool IsPowerOfTwo(ulong value) => (value & (value - 1)) == 0;

    private static LleSegmentPermissions ConvertPermissions(uint flags)
    {
        var result = LleSegmentPermissions.None;
        if ((flags & 4) != 0)
        {
            result |= LleSegmentPermissions.Read;
        }
        if ((flags & 2) != 0)
        {
            result |= LleSegmentPermissions.Write;
        }
        if ((flags & 1) != 0)
        {
            result |= LleSegmentPermissions.Execute;
        }
        return result;
    }
}
