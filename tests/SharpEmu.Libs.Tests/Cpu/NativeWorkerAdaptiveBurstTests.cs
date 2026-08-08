// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class NativeWorkerAdaptiveBurstTests
{
	[Theory]
	[InlineData("MAudioOutput")]
	[InlineData("maudiooutput")]
	[InlineData("audio_output_thread")]
	public void RecognizesLatencySensitiveAudioWorkers(string threadName)
	{
		Assert.True(DirectExecutionBackend.IsLatencySensitiveNativeWorker(threadName));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("Main Thread")]
	[InlineData("BgTaskManager")]
	public void DoesNotEscalateOrdinaryWorkers(string? threadName)
	{
		Assert.False(DirectExecutionBackend.IsLatencySensitiveNativeWorker(threadName));
	}

    private const long Frequency = 10_000_000;

    [Theory]
    [InlineData(5, 2_000_000)]
    [InlineData(6, 999_999)]
    [InlineData(100, 0)]
    public void TransientImportPatternsDoNotEnableBurst(int hits, long elapsedTicks)
    {
        Assert.False(DirectExecutionBackend.ShouldEnableNativeWorkerBurst(
            hits,
            elapsedTicks,
            Frequency));
    }

    [Theory]
    [InlineData(6, 1_000_000)]
    [InlineData(32, 20_000_000)]
    public void SustainedImportPatternsEnableBurst(int hits, long elapsedTicks)
    {
        Assert.True(DirectExecutionBackend.ShouldEnableNativeWorkerBurst(
            hits,
            elapsedTicks,
            Frequency));
    }
}
