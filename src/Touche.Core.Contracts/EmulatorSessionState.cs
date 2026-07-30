// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json.Serialization;

namespace Touche.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<EmulatorSessionState>))]
public enum EmulatorSessionState
{
    Created,
    Validating,
    Starting,
    Running,
    Paused,
    Stopping,
    Stopped,
    Crashed,
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<EmulatorExitReason>))]
public enum EmulatorExitReason
{
    None,
    UserRequested,
    GameExited,
    StartupFailure,
    RuntimeCrash,
    HostTerminated,
    ValidationFailure,
    IncompatibleFirmware,
    Unknown,
}

public static class EmulatorSessionTransitions
{
    public static bool CanTransition(EmulatorSessionState current, EmulatorSessionState next) =>
        current switch
        {
            EmulatorSessionState.Created => next is EmulatorSessionState.Validating or
                EmulatorSessionState.Stopping or EmulatorSessionState.Failed,
            EmulatorSessionState.Validating => next is EmulatorSessionState.Starting or
                EmulatorSessionState.Stopping or EmulatorSessionState.Failed,
            EmulatorSessionState.Starting => next is EmulatorSessionState.Running or
                EmulatorSessionState.Stopping or EmulatorSessionState.Failed,
            EmulatorSessionState.Running => next is EmulatorSessionState.Paused or
                EmulatorSessionState.Stopping or EmulatorSessionState.Stopped or EmulatorSessionState.Crashed,
            EmulatorSessionState.Paused => next is EmulatorSessionState.Running or
                EmulatorSessionState.Stopping or EmulatorSessionState.Crashed,
            EmulatorSessionState.Stopping => next is EmulatorSessionState.Stopped or EmulatorSessionState.Crashed,
            _ => false,
        };

    public static bool IsTerminal(EmulatorSessionState state) =>
        state is EmulatorSessionState.Stopped or EmulatorSessionState.Crashed or EmulatorSessionState.Failed;
}
