// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;

namespace Touche.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<SessionSurfaceMode>))]
public enum SessionSurfaceMode
{
    Embedded,
    SeparateWindow,
    Headless,
}

public sealed record GraphicsSessionSettings
{
    public string? AdapterId { get; init; }

    public int FpsLimit { get; init; }

    public SessionSurfaceMode SurfaceMode { get; init; } = SessionSurfaceMode.Embedded;
}

public sealed record AudioSessionSettings
{
    public string DeviceId { get; init; } = "default";

    public double Volume { get; init; } = 1.0;
}

public sealed record InputSessionSettings
{
    public string ProfileId { get; init; } = "dual-sense-default";
}

public sealed record FirmwareSessionSettings
{
    public required string ProfileId { get; init; }
}

public sealed record DiagnosticSessionSettings
{
    public string LogLevel { get; init; } = "info";

    public bool CaptureCrashDump { get; init; } = true;
}
