// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using Touche.PS5.Modules;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Adapts the core virtual memory to the transactional LLE linker contract.
/// Committed thunk pages remain owned by this factory until their module is released.
/// </summary>
public sealed class CoreLleGuestLinkTransactionFactory : ILleGuestLinkTransactionFactory, IDisposable
{
    private const ulong PageSize = 0x1000;
    private const ulong StubSlotSize = 0x10;
    private const ulong StubArenaStart = 0x0000_6f00_0000_0000;
    private const byte StubTrapOpcode = 0xcc;
    private const byte StubReturnOpcode = 0xc3;

    private readonly IVirtualMemory _memory;
    private readonly IGuestAddressSpace _addressSpace;
    private readonly IReleasableVirtualMemory _releasableMemory;
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private readonly object _ownershipGate = new();
    private readonly Dictionary<ModuleKey, IReadOnlyList<ulong>> _ownedThunkPages = [];
    private bool _disposed;

    public CoreLleGuestLinkTransactionFactory(IVirtualMemory memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _addressSpace = memory as IGuestAddressSpace ??
            throw new ArgumentException("Virtual memory cannot allocate executable guest regions.", nameof(memory));
        _releasableMemory = memory as IReleasableVirtualMemory ??
            throw new ArgumentException("Virtual memory cannot release executable guest regions.", nameof(memory));
    }

    public async ValueTask<ILleGuestLinkTransaction> BeginAsync(
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
                if (_ownedThunkPages.ContainsKey(key))
                {
                    throw new InvalidOperationException("The LLE module already owns a committed link transaction.");
                }
            }
            return new Transaction(this, key);
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
        IReadOnlyList<ulong>? pages;
        lock (_ownershipGate)
        {
            if (!_ownedThunkPages.TryGetValue(key, out pages))
            {
                return false;
            }
        }

