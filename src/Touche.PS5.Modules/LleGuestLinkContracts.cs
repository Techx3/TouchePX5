// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.PS5.Modules;

/// <summary>
/// Transactional boundary for import thunks and relocation writes. Disposing
/// an uncommitted transaction must restore all modified guest state.
/// </summary>
public interface ILleGuestLinkTransaction : IAsyncDisposable
{
    ValueTask<ulong> StageHleThunkAsync(
        string dispatchKey,
        bool controlledStub,
        CancellationToken cancellationToken = default);

    ValueTask StageWriteAsync(
        ulong runtimeAddress,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);

    ValueTask CommitAsync(CancellationToken cancellationToken = default);
}

public interface ILleGuestLinkTransactionFactory
{
    ValueTask<ILleGuestLinkTransaction> BeginAsync(
        string moduleVirtualPath,
        ulong runtimeImageStart,
        ulong imageSize,
        CancellationToken cancellationToken = default);
}
