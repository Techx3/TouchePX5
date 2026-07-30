// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;

namespace Touche.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<EmulatorLogLevel>))]
public enum EmulatorLogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SessionStateChanged), "state")]
[JsonDerivedType(typeof(EmulatorLogReceived), "log")]
[JsonDerivedType(typeof(PerformanceSnapshotReceived), "performance")]
[JsonDerivedType(typeof(EmulatorErrorRaised), "error")]
[JsonDerivedType(typeof(EmulatorProcessExited), "exited")]
public abstract record EmulatorEvent(DateTimeOffset Timestamp);

public sealed record SessionStateChanged(
    DateTimeOffset Timestamp,
    EmulatorSessionState Previous,
    EmulatorSessionState Current)
    : EmulatorEvent(Timestamp);

public sealed record EmulatorLogReceived(
    DateTimeOffset Timestamp,
    EmulatorLogLevel Level,
    string Category,
    string Message)
    : EmulatorEvent(Timestamp);

public sealed record PerformanceSnapshotReceived(
    DateTimeOffset Timestamp,
    double? FramesPerSecond,
    double? FrameTimeMilliseconds,
    long? GuestMemoryBytes,
    int? GuestThreadCount)
    : EmulatorEvent(Timestamp);

public sealed record EmulatorErrorRaised(
    DateTimeOffset Timestamp,
    string ErrorCode,
    string Message,
    bool IsFatal)
    : EmulatorEvent(Timestamp);

public sealed record EmulatorProcessExited(
    DateTimeOffset Timestamp,
    int? ExitCode,
    EmulatorExitReason Reason)
    : EmulatorEvent(Timestamp);
