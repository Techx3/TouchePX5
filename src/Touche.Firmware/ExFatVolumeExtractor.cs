// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;

namespace Touche.Firmware;

public sealed record ExFatExtractionOptions
{
    public int MaximumFileCount { get; init; } = 200_000;

    public int MaximumDirectoryDepth { get; init; } = 64;

    public long MaximumSingleFileBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    public long MaximumTotalBytes { get; init; } = 64L * 1024 * 1024 * 1024;

    public long MaximumDirectoryBytes { get; init; } = 64L * 1024 * 1024;
}

public sealed record ExFatExtractionResult(
    string ImagePath,
    string DestinationDirectory,
    int FileCount,
    int DirectoryCount,
    long ExtractedBytes);

/// <summary>
/// Bounded, read-only exFAT 1.x extractor. It supports both FAT-chained and
/// contiguous allocations and never writes to the source volume image.
/// </summary>
public sealed class ExFatVolumeExtractor
{
    private const int BootRegionBytes = 512;
    private const int DirectoryEntryBytes = 32;
    private const byte FileEntryType = 0x85;
    private const byte StreamExtensionEntryType = 0xC0;
    private const byte FileNameEntryType = 0xC1;
    private const ushort DirectoryAttribute = 0x0010;
    private const byte AllocationPossibleFlag = 0x01;
    private const byte NoFatChainFlag = 0x02;
    private const uint FirstDataCluster = 2;
    private const uint BadCluster = 0xFFFFFFF7;
    private const uint EndOfChain = 0xFFFFFFF8;

