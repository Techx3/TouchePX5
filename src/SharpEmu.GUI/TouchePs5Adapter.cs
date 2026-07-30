// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Touche.Core.Contracts;

namespace SharpEmu.GUI;

/// <summary>
/// Adapts the existing isolated PS5 process host to the versioned Touché
/// session contracts without changing the emulator runtime.
/// </summary>
internal sealed class TouchePs5Adapter : IEmulatorAdapter
{
    private readonly string _emulatorExecutablePath;
    private readonly string? _workingDirectory;
    private readonly Func<SessionDescriptor, IReadOnlyList<string>> _argumentBuilder;
    private readonly Func<IEmulatorProcess> _processFactory;

    public TouchePs5Adapter(
        string emulatorExecutablePath,
        Func<SessionDescriptor, IReadOnlyList<string>> argumentBuilder,
        string? workingDirectory = null)
        : this(emulatorExecutablePath, argumentBuilder, workingDirectory, static () => new EmulatorProcess())
    {
    }

    internal TouchePs5Adapter(
        string emulatorExecutablePath,
        Func<SessionDescriptor, IReadOnlyList<string>> argumentBuilder,
        string? workingDirectory,
        Func<IEmulatorProcess> processFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emulatorExecutablePath);
        ArgumentNullException.ThrowIfNull(argumentBuilder);
        ArgumentNullException.ThrowIfNull(processFactory);
        _emulatorExecutablePath = Path.GetFullPath(emulatorExecutablePath);
        _workingDirectory = workingDirectory;
        _argumentBuilder = argumentBuilder;
        _processFactory = processFactory;
    }

    public string CoreId => ToucheCoreIds.PlayStation5;

    public Task<CoreCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CoreCapabilities
        {
            SupportsPause = false,
            SupportsSaveStates = false,
            SupportsEmbeddedSurface = true,
            SupportsSeparateWindow = true,
            SupportsHeadless = false,
            SupportsInternalResolution = false,
            SupportsFirmware = true,
            SupportsPerGameSettings = true,
            SupportedFormats = ["eboot.bin", ".elf", ".self"],
        });
    }

    public Task<ValidationResult> ValidateGameAsync(
        GameLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            return Task.FromResult(ValidationResult.Failure(
                "game.path.empty",
                "The game executable path is empty."));
        }

        if (!File.Exists(request.ExecutablePath))
        {
            return Task.FromResult(ValidationResult.Failure(
                "game.path.missing",
                "The game executable was not found."));
        }

        return Task.FromResult(ValidationResult.Success);
    }

    public async Task<IEmulatorSession> LaunchAsync(
        SessionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptor(descriptor);

        var process = _processFactory();
        var session = new TouchePs5Session(descriptor.SessionId, process);
        try
        {
            session.TransitionTo(EmulatorSessionState.Validating);
            var validation = await ValidateGameAsync(descriptor.Game, cancellationToken).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                session.FailValidation(validation);
                throw new InvalidDataException(validation.Issues.First(issue => issue.IsFatal).Message);
            }

            if (!File.Exists(_emulatorExecutablePath))
            {
                session.FailStartup("host.executable.missing", "The Touché PX5 executable was not found.");
                throw new FileNotFoundException("The Touché PX5 executable was not found.", _emulatorExecutablePath);
            }

            session.TransitionTo(EmulatorSessionState.Starting);
            var arguments = _argumentBuilder(descriptor);
            process.Start(_emulatorExecutablePath, arguments, _workingDirectory);
            session.TransitionTo(EmulatorSessionState.Running);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void ValidateDescriptor(SessionDescriptor descriptor)
    {
        if (descriptor.SchemaVersion != SessionDescriptor.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported session descriptor schema {descriptor.SchemaVersion}.");
        }

        if (!string.Equals(descriptor.CoreId, CoreId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Session core '{descriptor.CoreId}' cannot be launched by {CoreId}.");
        }
    }
}

internal sealed class TouchePs5Session : IEmulatorSession
{
    private const int LogCapacity = 2048;
    private readonly object _sync = new();
    private readonly IEmulatorProcess _process;
    private readonly Channel<EmulatorEvent> _controlEvents = Channel.CreateUnbounded<EmulatorEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly Channel<EmulatorEvent> _logEvents = Channel.CreateBounded<EmulatorEvent>(
        new BoundedChannelOptions(LogCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
    private EmulatorSessionState _state = EmulatorSessionState.Created;
    private EmulatorExitReason _exitReason;
    private bool _disposed;

    public TouchePs5Session(Guid sessionId, IEmulatorProcess process)
    {
        SessionId = sessionId;
        _process = process;
        _process.OutputReceived += OnOutputReceived;
        _process.Exited += OnExited;
    }

    public Guid SessionId { get; }

    public EmulatorSessionState State
    {
        get { lock (_sync) { return _state; } }
    }

    public EmulatorExitReason ExitReason
    {
        get { lock (_sync) { return _exitReason; } }
    }

    public int? ProcessId => _process.ProcessId;

    public Task PauseAsync(CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("The PS5 core does not currently support pause."));

    public Task ResumeAsync(CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException("The PS5 core does not currently support resume."));

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (EmulatorSessionTransitions.IsTerminal(_state) || _state == EmulatorSessionState.Stopping)
            {
                return Task.CompletedTask;
            }
        }

        TransitionTo(EmulatorSessionState.Stopping);
        _process.Stop();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<EmulatorEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            while (_controlEvents.Reader.TryRead(out var controlEvent))
            {
                yield return controlEvent;
            }

            if (_logEvents.Reader.TryRead(out var logEvent))
            {
                yield return logEvent;
                continue;
            }

            var controlWait = _controlEvents.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var logWait = _logEvents.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var completedWait = await Task.WhenAny(controlWait, logWait).ConfigureAwait(false);
            if (await completedWait.ConfigureAwait(false))
            {
                continue;
            }

            var remainingWait = ReferenceEquals(completedWait, controlWait) ? logWait : controlWait;
            if (!await remainingWait.ConfigureAwait(false))
            {
                yield break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
        }

        _process.OutputReceived -= OnOutputReceived;
        _process.Exited -= OnExited;
        _process.Dispose();
        CompleteEvents();
        return ValueTask.CompletedTask;
    }

    internal void TransitionTo(EmulatorSessionState next)
    {
        EmulatorSessionState previous;
        lock (_sync)
        {
            previous = _state;
            if (!EmulatorSessionTransitions.CanTransition(previous, next))
            {
                throw new InvalidOperationException($"Invalid emulator session transition: {previous} -> {next}.");
            }

            _state = next;
        }

        _controlEvents.Writer.TryWrite(new SessionStateChanged(DateTimeOffset.UtcNow, previous, next));
    }

    internal void FailValidation(ValidationResult validation)
    {
        SetFailure(EmulatorExitReason.ValidationFailure);
        foreach (var issue in validation.Issues)
        {
            _controlEvents.Writer.TryWrite(new EmulatorErrorRaised(
                DateTimeOffset.UtcNow,
                issue.Code,
                issue.Message,
                issue.IsFatal));
        }
    }

    internal void FailStartup(string code, string message)
    {
        SetFailure(EmulatorExitReason.StartupFailure);
        _controlEvents.Writer.TryWrite(new EmulatorErrorRaised(DateTimeOffset.UtcNow, code, message, IsFatal: true));
    }

    private void SetFailure(EmulatorExitReason reason)
    {
        lock (_sync)
        {
            _exitReason = reason;
        }

        TransitionTo(EmulatorSessionState.Failed);
    }

    private void OnOutputReceived(string line, bool isError)
    {
        _logEvents.Writer.TryWrite(new EmulatorLogReceived(
            DateTimeOffset.UtcNow,
            isError ? EmulatorLogLevel.Error : EmulatorLogLevel.Information,
            isError ? "stderr" : "stdout",
            line));
    }

    private void OnExited(int exitCode)
    {
        EmulatorSessionState finalState;
        EmulatorExitReason reason;
        lock (_sync)
        {
            if (EmulatorSessionTransitions.IsTerminal(_state))
            {
                return;
            }

            reason = exitCode switch
            {
                EmulatorProcess.HostStopExitCode => EmulatorExitReason.UserRequested,
                0 => EmulatorExitReason.GameExited,
                _ => EmulatorExitReason.RuntimeCrash,
            };
            finalState = reason == EmulatorExitReason.RuntimeCrash
                ? EmulatorSessionState.Crashed
                : EmulatorSessionState.Stopped;
            _exitReason = reason;
        }

        TransitionTo(finalState);
        _controlEvents.Writer.TryWrite(new EmulatorProcessExited(DateTimeOffset.UtcNow, exitCode, reason));
        CompleteEvents();
    }

    private void CompleteEvents()
    {
        _controlEvents.Writer.TryComplete();
        _logEvents.Writer.TryComplete();
    }
}
