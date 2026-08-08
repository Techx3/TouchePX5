// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.Core.Contracts;

/// <summary>
/// Versioned, local and temporary description of one emulator session.
/// Persistent reports must use a sanitized model without host paths.
/// </summary>
public sealed record SessionDescriptor
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required Guid SessionId { get; init; }

    public required string CoreId { get; init; }

    public required GameLaunchRequest Game { get; init; }

    public GraphicsSessionSettings Graphics { get; init; } = new();

    public AudioSessionSettings Audio { get; init; } = new();

    public InputSessionSettings Input { get; init; } = new();

    public DiagnosticSessionSettings Diagnostics { get; init; } = new();
}
