// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Memory;

/// <summary>Releases an independently allocated virtual-memory region by base address.</summary>
public interface IReleasableVirtualMemory
{
    bool TryReleaseMapping(ulong virtualAddress);
}
