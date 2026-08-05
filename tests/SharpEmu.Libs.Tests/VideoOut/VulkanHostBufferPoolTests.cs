// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanHostBufferPoolTests
{
    [Fact]
    public void ReturnedAllocationCanBeRentedAgain()
    {
        var destroyed = new List<VulkanHostBufferAllocation>();
        using var pool = new VulkanHostBufferPool(1024, destroyed.Add);
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.StorageBufferBit, 256);
        var allocation = Allocation(1, 2, key);

        allocation = pool.Register(allocation);
        Assert.NotEqual(0, allocation.LeaseId);
        Assert.True(pool.Return(allocation));
        Assert.Equal(256UL, pool.CachedBytes);

        Assert.True(pool.TryRent(key, out var rented));
        Assert.Equal(allocation.Buffer, rented.Buffer);
        Assert.Equal(allocation.Memory, rented.Memory);
        Assert.NotEqual(allocation.LeaseId, rented.LeaseId);
        Assert.Equal(0UL, pool.CachedBytes);
        Assert.Empty(destroyed);
    }

    [Fact]
    public void ReturnDestroysAllocationThatWouldExceedBudget()
    {
        var destroyed = new List<VulkanHostBufferAllocation>();
        using var pool = new VulkanHostBufferPool(256, destroyed.Add);
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.VertexBufferBit, 512);
        var allocation = Allocation(3, 4, key);

        allocation = pool.Register(allocation);

        Assert.True(pool.Return(allocation));
        Assert.Equal(0UL, pool.CachedBytes);
        Assert.Equal([allocation], destroyed);
        Assert.False(pool.TryRent(key, out _));
    }

    [Fact]
    public void UnknownAllocationIsNotClaimedByPool()
    {
        using var pool = new VulkanHostBufferPool(1024, _ => { });

        Assert.False(pool.Return(Allocation(9, 10, new(
            BufferUsageFlags.StorageBufferBit,
            256))));
    }

    [Fact]
    public void StaleLeaseCannotReturnAnAllocationRentedByANewerOwner()
    {
        using var pool = new VulkanHostBufferPool(1024, _ => { });
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.VertexBufferBit, 256);
        var firstLease = pool.Register(Allocation(11, 12, key));

        Assert.True(pool.Return(firstLease));
        Assert.True(pool.TryRent(key, out var secondLease));
        Assert.NotEqual(firstLease.LeaseId, secondLease.LeaseId);

        Assert.False(pool.Return(firstLease));
        Assert.True(pool.Return(secondLease));
        Assert.Equal(256UL, pool.CachedBytes);
    }

    [Fact]
    public void WrittenLengthFollowsTheCurrentLease()
    {
        using var pool = new VulkanHostBufferPool(1024, _ => { });
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.StorageBufferBit, 256);
        var firstLease = pool.Register(Allocation(13, 14, key));

        Assert.True(pool.UpdateWrittenLength(firstLease, 192));
        Assert.True(pool.Return(firstLease));
        Assert.True(pool.TryRent(key, out var secondLease));
        Assert.Equal(192UL, secondLease.WrittenLength);
        Assert.False(pool.UpdateWrittenLength(firstLease, 64));
        Assert.True(pool.UpdateWrittenLength(secondLease, 64));
    }

    [Fact]
    public void DisposeIsIdempotentAndRejectsFurtherOperations()
    {
        var destroyed = new List<VulkanHostBufferAllocation>();
        var pool = new VulkanHostBufferPool(1024, destroyed.Add);
        var key = new VulkanHostBufferPoolKey(BufferUsageFlags.StorageBufferBit, 256);
        var allocation = pool.Register(Allocation(15, 16, key));

        pool.Dispose();
        pool.Dispose();

        Assert.Equal([allocation], destroyed);
        Assert.False(pool.TryRent(key, out _));
        Assert.False(pool.Return(allocation));
        Assert.Throws<ObjectDisposedException>(() =>
            pool.Register(Allocation(17, 18, key)));
    }

    private static VulkanHostBufferAllocation Allocation(
        ulong buffer,
        ulong memory,
        VulkanHostBufferPoolKey key) =>
        new(new VkBuffer(buffer), new DeviceMemory(memory), key, 0);
}
