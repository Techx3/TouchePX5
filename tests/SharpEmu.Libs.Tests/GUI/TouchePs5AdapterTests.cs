// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Touche.Core.Contracts;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class TouchePs5AdapterTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "TouchePx5AdapterTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LaunchUsesExistingProcessHostAndEmitsFormalLifecycle()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var hostPath = CreateFile("TouchePx5.exe");
        var gamePath = CreateFile("eboot.bin");
        var process = new FakeEmulatorProcess();
        IReadOnlyList<string>? builtArguments = null;
        var adapter = new TouchePs5Adapter(
            hostPath,
            descriptor => builtArguments = ["--cpu-engine=native", descriptor.Game.ExecutablePath],
            _temporaryDirectory,
            () => process);

        var session = await adapter.LaunchAsync(CreateDescriptor(gamePath), CancellationToken.None);
        process.EmitOutput("runtime ready", isError: false);
        await session.StopAsync(CancellationToken.None);
        var events = await ReadAllEventsAsync(session);

        Assert.Equal(["--cpu-engine=native", gamePath], builtArguments);
        Assert.Equal(4242, session.ProcessId);
        Assert.Equal(EmulatorSessionState.Stopped, session.State);
        Assert.Equal(EmulatorExitReason.UserRequested, session.ExitReason);
        Assert.Equal(
            [
                EmulatorSessionState.Validating,
                EmulatorSessionState.Starting,
                EmulatorSessionState.Running,
                EmulatorSessionState.Stopping,
                EmulatorSessionState.Stopped,
            ],
            events.OfType<SessionStateChanged>().Select(item => item.Current));
        Assert.Contains(events, item => item is EmulatorLogReceived { Message: "runtime ready" });
        Assert.Contains(events, item => item is EmulatorProcessExited
        {
            ExitCode: EmulatorProcess.HostStopExitCode,
            Reason: EmulatorExitReason.UserRequested,
        });

        await session.DisposeAsync();
    }

    [Fact]
    public async Task ValidationRejectsMissingExecutableBeforeStartingHost()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var hostPath = CreateFile("TouchePx5.exe");
        var process = new FakeEmulatorProcess();
        var adapter = new TouchePs5Adapter(hostPath, _ => [], null, () => process);

        var validation = await adapter.ValidateGameAsync(
            new GameLaunchRequest { ExecutablePath = Path.Combine(_temporaryDirectory, "missing.bin") },
            CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Issues, issue => issue.Code == "game.path.missing");
        Assert.False(process.WasStarted);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_temporaryDirectory, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static SessionDescriptor CreateDescriptor(string gamePath) => new()
    {
        SessionId = Guid.NewGuid(),
        CoreId = ToucheCoreIds.PlayStation5,
        Game = new GameLaunchRequest { ExecutablePath = gamePath, TitleId = "PPSA00000" },
    };

    private static async Task<List<EmulatorEvent>> ReadAllEventsAsync(IEmulatorSession session)
    {
        var events = new List<EmulatorEvent>();
        await foreach (var item in session.ReadEventsAsync(CancellationToken.None))
        {
            events.Add(item);
        }

        return events;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }

    private sealed class FakeEmulatorProcess : IEmulatorProcess
    {
        public event Action<string, bool>? OutputReceived;

        public event Action<int>? Exited;

        public bool IsRunning { get; private set; }

        public int? ProcessId => WasStarted ? 4242 : null;

        public bool WasStarted { get; private set; }

        public void Start(string exePath, IReadOnlyList<string> arguments, string? workingDirectory)
        {
            WasStarted = true;
            IsRunning = true;
        }

        public void Stop()
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            Exited?.Invoke(EmulatorProcess.HostStopExitCode);
        }

        public void EmitOutput(string line, bool isError) => OutputReceived?.Invoke(line, isError);

        public void Dispose() => Stop();
    }
}
