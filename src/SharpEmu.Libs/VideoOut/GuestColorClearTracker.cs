// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Identifies the exact guest color subresource affected by a metadata clear.
/// Format and tiling are part of the identity because one guest address can
/// have several live Vulkan image variants.
/// </summary>
internal readonly record struct GuestTargetSubresourceKey(
    ulong Address,
    uint Width,
    uint Height,
    uint MipLevels,
    uint MipLevel,
    uint BaseArrayLayer,
    uint LayerCount,
    uint Format,
    uint NumberType,
    uint CompSwap,
    uint TileMode);

/// <summary>
/// Tracks DCC metadata clears until the next matching guest draw is enqueued.
/// The presenter gate must be held by callers; once taken, keys travel with the
/// queued draw and no longer depend on mutable global state.
/// </summary>
internal sealed class GuestColorClearTracker
{
    private readonly HashSet<GuestTargetSubresourceKey> _pending = [];

    internal int Count => _pending.Count;

    internal void Request(GuestRenderTarget target)
    {
        if (target.Address != 0)
        {
            _pending.Add(GetKey(target));
        }
    }

    internal GuestTargetSubresourceKey[] TakeFor(
        IReadOnlyList<GuestRenderTarget> targets)
    {
        if (_pending.Count == 0 || targets.Count == 0)
        {
            return [];
        }

        List<GuestTargetSubresourceKey>? taken = null;
        foreach (var target in targets)
        {
            if (target.Address == 0)
            {
                continue;
            }

            var key = GetKey(target);
            if (_pending.Remove(key))
            {
                (taken ??= []).Add(key);
            }
        }

        return taken?.ToArray() ?? [];
    }

    internal void Reset() => _pending.Clear();

    internal static GuestTargetSubresourceKey GetKey(GuestRenderTarget target) =>
        new(
            target.Address,
            target.Width,
            target.Height,
            target.MipLevels,
            target.MipLevel,
            target.BaseArrayLayer,
            target.LayerCount,
            target.Format,
            target.NumberType,
            target.CompSwap,
            target.TileMode);
}
