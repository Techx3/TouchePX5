// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Touche.Core.Contracts;
using Touche.Core.Hosting;
using Xunit;

namespace Touche.Core.Hosting.Tests;

public sealed class EmulatorHostingTests
{
    [Fact]
    public void RegistryResolvesStableCoreIdsAndRejectsDuplicates()
    {
        var adapter = new FakeAdapter();
        var registry = new EmulatorCoreRegistry();

        registry.Register(adapter);

        Assert.Same(adapter, registry.GetRequired(ToucheCoreIds.PlayStation5));
        Assert.Equal([ToucheCoreIds.PlayStation5], registry.CoreIds);
        Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeAdapter()));
        Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("touche.unknown"));
    }

    [Fact]
    public async Task ManagerOwnsOneSessionAndForwardsTypedEvents()
    {
        var adapter = new FakeAdapter();
        await using var manager = new EmulatorSessionManager();
        var received = new TaskCompletionSource<EmulatorEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.EventReceived += emulatorEvent => received.TrySetResult(emulatorEvent);

        var session = await manager.StartAsync(adapter, CreateDescriptor(), CancellationToken.None);
        adapter.Session.Emit(new EmulatorLogReceived(
            DateTimeOffset.UtcNow,
            EmulatorLogLevel.Information,
            "test",
            "ready"));
        var forwarded = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(session, manager.ActiveSession);
        Assert.IsType<EmulatorLogReceived>(forwarded);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.StartAsync(adapter, CreateDescriptor(), CancellationToken.None));

        await manager.StopAsync(CancellationToken.None);
        Assert.True(adapter.Session.StopRequested);
        await manager.DisposeActiveSessionAsync();
        Assert.Null(manager.ActiveSession);
        Assert.True(adapter.Session.WasDisposed);
    }

    [Fact]
    public async Task ManagerRejectsAdapterForAnotherCore()
    {
        await using var manager = new EmulatorSessionManager();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.StartAsync(new FakeAdapter("touche.other"), CreateDescriptor(), CancellationToken.None));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
    }

    private static SessionDescriptor CreateDescriptor() => new()
    {
        SessionId = Guid.NewGuid(),
        CoreId = ToucheCoreIds.PlayStation5,
        Game = new GameLaunchRequest { ExecutablePath = "eboot.bin" },
    };

    private sealed class FakeAdapter(string coreId = ToucheCoreIds.PlayStation5) : IEmulatorAdapter
    {
        public FakeSession Session { get; } = new();

        public string CoreId { get; } = coreId;

        public Task<CoreCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CoreCapabilities());

        public Task<ValidationResult> ValidateGameAsync(
            GameLaunchRequest request,
            CancellationToken cancellationToken) => Task.FromResult(ValidationResult.Success);

        public Task<IEmulatorSession> LaunchAsync(
            SessionDescriptor descriptor,
            CancellationToken cancellationToken) => Task.FromResult<IEmulatorSession>(Session);
    }

    private sealed class FakeSession : IEmulatorSession
    {
        private readonly Channel<EmulatorEvent> _events = Channel.CreateUnbounded<EmulatorEvent>();

        public Guid SessionId { get; } = Guid.NewGuid();

        public EmulatorSessionState State => EmulatorSessionState.Running;

        public EmulatorExitReason ExitReason => EmulatorExitReason.None;

        public int? ProcessId => 42;

        public bool StopRequested { get; private set; }

        public bool WasDisposed { get; private set; }

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopRequested = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<EmulatorEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var emulatorEvent in _events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return emulatorEvent;
            }
        }

        public void Emit(EmulatorEvent emulatorEvent) => _events.Writer.TryWrite(emulatorEvent);

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
