// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Touche.PS5.Modules;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Maps verified firmware images into core memory. A module mapping remains
/// owned by this factory after commit and can be released by module identity.
/// </summary>
public sealed class CoreLleGuestMemoryTransactionFactory : ILleGuestMemoryTransactionFactory, IDisposable
{
    private const ulong PageSize = 0x1000;
    private const int ZeroChunkSize = 64 * 1024;

    private readonly IVirtualMemory _memory;
    private readonly IGuestAddressSpace _addressSpace;
    private readonly IReleasableVirtualMemory _releasableMemory;
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly object _ownershipGate = new();
    private readonly Dictionary<ModuleKey, ulong> _ownedMappings = [];
    private bool _disposed;

    public CoreLleGuestMemoryTransactionFactory(IVirtualMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _addressSpace = memory as IGuestAddressSpace ??
            throw new ArgumentException("Virtual memory cannot map fixed guest regions.", nameof(memory));
        _releasableMemory = memory as IReleasableVirtualMemory ??
            throw new ArgumentException("Virtual memory cannot release guest regions.", nameof(memory));
    }

    public async ValueTask<ILleGuestMemoryTransaction> BeginAsync(
        string moduleVirtualPath,
        ulong runtimeImageStart,
        ulong imageSize,
        CancellationToken cancellationToken = default)
    {
        ValidateModuleKey(moduleVirtualPath, runtimeImageStart, imageSize);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = new ModuleKey(moduleVirtualPath, runtimeImageStart);
            lock (_ownershipGate)
            {
                if (_ownedMappings.ContainsKey(key))
                {
                    throw new InvalidOperationException("The LLE module is already mapped.");
                }
            }

            var mappingStart = AlignDown(runtimeImageStart, PageSize);
            var mappingEnd = AlignUp(checked(runtimeImageStart + imageSize), PageSize);
            var mappedAddress = _addressSpace.AllocateAt(
                mappingStart,
                mappingEnd - mappingStart,
                executable: true,
                allowAlternative: false);
            if (mappedAddress != mappingStart)
            {
                _ = _releasableMemory.TryReleaseMapping(mappedAddress);
                throw new InvalidOperationException("The core did not honor the fixed LLE mapping address.");
            }
            return new Transaction(this, key, mappingStart, mappingEnd, runtimeImageStart, imageSize);
        }
        catch
        {
            _transactionGate.Release();
            throw;
        }
    }

    public bool TryReleaseModule(string moduleVirtualPath, ulong runtimeImageStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleVirtualPath);
        var key = new ModuleKey(moduleVirtualPath, runtimeImageStart);
        ulong address;
        lock (_ownershipGate)
        {
            if (!_ownedMappings.TryGetValue(key, out address))
            {
                return false;
            }
        }
        if (!_releasableMemory.TryReleaseMapping(address))
        {
            return false;
        }
        lock (_ownershipGate)
        {
            _ownedMappings.Remove(key);
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (!_transactionGate.Wait(0))
        {
            throw new InvalidOperationException("Cannot dispose the LLE memory factory while a transaction is active.");
        }
        _disposed = true;
        ulong[] mappings;
        lock (_ownershipGate)
        {
            mappings = _ownedMappings.Values.Distinct().ToArray();
            _ownedMappings.Clear();
        }
        foreach (var mapping in mappings)
        {
            _ = _releasableMemory.TryReleaseMapping(mapping);
        }
        _transactionGate.Dispose();
    }

    private void RegisterCommitted(ModuleKey key, ulong mappingStart)
    {
        lock (_ownershipGate)
        {
            if (!_ownedMappings.TryAdd(key, mappingStart))
            {
                throw new InvalidOperationException("The LLE module was mapped concurrently.");
            }
        }
    }

    private static void ValidateModuleKey(string path, ulong runtimeImageStart, ulong imageSize)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !path.StartsWith("/", StringComparison.Ordinal) ||
            path.Contains('\\') ||
            path.Contains("//", StringComparison.Ordinal) ||
            path.Any(char.IsControl) ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(component => component is "." or "..") ||
            runtimeImageStart == 0 ||
            imageSize == 0 ||
            imageSize > ulong.MaxValue - runtimeImageStart ||
            runtimeImageStart + imageSize > ulong.MaxValue - (PageSize - 1))
        {
            throw new ArgumentException("Invalid LLE module transaction identity.", nameof(path));
        }
    }

    private static ulong AlignDown(ulong value, ulong alignment) => value & ~(alignment - 1);

    private static ulong AlignUp(ulong value, ulong alignment) =>
        checked((value + alignment - 1) & ~(alignment - 1));

    private sealed class Transaction(
        CoreLleGuestMemoryTransactionFactory owner,
        ModuleKey key,
        ulong mappingStart,
        ulong mappingEnd,
        ulong imageStart,
        ulong imageSize) : ILleGuestMemoryTransaction
    {
        private readonly List<StagedSegment> _segments = [];
        private bool _committed;
        private bool _disposed;

        public ValueTask StageSegmentAsync(
            ulong runtimeAddress,
            ulong memorySize,
            ulong sourceFileOffset,
            ReadOnlyMemory<byte> initialData,
            LleSegmentPermissions finalPermissions,
            CancellationToken cancellationToken = default)
        {
            ThrowIfClosed();
            cancellationToken.ThrowIfCancellationRequested();
            if (memorySize == 0 ||
                (ulong)initialData.Length > memorySize ||
                runtimeAddress < imageStart ||
                runtimeAddress > ulong.MaxValue - memorySize ||
                runtimeAddress + memorySize > imageStart + imageSize ||
                (finalPermissions & ~(LleSegmentPermissions.Read |
                    LleSegmentPermissions.Write |
                    LleSegmentPermissions.Execute)) != 0 ||
                _segments.Any(segment => RangesOverlap(
                    runtimeAddress,
                    memorySize,
                    segment.Address,
                    segment.Size)))
            {
                throw new InvalidDataException("Invalid or overlapping LLE segment mapping.");
            }

            if (!initialData.IsEmpty && !owner._memory.TryWrite(runtimeAddress, initialData.Span))
            {
                throw new IOException("Unable to write the initial LLE segment data.");
            }
            ZeroFill(
                checked(runtimeAddress + (ulong)initialData.Length),
                memorySize - (ulong)initialData.Length,
                cancellationToken);
            _segments.Add(new StagedSegment(
                runtimeAddress,
                memorySize,
                sourceFileOffset,
                finalPermissions));
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfClosed();
            cancellationToken.ThrowIfCancellationRequested();
            if (_segments.Count == 0)
            {
                throw new InvalidOperationException("An LLE mapping cannot commit without segments.");
            }

            if (!owner._addressSpace.TryProtect(
                    mappingStart,
                    mappingEnd - mappingStart,
                    GuestPageProtection.None))
            {
                throw new IOException("Unable to protect the unused LLE image pages.");
            }

            var pages = BuildPagePermissions();
            foreach (var run in MergePermissionRuns(pages))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!owner._addressSpace.TryProtect(run.Address, run.Size, run.Permissions))
                {
                    throw new IOException("Unable to apply the final LLE segment permissions.");
                }
            }

            owner.RegisterCommitted(key, mappingStart);
            _committed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }
            _disposed = true;
            if (!_committed)
            {
                _ = owner._releasableMemory.TryReleaseMapping(mappingStart);
            }
            owner._transactionGate.Release();
            return ValueTask.CompletedTask;
        }

        private void ZeroFill(ulong address, ulong length, CancellationToken cancellationToken)
        {
            if (length == 0)
            {
                return;
            }
            var zeroes = new byte[(int)Math.Min((ulong)ZeroChunkSize, length)];
            while (length != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min((ulong)zeroes.Length, length);
                if (!owner._memory.TryWrite(address, zeroes.AsSpan(0, count)))
                {
                    throw new IOException("Unable to zero-fill the LLE segment.");
                }
                address += (ulong)count;
                length -= (ulong)count;
            }
        }

        private SortedDictionary<ulong, GuestPageProtection> BuildPagePermissions()
        {
            var result = new SortedDictionary<ulong, GuestPageProtection>();
            foreach (var segment in _segments)
            {
                var permissions = ConvertPermissions(segment.Permissions);
                var end = AlignUp(segment.Address + segment.Size, PageSize);
                for (var page = AlignDown(segment.Address, PageSize); page < end; page += PageSize)
                {
                    result.TryGetValue(page, out var existing);
                    result[page] = existing | permissions;
                }
            }
            return result;
        }

        private static IReadOnlyList<PermissionRun> MergePermissionRuns(
            SortedDictionary<ulong, GuestPageProtection> pages)
        {
            var result = new List<PermissionRun>();
            foreach (var page in pages)
            {
                if (result.Count != 0)
                {
                    var previous = result[^1];
                    if (previous.Address + previous.Size == page.Key && previous.Permissions == page.Value)
                    {
                        result[^1] = previous with { Size = previous.Size + PageSize };
                        continue;
                    }
                }
                result.Add(new PermissionRun(page.Key, PageSize, page.Value));
            }
            return result;
        }

        private static GuestPageProtection ConvertPermissions(LleSegmentPermissions permissions) =>
            (GuestPageProtection)(int)permissions;

        private static bool RangesOverlap(ulong left, ulong leftSize, ulong right, ulong rightSize) =>
            left < right + rightSize && right < left + leftSize;

        private void ThrowIfClosed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed)
            {
                throw new InvalidOperationException("The LLE mapping transaction is already committed.");
            }
        }

        private sealed record StagedSegment(
            ulong Address,
            ulong Size,
            ulong SourceFileOffset,
            LleSegmentPermissions Permissions);

        private sealed record PermissionRun(
            ulong Address,
            ulong Size,
            GuestPageProtection Permissions);
    }

    private readonly record struct ModuleKey(string VirtualPath, ulong RuntimeImageStart);
}