        var failed = new List<ulong>();
        foreach (var page in pages)
        {
            if (!_releasableMemory.TryReleaseMapping(page))
            {
                failed.Add(page);
            }
        }
        lock (_ownershipGate)
        {
            if (failed.Count == 0)
            {
                _ownedThunkPages.Remove(key);
            }
            else
            {
                _ownedThunkPages[key] = failed;
            }
        }
        return failed.Count == 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (!_transactionGate.Wait(0))
        {
            throw new InvalidOperationException("Cannot dispose the LLE link factory while a transaction is active.");
        }
        _disposed = true;
        IReadOnlyList<ulong> pages;
        lock (_ownershipGate)
        {
            pages = _ownedThunkPages.Values.SelectMany(value => value).Distinct().ToArray();
            _ownedThunkPages.Clear();
        }
        foreach (var page in pages)
        {
            _ = _releasableMemory.TryReleaseMapping(page);
        }
        _transactionGate.Dispose();
    }

    private ulong AllocateThunkPage()
    {
        if (!_addressSpace.TryAllocateAtOrAbove(
                StubArenaStart,
                PageSize,
                executable: true,
                alignment: PageSize,
                out var address) ||
            address == 0)
        {
            throw new OutOfMemoryException("Unable to allocate an executable LLE import-thunk page.");
        }
        if (address % PageSize != 0)
        {
            _ = _releasableMemory.TryReleaseMapping(address);
            throw new InvalidDataException("The core returned an unaligned import-thunk page.");
        }
        return address;
    }

    private void RegisterCommitted(ModuleKey key, IReadOnlyList<ulong> pages)
    {
        lock (_ownershipGate)
        {
            if (!_ownedThunkPages.TryAdd(key, pages.ToArray()))
            {
                throw new InvalidOperationException("The LLE module was linked concurrently.");
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
            imageSize > ulong.MaxValue - runtimeImageStart)
        {
            throw new ArgumentException("Invalid LLE module transaction identity.", nameof(path));
        }
    }

    private static uint NidToUInt32(string nid)
    {
        uint hash = 0;
        for (var index = 0; index < Math.Min(nid.Length, 8); index++)
        {
            hash = (hash << 4) | (byte)nid[index];
        }
        return hash ^ (uint)nid.Length;
    }

    private sealed class Transaction(CoreLleGuestLinkTransactionFactory owner, ModuleKey key)
        : ILleGuestLinkTransaction
    {
        private readonly List<ulong> _thunkPages = [];
        private readonly Dictionary<ThunkKey, ulong> _thunks = [];
        private readonly List<StagedWrite> _writes = [];
        private readonly List<StagedWrite> _appliedWrites = [];
        private bool _committed;
        private bool _disposed;
        private ulong _currentThunkPage;
        private ulong _nextThunkOffset;

        public ValueTask<ulong> StageHleThunkAsync(
            string dispatchKey,
            bool controlledStub,
            CancellationToken cancellationToken = default)
        {
            ThrowIfClosed();
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(dispatchKey);
            if (dispatchKey.Any(character => char.IsControl(character) || !char.IsAscii(character)))
            {
                throw new InvalidDataException("HLE dispatch keys must contain printable ASCII characters.");
            }

            var thunkKey = new ThunkKey(dispatchKey, controlledStub);
            if (_thunks.TryGetValue(thunkKey, out var existing))
            {
                return ValueTask.FromResult(existing);
            }
            if (_currentThunkPage == 0 || _nextThunkOffset > PageSize - StubSlotSize)
            {
                _currentThunkPage = owner.AllocateThunkPage();
                _thunkPages.Add(_currentThunkPage);
                _nextThunkOffset = 0;
            }

            var address = checked(_currentThunkPage + _nextThunkOffset);
            Span<byte> stub = stackalloc byte[(int)StubSlotSize];
            stub[0] = StubTrapOpcode;
            stub[1] = StubReturnOpcode;
            stub[2] = controlledStub ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt32LittleEndian(stub[8..], NidToUInt32(dispatchKey));
            if (!owner._memory.TryWrite(address, stub))
            {
                throw new IOException("Unable to stage an HLE import thunk in guest memory.");
            }

            _nextThunkOffset += StubSlotSize;
            _thunks.Add(thunkKey, address);
            return ValueTask.FromResult(address);
        }

        public ValueTask StageWriteAsync(
            ulong runtimeAddress,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            ThrowIfClosed();
            cancellationToken.ThrowIfCancellationRequested();
            if (runtimeAddress == 0 || data.IsEmpty || data.Length > sizeof(ulong))
            {
                throw new ArgumentOutOfRangeException(nameof(data));
            }
            var end = checked(runtimeAddress + (ulong)data.Length);
            if (_writes.Any(write =>
                    runtimeAddress < write.RuntimeAddress + (ulong)write.NewData.Length &&
                    end > write.RuntimeAddress))
            {
                throw new InvalidDataException("Transactional guest-memory writes cannot overlap.");
            }

            var original = new byte[data.Length];
            if (!owner._memory.TryRead(runtimeAddress, original))
            {
                throw new IOException($"Unable to read relocation target 0x{runtimeAddress:X16}.");
            }
            _writes.Add(new StagedWrite(runtimeAddress, original, data.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfClosed();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var write in _writes.OrderBy(write => write.RuntimeAddress))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!owner._memory.TryWrite(write.RuntimeAddress, write.NewData))
                {
                    throw new IOException($"Unable to write relocation target 0x{write.RuntimeAddress:X16}.");
                }
                _appliedWrites.Add(write);
            }
            foreach (var page in _thunkPages)
            {
                if (!owner._addressSpace.TryProtect(
                        page,
                        PageSize,
                        GuestPageProtection.Read | GuestPageProtection.Execute))
                {
                    throw new IOException($"Unable to protect import-thunk page 0x{page:X16}.");
                }
            }

            owner.RegisterCommitted(key, _thunkPages);
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
            Exception? rollbackFailure = null;
            if (!_committed)
            {
                for (var index = _appliedWrites.Count - 1; index >= 0; index--)
                {
                    var write = _appliedWrites[index];
                    if (!owner._memory.TryWrite(write.RuntimeAddress, write.OriginalData))
                    {
                        rollbackFailure ??= new IOException(
                            $"Unable to restore relocation target 0x{write.RuntimeAddress:X16}.");
                    }
                }
                foreach (var page in _thunkPages)
                {
                    if (!owner._releasableMemory.TryReleaseMapping(page))
                    {
                        rollbackFailure ??= new IOException(
                            $"Unable to release import-thunk page 0x{page:X16}.");
                    }
                }
            }
            owner._transactionGate.Release();
            return rollbackFailure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(rollbackFailure);
        }

        private void ThrowIfClosed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_committed)
            {
                throw new InvalidOperationException("The LLE link transaction is already committed.");
            }
        }
    }

    private readonly record struct ModuleKey(string ModuleVirtualPath, ulong RuntimeImageStart);

    private readonly record struct ThunkKey(string DispatchKey, bool ControlledStub);

    private sealed record StagedWrite(ulong RuntimeAddress, byte[] OriginalData, byte[] NewData);
}
