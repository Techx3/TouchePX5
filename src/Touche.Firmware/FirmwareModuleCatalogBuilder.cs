// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Touche.Firmware;

/// <summary>
/// Performs passive, bounded inspection of imported ELF/SELF artifacts.
/// It never maps or executes guest code.
/// </summary>
public sealed class FirmwareModuleCatalogBuilder
{
    private const uint ProgramTypeLoad = 1;
    private const uint ProgramTypeDynamic = 2;
    private const long DynamicNull = 0;
    private const long DynamicNeeded = 1;
    private const long DynamicStringTable = 5;
    private const long DynamicStringTableSize = 10;
    private const int ElfHeaderSize64 = 64;
    private const int ProgramHeaderSize64 = 56;
    private const int MaximumProgramHeaders = 4096;
    private const int MaximumDynamicEntries = 65_536;
    private const int MaximumDependencyNameBytes = 4096;
    private const long MaximumModuleBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _objectsRoot;

    public FirmwareModuleCatalogBuilder(string objectsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectsRoot);
        _objectsRoot = Path.GetFullPath(objectsRoot);
    }

    public async Task<FirmwareModuleCatalog> BuildAsync(
        FirmwareProfileManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var modules = new List<FirmwareModule>();
        foreach (var artifact in manifest.Artifacts
                     .Where(artifact => artifact.Kind == FirmwareArtifactKind.ElfOrSelf)
                     .OrderBy(artifact => artifact.VirtualPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectPath = ResolveObjectPath(artifact.Sha256);
            modules.Add(await InspectAsync(artifact, objectPath, cancellationToken).ConfigureAwait(false));
        }

        var availableNames = modules
            .Select(module => Path.GetFileName(module.VirtualPath))
            .ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < modules.Count; index++)
        {
            var module = modules[index];
            var missing = module.Dependencies
                .Where(dependency => !availableNames.Contains(Path.GetFileName(dependency)))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (module.State == FirmwareModuleState.Parseable && missing.Length > 0)
            {
                modules[index] = module with
                {
                    State = FirmwareModuleState.MissingDependencies,
                    Reason = $"Missing declared dependencies: {string.Join(", ", missing)}",
                };
            }
        }

        var contentHash = ComputeCatalogHash(modules);
        return new FirmwareModuleCatalog
        {
            ProfileId = manifest.ProfileId,
            ContentHash = contentHash,
            Modules = modules,
        };
    }

    public static async Task WriteAsync(
        FirmwareModuleCatalog catalog,
        string profileDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        Directory.CreateDirectory(profileDirectory);
        var destination = Path.Combine(Path.GetFullPath(profileDirectory), "modules.json");
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(catalog, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<FirmwareModule> InspectAsync(
        FirmwareArtifact artifact,
        string objectPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(objectPath))
        {
            return CreateFailure(artifact, FirmwareModuleState.RuntimeIncompatible, "CAS object is missing.");
        }

        if ((File.GetAttributes(objectPath) & FileAttributes.ReparsePoint) != 0)
        {
            return CreateFailure(artifact, FirmwareModuleState.RuntimeIncompatible, "CAS object cannot be a link.");
        }

        var objectSize = new FileInfo(objectPath).Length;
        if (objectSize != artifact.Size || objectSize > MaximumModuleBytes)
        {
            return CreateFailure(
                artifact,
                FirmwareModuleState.RuntimeIncompatible,
                objectSize != artifact.Size
                    ? "CAS object size does not match the artifact manifest."
                    : $"Module exceeds the {MaximumModuleBytes} byte catalog limit.");
        }

        var bytes = await ReadModuleBytesAsync(objectPath, objectSize, cancellationToken).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, artifact.Sha256, StringComparison.Ordinal))
        {
            return CreateFailure(artifact, FirmwareModuleState.RuntimeIncompatible, "CAS object failed hash verification.");
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0x4f, 0x15, 0x3d, 0x1d }) ||
            bytes.AsSpan().StartsWith(new byte[] { 0x54, 0x14, 0xf5, 0xee }))
        {
            return new FirmwareModule(
                artifact.VirtualPath,
                artifact.Sha256,
                FirmwareModuleFormat.SonySelf,
                FirmwareModuleState.UnsupportedEncryption,
                "x86-64",
                null,
                0,
                false,
                [],
                "Sony SELF content requires a legally extracted, decrypted ELF payload.");
        }

        return InspectElf64(artifact, bytes);
    }

    private static FirmwareModule InspectElf64(FirmwareArtifact artifact, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ElfHeaderSize64 ||
            bytes[0] != 0x7f ||
            !bytes[1..].StartsWith("ELF"u8))
        {
            return CreateFailure(artifact, FirmwareModuleState.RuntimeIncompatible, "Artifact is not a bounded ELF64 image.");
        }
        if (bytes[4] != 2 || bytes[5] != 1 || bytes[6] != 1)
        {
            return CreateFailure(
                artifact,
                FirmwareModuleState.UnsupportedArchitecture,
                "Only little-endian ELF64 version 1 images are supported by the catalog.",
                FirmwareModuleFormat.Elf64);
        }

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);
        var architecture = machine == 0x3e ? "x86-64" : $"elf-machine-0x{machine:x4}";
        if (machine != 0x3e)
        {
            return CreateFailure(
                artifact,
                FirmwareModuleState.UnsupportedArchitecture,
                $"Unsupported ELF machine 0x{machine:x4}.",
                FirmwareModuleFormat.Elf64,
                architecture);
        }

        var entryPoint = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
        var programHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]);
        var programHeaderEntrySize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[54..]);
        var programHeaderCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[56..]);
        if (programHeaderCount > MaximumProgramHeaders ||
            (programHeaderCount > 0 && programHeaderEntrySize < ProgramHeaderSize64) ||
            !IsRangeInside(bytes.Length, programHeaderOffset, (ulong)programHeaderEntrySize * programHeaderCount))
        {
            return CreateFailure(
                artifact,
                FirmwareModuleState.RuntimeIncompatible,
                "ELF program header table is outside the artifact bounds.",
                FirmwareModuleFormat.Elf64,
                architecture,
                entryPoint,
                programHeaderCount);
        }

        var programHeaders = new List<ProgramHeader>(programHeaderCount);
        for (var index = 0; index < programHeaderCount; index++)
        {
            var offset = checked((int)(programHeaderOffset + (ulong)index * programHeaderEntrySize));
            var header = bytes.Slice(offset, ProgramHeaderSize64);
            var parsed = new ProgramHeader(
                BinaryPrimitives.ReadUInt32LittleEndian(header),
                BinaryPrimitives.ReadUInt64LittleEndian(header[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(header[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(header[32..]));
            if (!IsRangeInside(bytes.Length, parsed.Offset, parsed.FileSize))
            {
                return CreateFailure(
                    artifact,
                    FirmwareModuleState.RuntimeIncompatible,
                    $"ELF program segment {index} is outside the artifact bounds.",
                    FirmwareModuleFormat.Elf64,
                    architecture,
                    entryPoint,
                    programHeaderCount);
            }
            programHeaders.Add(parsed);
        }

        var dynamicHeader = programHeaders.FirstOrDefault(header => header.Type == ProgramTypeDynamic);
        var hasDynamicTable = dynamicHeader is not null;
        IReadOnlyList<string> dependencies = [];
        if (dynamicHeader is not null)
        {
            try
            {
                dependencies = ReadDependencies(bytes, dynamicHeader, programHeaders);
            }
            catch (InvalidDataException exception)
            {
                return CreateFailure(
                    artifact,
                    FirmwareModuleState.RuntimeIncompatible,
                    exception.Message,
                    FirmwareModuleFormat.Elf64,
                    architecture,
                    entryPoint,
                    programHeaderCount,
                    hasDynamicTable);
            }
        }

        return new FirmwareModule(
            artifact.VirtualPath,
            artifact.Sha256,
            FirmwareModuleFormat.Elf64,
            FirmwareModuleState.Parseable,
            architecture,
            entryPoint,
            programHeaderCount,
            hasDynamicTable,
            dependencies,
            "ELF metadata is parseable; relocations and initializers have not been validated.");
    }

    private static IReadOnlyList<string> ReadDependencies(
        ReadOnlySpan<byte> bytes,
        ProgramHeader dynamicHeader,
        IReadOnlyList<ProgramHeader> programHeaders)
    {
        if (dynamicHeader.FileSize / 16 > MaximumDynamicEntries)
        {
            throw new InvalidDataException("ELF dynamic table exceeds the catalog limit.");
        }

        ulong? stringTableAddress = null;
        ulong? stringTableSize = null;
        var neededOffsets = new List<ulong>();
        var entryCount = dynamicHeader.FileSize / 16;
        for (ulong index = 0; index < entryCount; index++)
        {
            var offset = checked((int)(dynamicHeader.Offset + index * 16));
            var tag = BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
            var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes[(offset + 8)..]);
            if (tag == DynamicNull)
            {
                break;
            }

            switch (tag)
            {
                case DynamicNeeded:
                    neededOffsets.Add(value);
                    break;
                case DynamicStringTable:
                    stringTableAddress = value;
                    break;
                case DynamicStringTableSize:
                    stringTableSize = value;
                    break;
            }
        }

        if (neededOffsets.Count == 0)
        {
            return [];
        }
        if (stringTableAddress is null || stringTableSize is null || stringTableSize > int.MaxValue)
        {
            throw new InvalidDataException("ELF dependencies reference a missing or invalid string table.");
        }

        var stringTableOffset = MapVirtualAddressToFileOffset(
            stringTableAddress.Value,
            stringTableSize.Value,
            programHeaders);
        if (stringTableOffset is null || !IsRangeInside(bytes.Length, stringTableOffset.Value, stringTableSize.Value))
        {
            throw new InvalidDataException("ELF dynamic string table is outside loadable segments.");
        }

        var dependencies = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var neededOffset in neededOffsets)
        {
            if (neededOffset >= stringTableSize.Value)
            {
                throw new InvalidDataException("ELF dependency name offset is outside the dynamic string table.");
            }

            var absoluteOffset = checked((int)(stringTableOffset.Value + neededOffset));
            var maximumLength = checked((int)Math.Min(
                Math.Min(stringTableSize.Value - neededOffset, MaximumDependencyNameBytes),
                (ulong)(bytes.Length - absoluteOffset)));
            var nameBytes = bytes.Slice(absoluteOffset, maximumLength);
            var terminator = nameBytes.IndexOf((byte)0);
            if (terminator < 0)
            {
                throw new InvalidDataException("ELF dependency name is not null terminated within its bounds.");
            }

            string name;
            try
            {
                name = StrictUtf8.GetString(nameBytes[..terminator]);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("ELF dependency name is not valid UTF-8.");
            }
            if (string.IsNullOrWhiteSpace(name) ||
                name.Contains('/') ||
                name.Contains('\\') ||
                name.Any(char.IsControl))
            {
                throw new InvalidDataException("ELF dependency name is invalid.");
            }
            dependencies.Add(name);
        }

        return dependencies.ToArray();
    }

    private string ResolveObjectPath(string sha256)
    {
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException($"Invalid firmware object hash: {sha256}");
        }

        return Path.Combine(_objectsRoot, sha256[..2], sha256);
    }

    private static ulong? MapVirtualAddressToFileOffset(
        ulong address,
        ulong size,
        IReadOnlyList<ProgramHeader> programHeaders)
    {
        foreach (var header in programHeaders)
        {
            if (header.Type != ProgramTypeLoad || address < header.VirtualAddress)
            {
                continue;
            }

            var relative = address - header.VirtualAddress;
            if (relative <= header.FileSize && size <= header.FileSize - relative)
            {
                return checked(header.Offset + relative);
            }
        }

        return null;
    }

    private static bool IsRangeInside(int length, ulong offset, ulong size) =>
        offset <= (ulong)length && size <= (ulong)length - offset;

    private static async Task<byte[]> ReadModuleBytesAsync(
        string path,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedSize || stream.Length > MaximumModuleBytes)
        {
            throw new IOException("CAS object changed before module cataloging.");
        }

        var bytes = new byte[checked((int)expectedSize)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new IOException("CAS object grew while module cataloging was in progress.");
        }
        return bytes;
    }

    private static FirmwareModule CreateFailure(
        FirmwareArtifact artifact,
        FirmwareModuleState state,
        string reason,
        FirmwareModuleFormat format = FirmwareModuleFormat.Unknown,
        string? architecture = null,
        ulong? entryPoint = null,
        int programHeaderCount = 0,
        bool hasDynamicTable = false) => new(
            artifact.VirtualPath,
            artifact.Sha256,
            format,
            state,
            architecture,
            entryPoint,
            programHeaderCount,
            hasDynamicTable,
            [],
            reason);

    private static string ComputeCatalogHash(IReadOnlyList<FirmwareModule> modules)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[sizeof(ulong)];
        foreach (var module in modules)
        {
            AppendString(hasher, module.VirtualPath);
            AppendString(hasher, module.Sha256);
            hasher.AppendData([(byte)module.Format, (byte)module.State]);
            AppendString(hasher, module.Architecture ?? string.Empty);
            BinaryPrimitives.WriteUInt64LittleEndian(number, module.EntryPoint ?? 0);
            hasher.AppendData(number);
            BinaryPrimitives.WriteUInt64LittleEndian(number, checked((ulong)module.ProgramHeaderCount));
            hasher.AppendData(number);
            hasher.AppendData([module.HasDynamicTable ? (byte)1 : (byte)0]);
            foreach (var dependency in module.Dependencies)
            {
                AppendString(hasher, dependency);
            }
            AppendString(hasher, module.Reason ?? string.Empty);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hasher, string value)
    {
        hasher.AppendData(Encoding.UTF8.GetBytes(value));
        hasher.AppendData([0]);
    }

    private sealed record ProgramHeader(uint Type, ulong Offset, ulong VirtualAddress, ulong FileSize);
}
