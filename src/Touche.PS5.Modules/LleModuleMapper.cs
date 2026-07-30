// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using Touche.Firmware;

namespace Touche.PS5.Modules;

/// <summary>
/// Stages a verified LLE plan through a core-provided transaction. No segment
/// becomes durable unless every segment was staged and the transaction commits.
/// </summary>
public sealed class LleModuleMapper
{
    private const ulong PageSize = 4096;
    private const ulong MaximumImageSpan = 16UL * 1024 * 1024 * 1024;
    private const ulong MaximumFileBytes = 256UL * 1024 * 1024;

    public async Task<LleMappedModule> MapAsync(
        LleModuleLoadPlan plan,
        ulong runtimeImageStart,
        IFirmwareVirtualFileSystem fileSystem,
        ILleGuestMemoryTransactionFactory transactionFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(transactionFactory);
        var validatedSegments = ValidatePlan(plan, runtimeImageStart, fileSystem.ProfileId);

        await using var handle = await fileSystem.OpenReadAsync(
            plan.ModuleVirtualPath,
            cancellationToken).ConfigureAwait(false);
        if (handle is null)
        {
            throw new FileNotFoundException("The planned firmware module is not mounted.", plan.ModuleVirtualPath);
        }
        if (!string.Equals(handle.Artifact.Sha256, plan.ModuleHash, StringComparison.Ordinal) ||
            !string.Equals(handle.Artifact.VirtualPath, plan.ModuleVirtualPath, StringComparison.Ordinal) ||
            handle.Artifact.Kind != FirmwareArtifactKind.ElfOrSelf ||
            handle.Artifact.Size < 0 ||
            (ulong)handle.Artifact.Size > MaximumFileBytes)
        {
            throw new InvalidDataException("The mounted artifact does not match the LLE load plan.");
        }

        await using var transaction = await transactionFactory.BeginAsync(
            plan.ModuleVirtualPath,
            runtimeImageStart,
            plan.ImageSize,
            cancellationToken).ConfigureAwait(false);
        if (transaction is null)
        {
            throw new InvalidOperationException("The guest memory core returned no mapping transaction.");
        }

        var mappedSegments = new List<LleMappedSegment>(validatedSegments.Count);
        foreach (var item in validatedSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = item.Segment;
            byte[] fileData;
            if (segment.FileSize == 0)
            {
                fileData = [];
            }
            else
            {
                EnsureFileRange(segment.FileOffset, segment.FileSize, handle.Artifact.Size);
                fileData = new byte[checked((int)segment.FileSize)];
                handle.Content.Position = checked((long)segment.FileOffset);
                await handle.Content.ReadExactlyAsync(fileData, cancellationToken).ConfigureAwait(false);
            }

            await transaction.StageSegmentAsync(
                item.RuntimeAddress,
                segment.MemorySize,
                segment.FileOffset,
                fileData,
                segment.Permissions,
                cancellationToken).ConfigureAwait(false);
            mappedSegments.Add(new LleMappedSegment(
                segment.ProgramHeaderIndex,
                item.RuntimeAddress,
                segment.MemorySize,
                segment.Permissions));
        }

        var runtimeEntryPoint = plan.EntryPoint == 0
            ? 0
            : checked(runtimeImageStart + (plan.EntryPoint - plan.ImageVirtualStart));
        var mappedModule = new LleMappedModule
        {
            FirmwareProfileId = plan.FirmwareProfileId,
            ModuleVirtualPath = plan.ModuleVirtualPath,
            ModuleHash = plan.ModuleHash,
            RuntimeImageStart = runtimeImageStart,
            RuntimeEntryPoint = runtimeEntryPoint,
            ImageSize = plan.ImageSize,
            Segments = mappedSegments.ToArray(),
        };
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return mappedModule;
    }

    private static IReadOnlyList<ValidatedSegment> ValidatePlan(
        LleModuleLoadPlan plan,
        ulong runtimeImageStart,
        string mountedProfileId)
    {
        if (string.IsNullOrWhiteSpace(plan.FirmwareProfileId) ||
            !string.Equals(plan.FirmwareProfileId, mountedProfileId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(plan.ModuleVirtualPath) ||
            !plan.ModuleVirtualPath.StartsWith("/", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(plan.ModuleHash) ||
            plan.ModuleHash.Length != 64 ||
            !plan.ModuleHash.All(char.IsAsciiHexDigit) ||
            plan.Segments is null ||
            plan.Segments.Count == 0 ||
            plan.ImageSize is 0 or > MaximumImageSpan ||
            runtimeImageStart % PageSize != plan.ImageVirtualStart % PageSize)
        {
            throw new InvalidDataException("The LLE load plan is invalid or belongs to another firmware profile.");
        }
        if (plan.ImageSize > ulong.MaxValue - runtimeImageStart)
        {
            throw new InvalidDataException("The runtime LLE image exceeds the guest address space.");
        }

        var result = new List<ValidatedSegment>(plan.Segments.Count);
        var previousIndex = -1;
        var totalFileBytes = 0UL;
        foreach (var segment in plan.Segments)
        {
            ArgumentNullException.ThrowIfNull(segment);
            if (segment.ProgramHeaderIndex <= previousIndex ||
                segment.MemorySize == 0 ||
                segment.FileSize > segment.MemorySize ||
                segment.VirtualAddress < plan.ImageVirtualStart ||
                segment.MemorySize > ulong.MaxValue - segment.VirtualAddress ||
                segment.FileSize > MaximumFileBytes - totalFileBytes ||
                (segment.Permissions & ~(
                    LleSegmentPermissions.Read |
                    LleSegmentPermissions.Write |
                    LleSegmentPermissions.Execute)) != 0)
            {
                throw new InvalidDataException("The LLE load plan contains an invalid segment.");
            }
            previousIndex = segment.ProgramHeaderIndex;
            totalFileBytes += segment.FileSize;
            var relativeAddress = segment.VirtualAddress - plan.ImageVirtualStart;
            if (relativeAddress >= plan.ImageSize || segment.MemorySize > plan.ImageSize - relativeAddress)
            {
                throw new InvalidDataException("An LLE segment is outside the planned image span.");
            }
            var runtimeAddress = runtimeImageStart + relativeAddress;
            result.Add(new ValidatedSegment(segment, runtimeAddress));
        }

        var ordered = result.OrderBy(item => item.RuntimeAddress).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            var previous = ordered[index - 1];
            if (ordered[index].RuntimeAddress < previous.RuntimeAddress + previous.Segment.MemorySize)
            {
                throw new InvalidDataException("The LLE load plan contains overlapping segments.");
            }
        }
        if (plan.EntryPoint != 0 &&
            (plan.EntryPoint < plan.ImageVirtualStart ||
             plan.EntryPoint - plan.ImageVirtualStart >= plan.ImageSize))
        {
            throw new InvalidDataException("The LLE entry point is outside the planned image span.");
        }
        return result;
    }

    private static void EnsureFileRange(ulong offset, ulong size, long fileSize)
    {
        if (fileSize < 0 || offset > (ulong)fileSize || size > (ulong)fileSize - offset)
        {
            throw new InvalidDataException("An LLE segment exceeds the verified firmware object.");
        }
    }

    private sealed record ValidatedSegment(LleLoadSegment Segment, ulong RuntimeAddress);
}
