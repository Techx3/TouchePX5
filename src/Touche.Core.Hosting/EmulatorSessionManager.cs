// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using Touche.Core.Contracts;

namespace Touche.Core.Hosting;

/// <summary>
/// Owns the active emulator session and forwards its typed event stream.
/// It intentionally supports one active session per manager.
/// </summary>
public sealed class EmulatorSessionManager : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private IEmulatorSession? _activeSession;
    private CancellationTokenSource? _eventCancellation;
    private Task? _eventPump;
    private bool _disposed;

    public event Action<EmulatorEvent>? EventReceived;

    public IEmulatorSession? ActiveSession
    {
        get
        {
            lock (_sync)
            {
                return _activeSession;
            }
        }
    }

    public async Task<IEmulatorSession> StartAsync(
        IEmulatorAdapter adapter,
        SessionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(descriptor);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (ActiveSession is not null)
            {
                throw new InvalidOperationException("An emulator session is already active.");
            }

            if (!string.Equals(adapter.CoreId, descriptor.CoreId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Adapter core '{adapter.CoreId}' does not match session core '{descriptor.CoreId}'.");
            }

            var session = await adapter.LaunchAsync(descriptor, cancellationToken).ConfigureAwait(false);
            var eventCancellation = new CancellationTokenSource();
            lock (_sync)
            {
                _activeSession = session;
                _eventCancellation = eventCancellation;
                _eventPump = PumpEventsAsync(session, eventCancellation.Token);
            }

            return session;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IEmulatorSession? session;
        lock (_sync)
        {
            session = _activeSession;
        }

        if (session is not null)
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeActiveSessionAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            IEmulatorSession? session;
            CancellationTokenSource? eventCancellation;
            Task? eventPump;
            lock (_sync)
            {
                session = _activeSession;
                eventCancellation = _eventCancellation;
                eventPump = _eventPump;
                _activeSession = null;
                _eventCancellation = null;
                _eventPump = null;
            }

            eventCancellation?.Cancel();
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            if (eventPump is not null)
            {
                try
                {
                    await eventPump.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (eventCancellation?.IsCancellationRequested == true)
                {
                }
            }

            eventCancellation?.Dispose();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await DisposeActiveSessionAsync().ConfigureAwait(false);
        _operationGate.Dispose();
    }

    private async Task PumpEventsAsync(IEmulatorSession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var emulatorEvent in session.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                EventReceived?.Invoke(emulatorEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
}
