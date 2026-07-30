// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using Touche.Core.Contracts;
using Xunit;

namespace Touche.Core.Contracts.Tests;

public sealed class SessionLifecycleTests
{
    [Theory]
    [InlineData(EmulatorSessionState.Created, EmulatorSessionState.Validating)]
    [InlineData(EmulatorSessionState.Validating, EmulatorSessionState.Starting)]
    [InlineData(EmulatorSessionState.Starting, EmulatorSessionState.Running)]
    [InlineData(EmulatorSessionState.Running, EmulatorSessionState.Paused)]
    [InlineData(EmulatorSessionState.Paused, EmulatorSessionState.Running)]
    [InlineData(EmulatorSessionState.Running, EmulatorSessionState.Stopping)]
    [InlineData(EmulatorSessionState.Stopping, EmulatorSessionState.Stopped)]
    [InlineData(EmulatorSessionState.Validating, EmulatorSessionState.Stopping)]
    [InlineData(EmulatorSessionState.Starting, EmulatorSessionState.Stopping)]
    public void ExpectedTransitionsAreAccepted(EmulatorSessionState current, EmulatorSessionState next)
    {
        Assert.True(EmulatorSessionTransitions.CanTransition(current, next));
    }

    [Theory]
    [InlineData(EmulatorSessionState.Stopped)]
    [InlineData(EmulatorSessionState.Crashed)]
    [InlineData(EmulatorSessionState.Failed)]
    public void TerminalStatesCannotTransition(EmulatorSessionState state)
    {
        Assert.True(EmulatorSessionTransitions.IsTerminal(state));
        foreach (var next in Enum.GetValues<EmulatorSessionState>())
        {
            Assert.False(EmulatorSessionTransitions.CanTransition(state, next));
        }
    }

    [Fact]
    public void CommonEventsRoundTripPolymorphically()
    {
        EmulatorEvent original = new EmulatorErrorRaised(
            DateTimeOffset.Parse("2026-07-30T14:00:00Z"),
            "runtime.crash",
            "Guest process exited unexpectedly.",
            IsFatal: true);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<EmulatorEvent>(json);

        var error = Assert.IsType<EmulatorErrorRaised>(restored);
        Assert.Equal(original, error);
        Assert.Contains("\"$type\":\"error\"", json);
    }

    [Fact]
    public void ValidationWarningsDoNotRejectAGame()
    {
        var result = new ValidationResult
        {
            Issues = [new ValidationIssue("game.metadata.missing", "Metadata was not found.", IsFatal: false)],
        };

        Assert.True(result.IsValid);
        Assert.False(ValidationResult.Failure("game.path.invalid", "The executable is missing.").IsValid);
    }
}
