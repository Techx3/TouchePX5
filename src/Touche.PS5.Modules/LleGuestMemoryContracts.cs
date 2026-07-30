// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.PS5.Modules;

/// <summary>
/// Transactional boundary implemented by a concrete emulator core. Disposing
/// an uncommitted transaction must remove every segment staged through it.
/// </summary>
public interface ILleGuestMemoryTransaction : IAsyncDisposable
{
    ValueTask StageSegmentAsync(
        ulong runtimeAddress,
        ulong memorySize,
        ulong sourceFileOffset,
        ReadOnlyMemory<byte> initialData,
        LleSegmentPermissions finalPermissions,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(CancellationToken cancellationToken = default);
}

public interface ILleGuestMemoryTransactionFactory
{
    ValueTask<ILleGuestMemoryTransaction> BeginAsync(
        string moduleVirtualPath,
        ulong runtimeImageStart,
        ulong imageSize,
        CancellationToken cancellationToken = default);
}