    public async Task<ExFatExtractionResult> ExtractAsync(
        string imagePath,
        string destinationDirectory,
        ExFatExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        options ??= new ExFatExtractionOptions();
        ValidateOptions(options);

        var fullImagePath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullImagePath))
        {
            throw new FileNotFoundException("The exFAT volume image was not found.", fullImagePath);
        }
        if ((File.GetAttributes(fullImagePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("exFAT volume images cannot be links or reparse points.");
        }

        var fullDestination = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(fullDestination);
        if ((File.GetAttributes(fullDestination) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The exFAT extraction destination cannot be a link or reparse point.");
        }

        await using var image = new FileStream(
            fullImagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var volume = await Volume.OpenAsync(image, options, cancellationToken).ConfigureAwait(false);
        var state = new ExtractionState(fullImagePath, fullDestination, options);
        await volume.ExtractDirectoryAsync(
            volume.RootDirectoryCluster,
            dataLength: null,
            noFatChain: false,
            relativeDirectory: string.Empty,
            depth: 0,
            state,
            cancellationToken).ConfigureAwait(false);
        return new ExFatExtractionResult(
            fullImagePath,
            fullDestination,
            state.FileCount,
            state.DirectoryCount,
            state.TotalBytes);
    }

    private static void ValidateOptions(ExFatExtractionOptions options)
    {
        if (options.MaximumFileCount <= 0 ||
            options.MaximumDirectoryDepth <= 0 ||
            options.MaximumSingleFileBytes <= 0 ||
            options.MaximumTotalBytes <= 0 ||
            options.MaximumDirectoryBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "exFAT extraction limits must be positive.");
        }
    }

    private sealed class Volume
    {
        private readonly FileStream _image;
        private readonly ExFatExtractionOptions _options;
        private readonly long _fatOffset;
        private readonly long _clusterHeapOffset;
        private readonly uint _clusterCount;
        private readonly int _clusterSize;
        private readonly long _physicalLength;

        private Volume(
            FileStream image,
            ExFatExtractionOptions options,
            long fatOffset,
            long clusterHeapOffset,
            uint clusterCount,
            uint rootDirectoryCluster,
            int clusterSize)
        {
            _image = image;
            _options = options;
            _fatOffset = fatOffset;
            _clusterHeapOffset = clusterHeapOffset;
            _clusterCount = clusterCount;
            RootDirectoryCluster = rootDirectoryCluster;
            _clusterSize = clusterSize;
            _physicalLength = image.Length;
        }

        public uint RootDirectoryCluster { get; }

        public static async Task<Volume> OpenAsync(
            FileStream image,
            ExFatExtractionOptions options,
            CancellationToken cancellationToken)
        {
            var boot = new byte[BootRegionBytes];
            await ReadExactlyAtAsync(image, 0, boot, cancellationToken).ConfigureAwait(false);
            if (!boot.AsSpan(3, 8).SequenceEqual("EXFAT   "u8) ||
                boot[510] != 0x55 ||
                boot[511] != 0xAA)
            {
                throw new InvalidDataException("The selected image is not an exFAT volume.");
            }
            if (boot.AsSpan(11, 53).IndexOfAnyExcept((byte)0) >= 0)
            {
                throw new InvalidDataException("The exFAT boot sector reserved region is invalid.");
            }

            var volumeLengthSectors = BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(72, 8));
            var fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(80, 4));
            var fatLengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(84, 4));
            var clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(88, 4));
            var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(92, 4));
            var rootDirectoryCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(96, 4));
            var revision = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(104, 2));
            var bytesPerSectorShift = boot[108];
            var sectorsPerClusterShift = boot[109];
            var numberOfFats = boot[110];
            if ((revision >> 8) != 1 ||
                bytesPerSectorShift is < 9 or > 12 ||
                sectorsPerClusterShift > 25 - bytesPerSectorShift ||
                numberOfFats is < 1 or > 2 ||
                clusterCount == 0 ||
                volumeLengthSectors == 0)
            {
                throw new InvalidDataException("The exFAT volume geometry is invalid or unsupported.");
            }

            var sectorSize = 1L << bytesPerSectorShift;
            var clusterSizeLong = sectorSize << sectorsPerClusterShift;
            if (clusterSizeLong > int.MaxValue)
            {
                throw new InvalidDataException("The exFAT cluster size is unsupported.");
            }
            var volumeLength = checked((long)volumeLengthSectors * sectorSize);
            var fatOffset = checked((long)fatOffsetSectors * sectorSize);
            var fatLength = checked((long)fatLengthSectors * sectorSize);
            var clusterHeapOffset = checked((long)clusterHeapOffsetSectors * sectorSize);
            var clusterHeapLength = checked((long)clusterCount * clusterSizeLong);
            if (fatOffset < BootRegionBytes ||
                fatLength < checked(((long)clusterCount + FirstDataCluster) * sizeof(uint)) ||
                fatOffset + fatLength > volumeLength ||
                clusterHeapOffset < fatOffset + fatLength ||
                clusterHeapOffset + clusterHeapLength > volumeLength ||
                !IsDataCluster(rootDirectoryCluster, clusterCount))
            {
                throw new InvalidDataException("The exFAT allocation geometry is inconsistent.");
            }

            return new Volume(
                image,
                options,
                fatOffset,
                clusterHeapOffset,
                clusterCount,
                rootDirectoryCluster,
                (int)clusterSizeLong);
        }

        public async Task ExtractDirectoryAsync(
            uint firstCluster,
            long? dataLength,
            bool noFatChain,
            string relativeDirectory,
            int depth,
            ExtractionState state,
            CancellationToken cancellationToken)
        {
            if (depth > _options.MaximumDirectoryDepth)
            {
                throw new InvalidDataException("The exFAT directory depth limit was exceeded.");
            }

            var directoryBytes = await ReadAllocationAsync(
                firstCluster,
                dataLength,
                noFatChain,
                _options.MaximumDirectoryBytes,
                cancellationToken).ConfigureAwait(false);
            state.DirectoryCount++;
            for (var offset = 0; offset + DirectoryEntryBytes <= directoryBytes.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var primary = directoryBytes.AsSpan(offset, DirectoryEntryBytes);
                var entryType = primary[0];
                if (entryType == 0)
                {
                    break;
                }
                if ((entryType & 0x80) == 0 || entryType != FileEntryType)
                {
                    offset += DirectoryEntryBytes;
                    continue;
                }

                var secondaryCount = primary[1];
                var setLength = checked((secondaryCount + 1) * DirectoryEntryBytes);
                if (secondaryCount < 2 || offset + setLength > directoryBytes.Length)
                {
                    throw new InvalidDataException("An exFAT file directory entry set is truncated.");
                }
                var entrySet = directoryBytes.AsSpan(offset, setLength);
                var expectedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(primary[2..4]);
                if (ComputeEntrySetChecksum(entrySet) != expectedChecksum)
                {
                    throw new InvalidDataException("An exFAT file directory entry set failed checksum validation.");
                }

                var stream = entrySet.Slice(DirectoryEntryBytes, DirectoryEntryBytes);
                if (stream[0] != StreamExtensionEntryType ||
                    (stream[1] & AllocationPossibleFlag) == 0)
                {
                    throw new InvalidDataException("An exFAT file entry has an invalid stream extension.");
                }
                var nameLength = stream[3];
                var validDataLength = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(stream[8..16]));
                var entryFirstCluster = BinaryPrimitives.ReadUInt32LittleEndian(stream[20..24]);
                var allocationLength = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(stream[24..32]));
                if (validDataLength > allocationLength || allocationLength > _options.MaximumSingleFileBytes)
                {
                    throw new InvalidDataException("An exFAT file length is invalid or exceeds the configured limit.");
                }

                var name = DecodeName(entrySet, secondaryCount, nameLength);
                ValidatePathComponent(name);
                var relativePath = string.IsNullOrEmpty(relativeDirectory)
                    ? name
                    : relativeDirectory + '/' + name;
                var attributes = BinaryPrimitives.ReadUInt16LittleEndian(primary[4..6]);
                var isDirectory = (attributes & DirectoryAttribute) != 0;
                var entryNoFatChain = (stream[1] & NoFatChainFlag) != 0;
                if (isDirectory)
                {
                    if (allocationLength == 0 || !IsDataCluster(entryFirstCluster, _clusterCount))
                    {
                        throw new InvalidDataException("An exFAT directory has no valid allocation.");
                    }
                    var destination = state.ResolveDestination(relativePath);
                    Directory.CreateDirectory(destination);
                    await ExtractDirectoryAsync(
                        entryFirstCluster,
                        allocationLength,
                        entryNoFatChain,
                        relativePath,
                        depth + 1,
                        state,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    state.AddFile(validDataLength);
                    var destination = state.ResolveDestination(relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    await ExtractFileAsync(
                        entryFirstCluster,
                        allocationLength,
                        validDataLength,
                        entryNoFatChain,
                        destination,
                        cancellationToken).ConfigureAwait(false);
                }

                offset += setLength;
            }
        }

        private async Task ExtractFileAsync(
            uint firstCluster,
            long allocationLength,
            long validDataLength,
            bool noFatChain,
            string destination,
            CancellationToken cancellationToken)
        {
            if (validDataLength == 0)
            {
                await File.WriteAllBytesAsync(destination, [], cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!IsDataCluster(firstCluster, _clusterCount))
            {
                throw new InvalidDataException("An exFAT file has an invalid first cluster.");
            }

            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var remaining = validDataLength;
            await foreach (var cluster in EnumerateClustersAsync(
                               firstCluster,
                               allocationLength,
                               noFatChain,
                               cancellationToken))
            {
                var copyLength = (int)Math.Min(_clusterSize, remaining);
                if (copyLength == 0)
                {
                    break;
                }
                var buffer = new byte[copyLength];
                await ReadExactlyAtAsync(_image, GetClusterOffset(cluster), buffer, cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                remaining -= copyLength;
            }
            if (remaining != 0)
            {
                throw new EndOfStreamException("The exFAT file allocation ended before its valid data length.");
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<byte[]> ReadAllocationAsync(
            uint firstCluster,
            long? allocationLength,
            bool noFatChain,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            if (!IsDataCluster(firstCluster, _clusterCount))
            {
                throw new InvalidDataException("The exFAT allocation has an invalid first cluster.");
            }

            var capacity = allocationLength is > 0
                ? (int)Math.Min(allocationLength.Value, maximumBytes)
                : _clusterSize;
            using var output = new MemoryStream(capacity);
            await foreach (var cluster in EnumerateClustersAsync(
                               firstCluster,
                               allocationLength,
                               noFatChain,
                               cancellationToken))
            {
                var remainingDeclared = allocationLength.HasValue
                    ? allocationLength.Value - output.Length
                    : _clusterSize;
                var readLength = (int)Math.Min(_clusterSize, remainingDeclared);
                if (readLength <= 0)
                {
                    break;
                }
                if (output.Length + readLength > maximumBytes)
                {
                    throw new InvalidDataException("An exFAT directory exceeds the configured size limit.");
                }
                var buffer = new byte[readLength];
                await ReadExactlyAtAsync(_image, GetClusterOffset(cluster), buffer, cancellationToken)
                    .ConfigureAwait(false);
                await output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (!allocationLength.HasValue && ContainsEndOfDirectoryEntry(buffer))
                {
                    break;
                }
            }
            return output.ToArray();
        }

        private async IAsyncEnumerable<uint> EnumerateClustersAsync(
            uint firstCluster,
            long? allocationLength,
            bool noFatChain,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var requiredClusters = allocationLength.HasValue
                ? checked((allocationLength.Value + _clusterSize - 1) / _clusterSize)
                : _clusterCount;
            if (requiredClusters > _clusterCount)
            {
                throw new InvalidDataException("The exFAT allocation exceeds the volume cluster count.");
            }

            if (noFatChain)
            {
                for (long index = 0; index < requiredClusters; index++)
                {
                    var cluster = checked(firstCluster + (uint)index);
                    ValidateDataCluster(cluster);
                    yield return cluster;
                }
                yield break;
            }

            var visited = new HashSet<uint>();
            var current = firstCluster;
            for (long index = 0; index < requiredClusters; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateDataCluster(current);
                if (!visited.Add(current))
                {
                    throw new InvalidDataException("The exFAT FAT chain contains a cycle.");
                }
                yield return current;
                var next = await ReadFatEntryAsync(current, cancellationToken).ConfigureAwait(false);
                if (next >= EndOfChain)
                {
                    if (allocationLength.HasValue && index + 1 < requiredClusters)
                    {
                        throw new EndOfStreamException("The exFAT FAT chain is shorter than its allocation length.");
                    }
                    yield break;
                }
                if (next is 0 or 1 || next == BadCluster)
                {
                    throw new InvalidDataException("The exFAT FAT chain contains an invalid cluster.");
                }
                current = next;
            }
        }

        private async Task<uint> ReadFatEntryAsync(uint cluster, CancellationToken cancellationToken)
        {
            var bytes = new byte[sizeof(uint)];
            var offset = checked(_fatOffset + (long)cluster * sizeof(uint));
            await ReadExactlyAtAsync(_image, offset, bytes, cancellationToken).ConfigureAwait(false);
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        private long GetClusterOffset(uint cluster)
        {
            ValidateDataCluster(cluster);
            var offset = checked(_clusterHeapOffset + (long)(cluster - FirstDataCluster) * _clusterSize);
            if (offset < 0 || offset + _clusterSize > _physicalLength)
            {
                throw new EndOfStreamException("The exFAT allocation references data outside the physical image.");
            }
            return offset;
        }

        private void ValidateDataCluster(uint cluster)
        {
            if (!IsDataCluster(cluster, _clusterCount))
            {
                throw new InvalidDataException("The exFAT allocation references an out-of-range cluster.");
            }
        }
    }

    private sealed class ExtractionState(
        string imagePath,
        string destinationDirectory,
        ExFatExtractionOptions options)
    {
        private readonly string _destinationPrefix = Path.TrimEndingDirectorySeparator(destinationDirectory) +
                                                     Path.DirectorySeparatorChar;

        public int FileCount { get; private set; }

        public int DirectoryCount { get; set; }

        public long TotalBytes { get; private set; }

        public void AddFile(long length)
        {
            FileCount++;
            if (FileCount > options.MaximumFileCount)
            {
                throw new InvalidDataException("The exFAT volume exceeds the configured file-count limit.");
            }
            TotalBytes = checked(TotalBytes + length);
            if (TotalBytes > options.MaximumTotalBytes)
            {
                throw new InvalidDataException("The exFAT volume exceeds the configured total-size limit.");
            }
        }

        public string ResolveDestination(string relativePath)
        {
            var components = relativePath.Split('/');
            var candidate = Path.GetFullPath(Path.Combine([destinationDirectory, .. components]));
            if (!candidate.StartsWith(_destinationPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"An exFAT path escapes the extraction destination: {imagePath}");
            }
            return candidate;
        }
    }

    private static string DecodeName(ReadOnlySpan<byte> entrySet, int secondaryCount, int nameLength)
    {
        if (nameLength <= 0 || nameLength > 255)
        {
            throw new InvalidDataException("An exFAT file name length is invalid.");
        }
        var nameBytes = new byte[checked(nameLength * 2)];
        var copiedCharacters = 0;
        for (var secondary = 2; secondary <= secondaryCount && copiedCharacters < nameLength; secondary++)
        {
            var entry = entrySet.Slice(secondary * DirectoryEntryBytes, DirectoryEntryBytes);
            if (entry[0] != FileNameEntryType)
            {
                continue;
            }
            var characterCount = Math.Min(15, nameLength - copiedCharacters);
            entry.Slice(2, characterCount * 2).CopyTo(nameBytes.AsSpan(copiedCharacters * 2));
            copiedCharacters += characterCount;
        }
        if (copiedCharacters != nameLength)
        {
            throw new InvalidDataException("An exFAT file name entry set is incomplete.");
        }
        return Encoding.Unicode.GetString(nameBytes);
    }

    private static void ValidatePathComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.EndsWith(' ') ||
            value.EndsWith('.'))
        {
            throw new InvalidDataException("The exFAT volume contains an unsafe or non-portable file name.");
        }
    }

    private static ushort ComputeEntrySetChecksum(ReadOnlySpan<byte> entrySet)
    {
        ushort checksum = 0;
        for (var index = 0; index < entrySet.Length; index++)
        {
            if (index is 2 or 3)
            {
                continue;
            }
            checksum = (ushort)(((checksum << 15) | (checksum >> 1)) + entrySet[index]);
        }
        return checksum;
    }

    private static bool ContainsEndOfDirectoryEntry(ReadOnlySpan<byte> cluster)
    {
        for (var offset = 0; offset + DirectoryEntryBytes <= cluster.Length; offset += DirectoryEntryBytes)
        {
            if (cluster[offset] == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsDataCluster(uint cluster, uint clusterCount) =>
        cluster >= FirstDataCluster && (ulong)cluster < (ulong)clusterCount + FirstDataCluster;

    private static async Task ReadExactlyAtAsync(
        FileStream stream,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (offset < 0 || offset > stream.Length - destination.Length)
        {
            throw new EndOfStreamException("The exFAT image ended before the requested structure.");
        }
        var total = 0;
        while (total < destination.Length)
        {
            var read = await RandomAccess.ReadAsync(
                stream.SafeFileHandle,
                destination[total..],
                offset + total,
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The exFAT image ended before the requested structure.");
            }
            total += read;
        }
    }
}
