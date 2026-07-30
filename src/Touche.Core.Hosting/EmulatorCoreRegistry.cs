// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using Touche.Core.Contracts;

namespace Touche.Core.Hosting;

/// <summary>
/// Process-local registry of emulator adapters keyed by their stable core IDs.
/// </summary>
public sealed class EmulatorCoreRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IEmulatorAdapter> _adapters = new(StringComparer.Ordinal);

    public IReadOnlyList<string> CoreIds
    {
        get
        {
            lock (_sync)
            {
                return _adapters.Keys.Order(StringComparer.Ordinal).ToArray();
            }
        }
    }

    public void Register(IEmulatorAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (string.IsNullOrWhiteSpace(adapter.CoreId))
        {
            throw new ArgumentException("An emulator adapter must expose a non-empty core ID.", nameof(adapter));
        }

        lock (_sync)
        {
            if (!_adapters.TryAdd(adapter.CoreId, adapter))
            {
                throw new InvalidOperationException($"Core '{adapter.CoreId}' is already registered.");
            }
        }
    }

    public void RegisterOrReplace(IEmulatorAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (string.IsNullOrWhiteSpace(adapter.CoreId))
        {
            throw new ArgumentException("An emulator adapter must expose a non-empty core ID.", nameof(adapter));
        }

        lock (_sync)
        {
            _adapters[adapter.CoreId] = adapter;
        }
    }

    public bool TryGet(string coreId, out IEmulatorAdapter? adapter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coreId);
        lock (_sync)
        {
            return _adapters.TryGetValue(coreId, out adapter);
        }
    }

    public IEmulatorAdapter GetRequired(string coreId)
    {
        if (!TryGet(coreId, out var adapter) || adapter is null)
        {
            throw new KeyNotFoundException($"Core '{coreId}' is not registered.");
        }

        return adapter;
    }
}
