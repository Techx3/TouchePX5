// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.Core.Contracts;

/// <summary>
/// Identifies the local game selected for a session. The executable path is
/// transient session input and must be removed from persistent reports.
/// </summary>
public sealed record GameLaunchRequest
{
    public required string ExecutablePath { get; init; }

    public string? TitleId { get; init; }

    public string? DisplayName { get; init; }

    public string? ContentHash { get; init; }
}
