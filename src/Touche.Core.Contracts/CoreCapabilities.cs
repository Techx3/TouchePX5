// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.Core.Contracts;

public sealed record CoreCapabilities
{
    public bool SupportsPause { get; init; }

    public bool SupportsSaveStates { get; init; }

    public bool SupportsEmbeddedSurface { get; init; }

    public bool SupportsSeparateWindow { get; init; }

    public bool SupportsHeadless { get; init; }

    public bool SupportsInternalResolution { get; init; }

    public bool SupportsPerGameSettings { get; init; }

    public IReadOnlyList<string> SupportedFormats { get; init; } = [];
}
