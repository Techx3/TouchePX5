// Copyright (C) 2026 Touché PX5 Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.GUI;

public enum FirmwareContainerKind
{
    OfficialSlb2,
    DecryptedPup,
    BackupArchiveSiecaf,
}

public sealed record FirmwarePackageInspection(
    FirmwareContainerKind Kind,
    string FormatLabel,
    int? EntryCount,
    bool HasVersionMetadataEntry,
    bool CanInspectEntries,
    IReadOnlyList<FirmwarePackageEntry> Entries);

public sealed record FirmwarePackageEntry(
    int Index,
    uint Flags,
    uint Id,
    ulong Offset,
    ulong StoredSize,
    ulong UnpackedSize,
    bool IsCompressed,
    bool IsBlocked,
    bool IsSpecial)
{
    public bool CanExtractDirectly => !IsCompressed && !IsBlocked && !IsSpecial && StoredSize > 0;

    public string FileName => $"entry-{Index:D4}-id-{Id:x3}.bin";
}

/// <summary>
/// Performs bounded, read-only inspection of firmware package headers. The
/// decrypted-PUP layout is implemented independently from publicly observable
/// format fields; no decryption material or proprietary data is included.
/// </summary>
public static class FirmwarePackageInspector
{
    private const uint DecryptedPupMagic = 0xEEF51454;
    private const int DecryptedHeaderSize = 32;
    private const int DecryptedEntrySize = 32;
    private const int MaximumEntryCount = 4096;
    private const uint VersionMetadataEntryId = 0x0C;
    private const int SiecafHeaderSize = 0x58;
    private const int SiecafSegmentMetadataSize = 0x40;
    private const int SiecafSegmentHashSize = 0x30;
    private const ulong MaximumSiecafSegmentCount = 4096;
    private static readonly byte[] Slb2Magic = "SLB2"u8.ToArray();
    private static readonly byte[] SiecafMagic = "SIECAF\0\0"u8.ToArray();

    public static async Task<FirmwarePackageInspection> InspectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length < DecryptedHeaderSize)
        {
            throw new InvalidDataException("The firmware package is too small to contain a valid header.");
        }

        await using var stream = new FileStream(
            fileInfo.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[SiecafHeaderSize];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);

        if (header.AsSpan(0, Slb2Magic.Length).SequenceEqual(Slb2Magic))
        {
            return new FirmwarePackageInspection(
                FirmwareContainerKind.OfficialSlb2,
                "Official SLB2",
                null,
                false,
                false,
                []);
        }

        if (header.AsSpan(0, SiecafMagic.Length).SequenceEqual(SiecafMagic))
        {
            return InspectSiecaf(header, fileInfo.Length);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != DecryptedPupMagic)
        {
            throw new InvalidDataException("The selected file is neither an SLB2 nor a decrypted PS5 PUP container.");
        }

        var declaredHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x0C));
        var declaredFileSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x10));
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x18));
        var minimumTableEnd = checked(DecryptedHeaderSize + entryCount * DecryptedEntrySize);
        if (entryCount == 0 ||
            entryCount > MaximumEntryCount ||
            declaredHeaderSize < DecryptedHeaderSize ||
            declaredHeaderSize < minimumTableEnd ||
            declaredHeaderSize > fileInfo.Length ||
            minimumTableEnd > fileInfo.Length ||
            declaredFileSize > (ulong)fileInfo.Length)
        {
            throw new InvalidDataException("The decrypted PUP header contains invalid bounds.");
        }

        var entryTable = new byte[entryCount * DecryptedEntrySize];
        stream.Position = DecryptedHeaderSize;
        await ReadExactlyAsync(stream, entryTable, cancellationToken).ConfigureAwait(false);
        var hasVersionMetadata = false;
        ulong directExtractionSize = 0;
        var entries = new List<FirmwarePackageEntry>(entryCount);
        for (var index = 0; index < entryCount; index++)
        {
            var entry = entryTable.AsSpan(index * DecryptedEntrySize, DecryptedEntrySize);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            var storedSize = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var unpackedSize = BinaryPrimitives.ReadUInt64LittleEndian(entry[24..]);
            if ((storedSize > 0 && offset < declaredHeaderSize) ||
                offset > (ulong)fileInfo.Length ||
                storedSize > (ulong)fileInfo.Length - offset)
            {
                throw new InvalidDataException($"Decrypted PUP entry {index} points outside the package.");
            }

            var id = flags >> 20;
            var isCompressed = (flags & 0x8) != 0;
            var isBlocked = (flags & 0x800) != 0;
            var specialFlags = flags & 0xF0000000;
            var isSpecial = specialFlags is 0xE0000000 or 0xF0000000;
            var packageEntry = new FirmwarePackageEntry(
                index,
                flags,
                id,
                offset,
                storedSize,
                unpackedSize,
                isCompressed,
                isBlocked,
                isSpecial);
            entries.Add(packageEntry);

            if (packageEntry.CanExtractDirectly)
            {
                var maximumExtractionSize = (ulong)fileInfo.Length;
                if (storedSize > maximumExtractionSize - directExtractionSize)
                {
                    throw new InvalidDataException("The decrypted PUP requests an excessive direct extraction size.");
                }

                directExtractionSize += storedSize;
            }

            if (id == VersionMetadataEntryId)
            {
                hasVersionMetadata = true;
            }
        }

        return new FirmwarePackageInspection(
            FirmwareContainerKind.DecryptedPup,
            "Decrypted PUP",
            entryCount,
            hasVersionMetadata,
            true,
            entries);
    }

    private static FirmwarePackageInspection InspectSiecaf(ReadOnlySpan<byte> header, long fileLength)
    {
        var version = BinaryPrimitives.ReadUInt64LittleEndian(header[0x08..]);
        var segmentCount = BinaryPrimitives.ReadUInt64LittleEndian(header[0x40..]);
        var dataOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[0x48..]);
        var dataSize = BinaryPrimitives.ReadUInt64LittleEndian(header[0x50..]);

        if (version != 1 || segmentCount is 0 or > MaximumSiecafSegmentCount)
        {
            throw new InvalidDataException("The SIECAF backup header contains an unsupported version or segment count.");
        }

        var metadataEnd = checked(
            (ulong)SiecafHeaderSize +
            segmentCount * (SiecafSegmentMetadataSize + SiecafSegmentHashSize));
        if (dataOffset < metadataEnd ||
            dataOffset > (ulong)fileLength ||
            dataSize > (ulong)fileLength - dataOffset)
        {
            throw new InvalidDataException("The SIECAF backup header contains invalid bounds.");
        }

        return new FirmwarePackageInspection(
            FirmwareContainerKind.BackupArchiveSiecaf,
            "PS5 Backup (SIECAF)",
            checked((int)segmentCount),
            false,
            false,
            []);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = await stream.ReadAsync(destination[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The firmware package ended before its header was complete.");
            }

            totalRead += read;
        }
    }
}
