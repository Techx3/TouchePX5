// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestWorkPolicyTests
{
    [Fact]
    public void WriteBackModeDoesNotDependOnDebugName()
    {
        var acquireNamedFullWriteBack = new VulkanOrderedGuestAction(
            static () => { },
            "acquire_mem_flush compatibility label",
            WriteBackMode: GuestGpuWriteBackMode.AllDirty);
        var ordinaryNamedNoWriteBack = new VulkanOrderedGuestAction(
            static () => { },
            "ordinary action",
            WriteBackMode: GuestGpuWriteBackMode.None);

        Assert.True(acquireNamedFullWriteBack.RequiresGuestBufferWriteBack);
        Assert.False(ordinaryNamedNoWriteBack.RequiresGuestBufferWriteBack);
    }

    [Fact]
    public void SelectiveWriteBackRequiresAnExplicitRange()
    {
        var ranged = new VulkanOrderedGuestAction(
            static () => { },
            "readback",
            WriteBackAddress: 0x1000,
            WriteBackLength: 0x80,
            WriteBackMode: GuestGpuWriteBackMode.Selective);
        var empty = ranged with { WriteBackLength = 0 };

        Assert.True(ranged.RequiresGuestBufferWriteBack);
        Assert.True(ranged.HasSelectiveGuestBufferWriteBack);
        Assert.False(empty.HasSelectiveGuestBufferWriteBack);
    }

    [Fact]
    public void EveryGuestWorkDependencyMustBeComplete()
    {
        long[] required = [3, 7];
        HashSet<long> completedOutOfOrder = [7];

        Assert.False(
            VulkanVideoPresenter.AreGuestWorkDependenciesSatisfied(
                required,
                completedThrough: 2,
                completedOutOfOrder));

        completedOutOfOrder.Add(3);
        Assert.True(
            VulkanVideoPresenter.AreGuestWorkDependenciesSatisfied(
                required,
                completedThrough: 2,
                completedOutOfOrder));
    }

    [Fact]
    public void ContiguousCompletionSatisfiesEveryEarlierDependency()
    {
        Assert.True(
            VulkanVideoPresenter.AreGuestWorkDependenciesSatisfied(
                [1, 4, 9],
                completedThrough: 9,
                completedOutOfOrder: new HashSet<long>()));
    }

    [Fact]
    public void RejectedWorkCannotPublishGuestState()
    {
        var published = false;

        var accepted = VulkanVideoPresenter.TryCommitGuestWorkPublication(
            workSequence: 0,
            state: true,
            publish: value => published = value);

        Assert.False(accepted);
        Assert.False(published);
    }

    [Fact]
    public void AcceptedWorkPublishesGuestStateExactlyOnce()
    {
        var publicationCount = 0;

        var accepted = VulkanVideoPresenter.TryCommitGuestWorkPublication(
            workSequence: 12,
            state: 1,
            publish: value => publicationCount += value);

        Assert.True(accepted);
        Assert.Equal(1, publicationCount);
    }

    [Fact]
    public void OrderedVisibilityRemainsNonBlockingWithoutBacklog()
    {
        var waitNs = VulkanVideoPresenter.ResolveOrderedVisibilityWaitNs(
            isMacOS: false,
            hasExplicitOverride: false,
            explicitWaitNs: 0,
            pendingPayloadWorkCount: 127,
            pendingTotalWorkCount: 127,
            maximumPendingPayloadWorkCount: 512);

        Assert.Equal(0UL, waitNs);
    }

    [Theory]
    [InlineData(256, 256)]
    [InlineData(128, 256)]
    public void OrderedVisibilityUsesBoundedWaitUnderSustainedBacklog(
        int pendingPayloadWorkCount,
        int pendingTotalWorkCount)
    {
        var waitNs = VulkanVideoPresenter.ResolveOrderedVisibilityWaitNs(
            isMacOS: false,
            hasExplicitOverride: false,
            explicitWaitNs: 0,
            pendingPayloadWorkCount,
            pendingTotalWorkCount,
            maximumPendingPayloadWorkCount: 512);

        Assert.Equal(2_000_000UL, waitNs);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(7_000_000UL)]
    public void OrderedVisibilityHonorsExplicitOverride(ulong explicitWaitNs)
    {
        var waitNs = VulkanVideoPresenter.ResolveOrderedVisibilityWaitNs(
            isMacOS: false,
            hasExplicitOverride: true,
            explicitWaitNs,
            pendingPayloadWorkCount: 512,
            pendingTotalWorkCount: 1024,
            maximumPendingPayloadWorkCount: 512);

        Assert.Equal(explicitWaitNs, waitNs);
    }

    [Fact]
    public void OrderedVisibilityNeverBlocksMacOSMainThread()
    {
        var waitNs = VulkanVideoPresenter.ResolveOrderedVisibilityWaitNs(
            isMacOS: true,
            hasExplicitOverride: true,
            explicitWaitNs: 7_000_000,
            pendingPayloadWorkCount: 512,
            pendingTotalWorkCount: 1024,
            maximumPendingPayloadWorkCount: 512);

        Assert.Equal(0UL, waitNs);
    }
}
