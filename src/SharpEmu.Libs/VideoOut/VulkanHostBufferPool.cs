// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct VulkanHostBufferPoolKey(
    BufferUsageFlags Usage,
    ulong Capacity);

internal readonly record struct VulkanHostBufferAllocation(
    VkBuffer Buffer,
    DeviceMemory Memory,
    VulkanHostBufferPoolKey Key,
    nint Mapped,
    long LeaseId = 0,
    ulong WrittenLength = 0);

internal sealed class VulkanHostBufferPool : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<VulkanHostBufferPoolKey, Stack<VulkanHostBufferAllocation>>
        _available = [];
    private readonly Dictionary<ulong, VulkanHostBufferAllocation> _allocations = [];
    private readonly HashSet<ulong> _cachedHandles = [];
    private readonly Action<VulkanHostBufferAllocation> _destroy;
    private long _nextLeaseId;
    private ulong _cachedBytes;
    private bool _disposed;

    public VulkanHostBufferPool(
        ulong maximumCachedBytes,
        Action<VulkanHostBufferAllocation> destroy)
    {
        MaximumCachedBytes = maximumCachedBytes;
        _destroy = destroy;
    }

    public ulong MaximumCachedBytes { get; }

    public ulong CachedBytes
    {
        get
        {
            lock (_gate)
            {
                return _cachedBytes;
            }
        }
    }

    public bool TryRent(
        VulkanHostBufferPoolKey key,
        out VulkanHostBufferAllocation allocation)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                allocation = default;
                return false;
            }

            if (!_available.TryGetValue(key, out var available) ||
                !available.TryPop(out allocation))
            {
                allocation = default;
                return false;
            }

            _cachedHandles.Remove(allocation.Buffer.Handle);
            _cachedBytes -= allocation.Key.Capacity;
            allocation = allocation with { LeaseId = NextLeaseIdLocked() };
            _allocations[allocation.Buffer.Handle] = allocation;
            return true;
        }
    }

    public VulkanHostBufferAllocation Register(VulkanHostBufferAllocation allocation)
    {
        if (allocation.Buffer.Handle == 0)
        {
            throw new ArgumentException("A pooled buffer must have a valid handle.", nameof(allocation));
        }
        if (allocation.Memory.Handle == 0)
        {
            throw new ArgumentException("A pooled buffer must have valid memory.", nameof(allocation));
        }
        if (allocation.WrittenLength > allocation.Key.Capacity)
        {
            throw new ArgumentException("Written length exceeds the pooled buffer capacity.", nameof(allocation));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            allocation = allocation with { LeaseId = NextLeaseIdLocked() };
            _allocations.Add(allocation.Buffer.Handle, allocation);
            return allocation;
        }
    }

    public bool UpdateWrittenLength(
        VulkanHostBufferAllocation allocation,
        ulong writtenLength)
    {
        if (writtenLength > allocation.Key.Capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(writtenLength),
                "Written length exceeds the pooled buffer capacity.");
        }

        lock (_gate)
        {
            if (_disposed ||
                !_allocations.TryGetValue(allocation.Buffer.Handle, out var current) ||
                current.Memory.Handle != allocation.Memory.Handle ||
                current.LeaseId != allocation.LeaseId ||
                _cachedHandles.Contains(allocation.Buffer.Handle))
            {
                return false;
            }

            _allocations[allocation.Buffer.Handle] = current with
            {
                WrittenLength = writtenLength,
            };
            return true;
        }
    }

    public bool Return(VulkanHostBufferAllocation allocation)
    {
        VulkanHostBufferAllocation? toDestroy = null;
        lock (_gate)
        {
            if (_disposed ||
                !_allocations.TryGetValue(allocation.Buffer.Handle, out var current) ||
                current.Memory.Handle != allocation.Memory.Handle ||
                current.LeaseId != allocation.LeaseId)
            {
                return false;
            }

            if (!_cachedHandles.Add(allocation.Buffer.Handle))
            {
                return true;
            }

            if (current.Key.Capacity > MaximumCachedBytes ||
                _cachedBytes > MaximumCachedBytes - current.Key.Capacity)
            {
                _cachedHandles.Remove(current.Buffer.Handle);
                _allocations.Remove(current.Buffer.Handle);
                toDestroy = current;
            }
            else
            {
                if (!_available.TryGetValue(current.Key, out var available))
                {
                    available = [];
                    _available.Add(current.Key, available);
                }

                available.Push(current);
                _cachedBytes += current.Key.Capacity;
            }
        }

        // Destroy outside the lock — _destroy calls into Vulkan which may
        // grab device-level locks, and holding _gate while doing so risks
        // a lock-ordering deadlock with a thread that holds the device lock
        // and is waiting on _gate.
        if (toDestroy is { } allocationToDestroy)
        {
            _destroy(allocationToDestroy);
        }

        return true;
    }

    public void Dispose()
    {
        // Snapshot under the lock, destroy outside — _destroy calls into
        // Vulkan which may grab device-level locks; holding _gate while
        // doing so risks a lock-ordering deadlock with any thread that
        // acquires the device lock first and then waits on _gate.
        List<VulkanHostBufferAllocation> toDestroy;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toDestroy = new List<VulkanHostBufferAllocation>(_allocations.Values);
            _allocations.Clear();
            _available.Clear();
            _cachedHandles.Clear();
            _cachedBytes = 0;
        }

        foreach (var allocation in toDestroy)
        {
            _destroy(allocation);
        }
    }

    private long NextLeaseIdLocked()
    {
        _nextLeaseId++;
        if (_nextLeaseId == 0)
        {
            _nextLeaseId++;
        }

        return _nextLeaseId;
    }
}
