// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

namespace Touche.Core.Contracts;

public sealed record ValidationIssue(string Code, string Message, bool IsFatal = true);

public sealed record ValidationResult
{
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];

    public bool IsValid => Issues.All(issue => !issue.IsFatal);

    public static ValidationResult Success { get; } = new();

    public static ValidationResult Failure(string code, string message) => new()
    {
        Issues = [new ValidationIssue(code, message)],
    };
}

public interface IEmulatorAdapter
{
    string CoreId { get; }

    Task<CoreCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);

    Task<ValidationResult> ValidateGameAsync(
        GameLaunchRequest request,
        CancellationToken cancellationToken);

    Task<IEmulatorSession> LaunchAsync(
        SessionDescriptor descriptor,
        CancellationToken cancellationToken);
}

public interface IEmulatorSession : IAsyncDisposable
{
    Guid SessionId { get; }

    EmulatorSessionState State { get; }

    EmulatorExitReason ExitReason { get; }

    int? ProcessId { get; }

    Task PauseAsync(CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<EmulatorEvent> ReadEventsAsync(CancellationToken cancellationToken);
}
