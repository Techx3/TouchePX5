// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Threading;

namespace SharpEmu.Libs.Ngs2;

public static class Ngs2Exports
{
    private const int OrbisNgs2ErrorInvalidOutAddress = unchecked((int)0x804A0053);
    private const int OrbisNgs2ErrorInvalidSystemHandle = unchecked((int)0x804A0230);
    private const int OrbisNgs2ErrorInvalidRackHandle = unchecked((int)0x804A0261);
    private const int OrbisNgs2ErrorInvalidVoiceHandle = unchecked((int)0x804A0300);
    private const ulong HandleStorageSize = 0x20;
    private const int RenderBufferInfoSize = 0x18;
    private const ulong MaximumRenderBufferSize = 16 * 1024 * 1024;

    private static readonly object StateGate = new();
    private static readonly Dictionary<ulong, SystemState> Systems = new();
    private static readonly Dictionary<ulong, RackState> Racks = new();
    private static readonly Dictionary<ulong, VoiceState> Voices = new();
    private static long _nextUid;
    private static long _renderCount;
    private static long _unsupportedWaveformCount;
    private static long _unresolvedWaveformDumpCount;
    private static long _voiceEventTraceCount;

    // NGS2 renders one grain of interleaved float32 per sceNgs2SystemRender.
    // The grain length defaults to 256 frames (matching the 8192-byte AudioOut
    // buffers games copy it into) until the title overrides it.
    private const int DefaultGrainSamples = 256;
    private const double OutputSampleRate = 48000.0;

    private sealed class SystemState
    {
        public SystemState(uint uid) => Uid = uid;

        public uint Uid { get; }
        public int GrainSamples { get; set; } = DefaultGrainSamples;
        public long ConsecutiveSilentRenders { get; set; }
    }

    private sealed record RackState(ulong SystemHandle, uint RackId);

    private sealed class VoiceState
    {
        public VoiceState(ulong rackHandle, uint voiceIndex)
        {
            RackHandle = rackHandle;
            VoiceIndex = voiceIndex;
        }

        public ulong RackHandle { get; }
        public uint VoiceIndex { get; }

        // Software-mixer playback state. Pcm is the fully decoded left/mono
        // waveform (PcmRight carries the right channel of stereo sources);
        // Position is a fractional read cursor advanced at the source/output rate
        // ratio each output frame.
        public short[]? Pcm { get; set; }
        public short[]? PcmRight { get; set; }
        public ulong SourceAddr { get; set; }
        public int SourceRate { get; set; }
        public double Position { get; set; }
        public bool Playing { get; set; }
        public bool Paused { get; set; }
        public bool Stopped { get; set; }
        public bool ExplicitlyStopped { get; set; }
        public bool HasTransportCommand { get; set; }
        public bool CompactLifecycleArmed { get; set; }
        public bool CompactLifecycleStopped { get; set; }
        public uint WaveformType { get; set; }
        public int WaveformChannels { get; set; } = 1;
        public ulong WaveformBlocksAddress { get; set; }
        public int WaveformBlockCount { get; set; }
        public ulong PreviousWaveformBlocksAddress { get; set; }
        public int DirectPcmBufferBytes { get; set; }
        public ulong StreamingFingerprint { get; set; }
        public bool StreamingPending { get; set; }
        public ulong DestinationVoiceHandle { get; set; }
        public int LoopStart { get; set; } = -1;
        public int LoopEnd { get; set; }
        public float Gain { get; set; } = 1f;
    }

    [SysAbiExport(
        Nid = "mPYgU4oYpuY",
        ExportName = "sceNgs2SystemCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreateWithAllocator(CpuContext ctx)
    {
        var outHandleAddress = ctx[CpuRegister.Rdx];
        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 1, ownerHandle: 0, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Systems[handle] = new SystemState(unchecked((uint)Interlocked.Increment(ref _nextUid)));
        }

        return SetReturn(ctx, 0);
    }

    // Non-allocator create: identical to the WithAllocator form for our purposes.
    // The only signature difference is the caller-supplied buffer info in rsi
    // (vs an allocator callback); the system option (rdi) and out-handle (rdx)
    // sit at the same argument positions, so we reuse the same implementation.
    // Dead Cells uses these variants — leaving sceNgs2SystemCreate unresolved
    // gave the game a garbage system handle, so every later rack/voice call
    // failed and it polled sceNgs2VoiceGetState forever, freezing at FLIP 0.
    [SysAbiExport(
        Nid = "koBbCMvOKWw",
        ExportName = "sceNgs2SystemCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemCreate(CpuContext ctx) => Ngs2SystemCreateWithAllocator(ctx);

    [SysAbiExport(
        Nid = "u-WrYDaJA3k",
        ExportName = "sceNgs2SystemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Systems.Remove(handle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            var rackHandles = Racks
                .Where(pair => pair.Value.SystemHandle == handle)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var rackHandle in rackHandles)
            {
                RemoveRackLocked(rackHandle);
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "U546k6orxQo",
        ExportName = "sceNgs2RackCreateWithAllocator",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreateWithAllocator(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var rackId = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.R8];
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }
        }

        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 2, systemHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Racks[handle] = new RackState(systemHandle, rackId);
        }

        return SetReturn(ctx, 0);
    }

    // Non-allocator rack create: system handle (rdi), rack id (rsi) and the
    // out-handle (r8) share the WithAllocator argument layout, so reuse it.
    [SysAbiExport(
        Nid = "cLV4aiT9JpA",
        ExportName = "sceNgs2RackCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackCreate(CpuContext ctx) => Ngs2RackCreateWithAllocator(ctx);

    [SysAbiExport(
        Nid = "lCqD7oycmIM",
        ExportName = "sceNgs2RackDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackDestroy(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(handle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidRackHandle);
            }

            RemoveRackLocked(handle);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "MwmHz8pAdAo",
        ExportName = "sceNgs2RackGetVoiceHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackGetVoiceHandle(CpuContext ctx)
    {
        var rackHandle = ctx[CpuRegister.Rdi];
        var voiceIndex = unchecked((uint)ctx[CpuRegister.Rsi]);
        var outHandleAddress = ctx[CpuRegister.Rdx];
        lock (StateGate)
        {
            if (!Racks.ContainsKey(rackHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidRackHandle);
            }

            var existing = Voices.FirstOrDefault(
                pair => pair.Value.RackHandle == rackHandle && pair.Value.VoiceIndex == voiceIndex);
            if (existing.Key != 0)
            {
                return ctx.TryWriteUInt64(outHandleAddress, existing.Key)
                    ? SetReturn(ctx, 0)
                    : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        if (outHandleAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        if (!TryCreateHandle(ctx, type: 4, rackHandle, out var handle) ||
            !ctx.TryWriteUInt64(outHandleAddress, handle))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        lock (StateGate)
        {
            Voices[handle] = new VoiceState(rackHandle, voiceIndex);
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "uu94irFOGpA",
        ExportName = "sceNgs2VoiceControl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceControl(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var paramList = ctx[CpuRegister.Rsi];
        lock (StateGate)
        {
            if (!Voices.ContainsKey(voiceHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }
        }

        if (ShouldTrace())
        {
            TraceVoiceParamList(ctx, voiceHandle, paramList);
        }

        if (UseSoftwareMixer())
        {
            HandleVoiceParams(ctx, voiceHandle, paramList);
        }
        return SetReturn(ctx, 0);
    }

    // Parse SceNgs2VoiceParamHead as u16 size, s16 next and u32 id. The signed
    // next field is the byte offset to the following command. Reading size and
    // next as one u32 skipped chained stop/kill events and left menu voices alive.
    private static void HandleVoiceParams(CpuContext ctx, ulong voiceHandle, ulong paramList)
    {
        if (paramList == 0)
        {
            return;
        }

        var offset = paramList;
        for (var guard = 0; guard < 32; guard++)
        {
            if (!ctx.TryReadUInt16(offset, out var size) ||
                !ctx.TryReadUInt16(offset + 2, out var nextRaw) ||
                !ctx.TryReadUInt32(offset + 4, out var id))
            {
                return;
            }

            // M2's NGS2 client also emits an eight-byte lifecycle pulse encoded
            // as { 2, 0x400 }. The first pulse follows waveform/patch setup and
            // commits the voice; a later pulse for the same waveform retires it.
            // Treating this as an invalid two-byte parameter left every previous
            // music voice mixing across title, selection and gameplay screens.
            if (IsCompactLifecyclePulse(size, nextRaw, id))
            {
                ApplyCompactLifecyclePulse(voiceHandle);
                return;
            }

            switch (id)
            {
                case 0x00000005:
                    ApplyVoicePatchParam(ctx, voiceHandle, offset);
                    break;
                case 0x00000002:
                    ApplyPortVolumeParam(ctx, voiceHandle, offset);
                    break;
                case 0x10000001:
                    ApplyWaveformParam(ctx, voiceHandle, offset);
                    break;
                case 0x10000000:
                    ApplyWaveformFormatParam(ctx, voiceHandle, offset);
                    break;
                case 0x20010001:
                    ApplyPortMatrixParam(ctx, voiceHandle, offset);
                    break;
                case 0x00000006:
                    ApplyVoiceEventParam(ctx, voiceHandle, offset);
                    break;
            }

            if (size < 8 || size > 0x1000)
            {
                return;
            }

            var next = unchecked((short)nextRaw);
            if (next == 0)
            {
                return;
            }

            if (next < 8 || next > 0x1000)
            {
                return;
            }

            offset += unchecked((ulong)next);
        }
    }

    private static void ApplyVoiceEventParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset)
    {
        if (!ctx.TryReadUInt32(paramOffset + 8, out var eventId))
        {
            return;
        }

        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return;
            }

            switch (eventId)
            {
                case 0x0001: // Play
                    // A play event can precede the waveform command in the same
                    // list. Keep the requested state even when PCM is not armed
                    // yet; the mixer will begin once the waveform is available.
                    voice.Position = 0;
                    voice.Paused = false;
                    voice.Stopped = false;
                    voice.ExplicitlyStopped = false;
                    voice.Playing = true;
                    voice.HasTransportCommand = true;
                    break;
                case 0x0002: // Stop
                case 0x0004: // Stop immediately
                    voice.Playing = false;
                    voice.Paused = false;
                    voice.Stopped = true;
                    voice.ExplicitlyStopped = true;
                    voice.Position = 0;
                    voice.HasTransportCommand = true;
                    break;
                case 0x0008: // Kill
                    voice.Playing = false;
                    voice.Paused = false;
                    voice.Stopped = false;
                    voice.ExplicitlyStopped = true;
                    voice.Position = 0;
                    voice.Pcm = null;
                    voice.PcmRight = null;
                    voice.SourceAddr = 0;
                    voice.HasTransportCommand = true;
                    break;
                case 0x0010: // Pause
                    if (voice.Playing)
                    {
                        voice.Playing = false;
                        voice.Paused = true;
                        voice.Stopped = false;
                    }
                    voice.HasTransportCommand = true;
                    break;
                case 0x0020: // Resume
                    if (voice.Paused && voice.Pcm is not null)
                    {
                        voice.Paused = false;
                        voice.Stopped = false;
                        voice.ExplicitlyStopped = false;
                        voice.Playing = true;
                    }
                    voice.HasTransportCommand = true;
                    break;
            }

            var traceCount = Interlocked.Increment(ref _voiceEventTraceCount);
            if (traceCount <= 16 || (traceCount & (traceCount - 1)) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] ngs2.voice_event count={traceCount} " +
                    $"voice=0x{voiceHandle:X16} event=0x{eventId:X} " +
                    $"playing={voice.Playing} paused={voice.Paused} stopped={voice.Stopped}");
            }
        }
    }

    internal static bool IsCompactLifecyclePulse(ushort size, ushort next, uint id)
        => size == 2 && next == 0 && id == 0x00000400;

    private static void ApplyCompactLifecyclePulse(ulong voiceHandle)
    {
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return;
            }

            if (!voice.CompactLifecycleArmed)
            {
                voice.CompactLifecycleArmed = true;
                return;
            }

            // Streaming sampler voices use this compact packet while rotating
            // waveform blocks. Retiring those voices here silences all PCM
            // gameplay audio after the menu transition. Their lifetime is
            // instead controlled by route removal and explicit transport.
            if (!ShouldRetireOnCompactLifecycle(
                    voice.StreamingPending,
                    voice.WaveformType,
                    voice.Pcm is { Length: > 0 },
                    voice.LoopStart))
            {
                return;
            }

            voice.Playing = false;
            voice.Paused = false;
            voice.Stopped = true;
            voice.Position = 0;
            voice.CompactLifecycleStopped = true;

            var traceCount = Interlocked.Increment(ref _voiceEventTraceCount);
            if (traceCount <= 16 || (traceCount & (traceCount - 1)) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] ngs2.voice_lifecycle_stop count={traceCount} " +
                    $"voice=0x{voiceHandle:X16}");
            }
        }
    }

    internal static bool ShouldRetireOnCompactLifecycle(
        bool streamingPending, uint waveformType, bool hasPcm, int loopStart)
    {
        // PCM sampler voices rotate guest-owned buffers and use the compact
        // packet as part of that update protocol. One-shot VAG voices use it
        // while their effect is active and must be allowed to reach EOF. Only
        // an armed looping VAG voice represents persistent music that needs to
        // be retired when M2 moves to the next screen.
        if (streamingPending || (waveformType != 0 && waveformType != 0x80))
        {
            return false;
        }

        return hasPcm && loopStart >= 0;
    }

    // SceNgs2VoicePatchParam: header, source port, destination input and the
    // destination voice handle. Some M2 titles configure a waveform and route
    // it directly into the mastering rack without sending a separate Play
    // event. Treat that completed route as the implicit transport request, but
    // never override an explicit stop/pause/kill event.
    private static void ApplyVoicePatchParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset)
    {
        if (!ctx.TryReadUInt64(paramOffset + 16, out var destinationVoiceHandle))
        {
            return;
        }

        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return;
            }

            voice.DestinationVoiceHandle = destinationVoiceHandle;
            if (destinationVoiceHandle == 0)
            {
                // A removed route must become silent immediately. Do not mark
                // it as an explicit transport stop: reconnecting the same
                // implicit voice is allowed to restart it.
                voice.Playing = false;
                voice.Paused = false;
                voice.Position = 0;
                return;
            }

            if (!voice.HasTransportCommand &&
                !voice.CompactLifecycleStopped &&
                voice.Pcm is { Length: > 0 })
            {
                voice.Position = 0;
                voice.Paused = false;
                voice.Stopped = false;
                voice.Playing = true;
            }
        }
    }

    private static void ApplyWaveformFormatParam(
        CpuContext ctx, ulong voiceHandle, ulong paramOffset)
    {
        if (!ctx.TryReadUInt32(paramOffset + 8, out var waveformType) ||
            !ctx.TryReadUInt32(paramOffset + 12, out var channelsRaw) ||
            !ctx.TryReadUInt32(paramOffset + 16, out var sampleRateRaw))
        {
            return;
        }

        var channels = channelsRaw is >= 1 and <= 8 ? (int)channelsRaw : 1;
        var sampleRate = sampleRateRaw is >= 8_000 and <= 384_000
            ? (int)sampleRateRaw
            : (int)OutputSampleRate;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return;
            }

            voice.WaveformType = waveformType;
            voice.WaveformChannels = channels;
            voice.SourceRate = sampleRate;
            if (waveformType != 0x80)
            {
                voice.StreamingPending = true;
                voice.CompactLifecycleStopped = false;
                if (!voice.ExplicitlyStopped)
                {
                    voice.Stopped = false;
                }
            }
        }
    }

    // Waveform-blocks param. VAG voices usually reference a complete VAGp
    // container; PCM sampler voices rotate guest-owned waveform blocks.
    private static void ApplyWaveformParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset)
    {
        if (!ctx.TryReadUInt64(paramOffset + 8, out var blocksAddress) ||
            !ctx.TryReadUInt32(paramOffset + 16, out var blockCountRaw))
        {
            return;
        }

        var knownStreaming = false;
        lock (StateGate)
        {
            knownStreaming = Voices.TryGetValue(voiceHandle, out var voice) &&
                voice.WaveformType != 0 && voice.WaveformType != 0x80;
        }

        // The format command can follow the first block command. Resolve VAG
        // whenever the type is still unknown so ordinary effects are not
        // mistaken for PCM simply because their initial type is zero.
        if (!knownStreaming && TryResolveVagDataAddress(ctx, paramOffset, out var vagAddress))
        {
            ArmVagVoice(ctx, voiceHandle, vagAddress);
            return;
        }

        if (blocksAddress > 0x10000)
        {
            lock (StateGate)
            {
                if (Voices.TryGetValue(voiceHandle, out var voice))
                {
                    if (voice.WaveformBlocksAddress != 0 &&
                        voice.WaveformBlocksAddress != blocksAddress)
                    {
                        voice.PreviousWaveformBlocksAddress = voice.WaveformBlocksAddress;
                        var distance = voice.WaveformBlocksAddress > blocksAddress
                            ? voice.WaveformBlocksAddress - blocksAddress
                            : blocksAddress - voice.WaveformBlocksAddress;
                        if (distance is >= 256 and <= 4 * 1024 * 1024 &&
                            (distance & 3) == 0)
                        {
                            voice.DirectPcmBufferBytes = (int)distance;
                        }
                    }

                    voice.WaveformBlocksAddress = blocksAddress;
                    voice.WaveformBlockCount = (int)Math.Clamp(blockCountRaw, 1u, 64u);
                    voice.StreamingPending = true;
                    voice.CompactLifecycleStopped = false;
                }
            }
        }

        if (knownStreaming)
        {
            return;
        }

        var failure = Interlocked.Increment(ref _unsupportedWaveformCount);
        if (failure <= 8 || (failure & (failure - 1)) == 0)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] ngs2.waveform_unresolved count={failure} " +
                $"voice=0x{voiceHandle:X16} param=0x{paramOffset:X}");
        }
        if (Interlocked.Increment(ref _unresolvedWaveformDumpCount) <= 4)
        {
            TraceUnresolvedWaveform(ctx, voiceHandle, paramOffset);
        }
    }

    private static void ArmVagVoice(CpuContext ctx, ulong voiceHandle, ulong dataAddr)
    {
        lock (StateGate)
        {
            if (Voices.TryGetValue(voiceHandle, out var existing) &&
                existing.SourceAddr == dataAddr && existing.Pcm is not null)
            {
                // Same waveform already armed — don't restart it every frame.
                return;
            }
        }

        Span<byte> header = stackalloc byte[Ngs2VagDecoder.VagHeaderSize];
        ctx.Memory.TryRead(dataAddr, header);

        var declaredSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header[0x0C..]);
        var totalBytes = Ngs2VagDecoder.VagHeaderSize + Math.Clamp(declaredSize, 0, 8 * 1024 * 1024);
        var raw = System.Buffers.ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            if (!ctx.Memory.TryRead(dataAddr, raw.AsSpan(0, totalBytes)) ||
                !Ngs2VagDecoder.TryDecode(raw.AsSpan(0, totalBytes), out var waveform))
            {
                return;
            }

            lock (StateGate)
            {
                if (!Voices.TryGetValue(voiceHandle, out var voice))
                {
                    return;
                }

                voice.Pcm = waveform.Samples;
                voice.PcmRight = waveform.RightSamples;
                voice.SourceAddr = dataAddr;
                voice.SourceRate = waveform.SampleRate;
                voice.CompactLifecycleArmed = false;
                voice.CompactLifecycleStopped = false;
                voice.LoopStart = waveform.LoopStart;
                voice.LoopEnd = waveform.LoopEnd > 0 ? waveform.LoopEnd : waveform.Samples.Length;
                voice.Position = 0;
                voice.Paused = false;
                // Loading/changing a waveform is configuration, not a play
                // request. Auto-starting here revived stopped menu music when a
                // later effect updated the same NGS2 command list.
                // M2's implicit variant is started only after it is patched to
                // a destination voice; explicit transport state still wins.
                if (voice.DestinationVoiceHandle != 0 && !voice.HasTransportCommand)
                {
                    voice.Stopped = false;
                    voice.Playing = true;
                }
            }

            if (ShouldTrace())
            {
                var peak = 0;
                for (var i = 0; i < waveform.Samples.Length; i++)
                {
                    peak = Math.Max(peak, Math.Abs((int)waveform.Samples[i]));
                }

                Console.Error.WriteLine(
                    $"[LOADER][TRACE] ngs2.arm voice=0x{voiceHandle:X16} addr=0x{dataAddr:X} rate={waveform.SampleRate} " +
                    $"samples={waveform.Samples.Length} stereo={waveform.RightSamples is not null} loop={waveform.LoopStart} peak={peak}");
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
        }
    }

    // Refresh a guest-owned PCM block only when its contents change. M2 uses
    // alternating 48-kHz stereo buffers for continuous gameplay audio. Some
    // clients pass a waveform-block descriptor, while others pass PCM directly.
    private static void RefreshStreamingVoice(CpuContext ctx, VoiceState voice)
    {
        if (!voice.StreamingPending ||
            voice.WaveformType != Ngs2PcmDecoder.Signed16LittleEndian ||
            voice.WaveformBlocksAddress <= 0x10000 ||
            voice.WaveformChannels is < 1 or > 8)
        {
            return;
        }

        var dataAddress = voice.WaveformBlocksAddress;
        var byteCount = voice.DirectPcmBufferBytes;
        var requestedFrames = 0;

        Span<byte> descriptor = stackalloc byte[40];
        if (ctx.Memory.TryRead(voice.WaveformBlocksAddress, descriptor))
        {
            var descriptorData = BinaryPrimitives.ReadUInt64LittleEndian(descriptor);
            var descriptorBytes = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[8..]);
            var descriptorFrames = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[24..]);
            if (descriptorData > 0x10000 &&
                descriptorBytes is >= 2 and <= MaximumRenderBufferSize &&
                descriptorBytes % (ulong)(sizeof(short) * voice.WaveformChannels) == 0)
            {
                dataAddress = descriptorData;
                byteCount = (int)descriptorBytes;
                requestedFrames = descriptorFrames <= int.MaxValue
                    ? (int)descriptorFrames
                    : 0;
            }
        }

        if (byteCount < sizeof(short) * voice.WaveformChannels ||
            byteCount > (int)MaximumRenderBufferSize)
        {
            return;
        }

        var raw = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var payload = raw.AsSpan(0, byteCount);
            if (!ctx.Memory.TryRead(dataAddress, payload))
            {
                return;
            }

            var fingerprint = ComputeStreamingFingerprint(dataAddress, payload);
            if (fingerprint == voice.StreamingFingerprint && voice.Pcm is { Length: > 0 })
            {
                return;
            }

            if (!Ngs2PcmDecoder.TryDecodeInterleaved(
                    payload, voice.WaveformChannels, requestedFrames, out var left, out var right))
            {
                return;
            }

            voice.Pcm = left;
            voice.PcmRight = right;
            voice.SourceAddr = dataAddress;
            voice.StreamingFingerprint = fingerprint;
            voice.LoopStart = -1;
            voice.LoopEnd = left.Length;
            voice.Position = 0;
            voice.Paused = false;
            if (!voice.ExplicitlyStopped && voice.DestinationVoiceHandle != 0)
            {
                voice.Stopped = false;
                voice.Playing = true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static ulong ComputeStreamingFingerprint(ulong address, ReadOnlySpan<byte> bytes)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = (offsetBasis ^ address) * prime;
        hash = (hash ^ (uint)bytes.Length) * prime;
        foreach (var value in bytes)
        {
            hash = (hash ^ value) * prime;
        }

        return hash;
    }

    private static void TraceUnresolvedWaveform(
        CpuContext ctx, ulong voiceHandle, ulong paramOffset)
    {
        Span<byte> param = stackalloc byte[64];
        param.Clear();
        ctx.Memory.TryRead(paramOffset, param);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] ngs2.waveform_param voice=0x{voiceHandle:X16} " +
            $"addr=0x{paramOffset:X} bytes={Convert.ToHexString(param)}");

        Span<byte> pointed = stackalloc byte[64];
        for (var offset = 8; offset + sizeof(ulong) <= param.Length; offset += sizeof(ulong))
        {
            var candidate = BinaryPrimitives.ReadUInt64LittleEndian(param[offset..]);
            if (candidate <= 0x10000)
            {
                continue;
            }

            pointed.Clear();
            if (ctx.Memory.TryRead(candidate, pointed))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] ngs2.waveform_pointer field=+{offset} " +
                    $"ptr=0x{candidate:X} bytes={Convert.ToHexString(pointed)}");
            }
        }
    }

    // Some NGS2 clients pass the VAG container directly while others put it in
    // a waveform-block descriptor. Resolve both layouts without assuming that
    // every pointer-shaped field is valid guest memory.
    private static bool TryResolveVagDataAddress(
        CpuContext ctx, ulong paramOffset, out ulong dataAddress)
    {
        dataAddress = 0;
        if (!ctx.TryReadUInt16(paramOffset, out var paramSize))
        {
            return false;
        }

        var scanBytes = Math.Clamp((int)paramSize, 16, 64);
        for (var fieldOffset = 8; fieldOffset + sizeof(ulong) <= scanBytes; fieldOffset += sizeof(ulong))
        {
            if (!ctx.TryReadUInt64(paramOffset + (ulong)fieldOffset, out var candidate) ||
                candidate <= 0x10000)
            {
                continue;
            }

            if (IsVagAddress(ctx, candidate))
            {
                dataAddress = candidate;
                return true;
            }

            // One level of indirection covers SceNgs2WaveformBlock-style
            // descriptors while keeping malformed guest pointers bounded.
            for (var nestedOffset = 0; nestedOffset < 64; nestedOffset += sizeof(ulong))
            {
                if (!ctx.TryReadUInt64(candidate + (ulong)nestedOffset, out var nested) ||
                    nested <= 0x10000 || !IsVagAddress(ctx, nested))
                {
                    continue;
                }

                dataAddress = nested;
                return true;
            }
        }

        return false;
    }

    private static bool IsVagAddress(CpuContext ctx, ulong address)
    {
        Span<byte> header = stackalloc byte[Ngs2VagDecoder.VagHeaderSize];
        return ctx.Memory.TryRead(address, header) && Ngs2VagDecoder.IsVag(header);
    }

    // Common voice port-volume param: header, port, then float level.
    private static void ApplyPortVolumeParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset) =>
        ApplyVoiceGain(ctx, voiceHandle, paramOffset + 12);

    // Reverb/custom port matrix fallback retained for titles that encode a
    // scalar level in this slot.
    private static void ApplyPortMatrixParam(CpuContext ctx, ulong voiceHandle, ulong paramOffset)
        => ApplyVoiceGain(ctx, voiceHandle, paramOffset + 12);

    private static void ApplyVoiceGain(
        CpuContext ctx, ulong voiceHandle, ulong levelAddress)
    {
        if (!ctx.TryReadUInt32(levelAddress, out var levelBits))
        {
            return;
        }

        var level = BitConverter.UInt32BitsToSingle(levelBits);
        if (!float.IsFinite(level) || level < 0f || level > 8f)
        {
            return;
        }

        lock (StateGate)
        {
            if (Voices.TryGetValue(voiceHandle, out var voice))
            {
                voice.Gain = level;
            }
        }
    }

    // Empirically dump the SceNgs2VoiceParamHead-chained command list so we can
    // confirm the real struct layout (size/next/id) against public NGS2 sources
    // before building the software mixer. Assumed header: u16 size, s16 next
    // (byte offset to the next block, 0 = end), u32 id.
    private static void TraceVoiceParamList(CpuContext ctx, ulong voiceHandle, ulong paramList)
    {
        if (paramList == 0)
        {
            return;
        }

        Span<byte> peek = stackalloc byte[32];
        var offset = paramList;
        for (int guard = 0; guard < 32; guard++)
        {
            if (!ctx.TryReadUInt16(offset, out var size) ||
                !ctx.TryReadUInt16(offset + 2, out var next) ||
                !ctx.TryReadUInt32(offset + 4, out var id))
            {
                Console.Error.WriteLine($"[LOADER][TRACE] ngs2.voiceparam voice=0x{voiceHandle:X16} @0x{offset:X}: unreadable header");
                return;
            }

            peek.Clear();
            var readable = Math.Min((int)Math.Max((ushort)8, size), peek.Length);
            ctx.Memory.TryRead(offset, peek[..readable]);
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.voiceparam voice=0x{voiceHandle:X16} id=0x{id:X} size={size} next={unchecked((short)next)} bytes={Convert.ToHexString(peek[..readable])}");

            // For the waveform-blocks param, follow the embedded pointers and
            // dump the pointed-to bytes so we can tell PCM16 from ATRAC9.
            if (id == 0x10000001 && Interlocked.Increment(ref _waveformDumps) <= 8)
            {
                for (int po = 8; po + 8 <= readable; po += 8)
                {
                    if (ctx.TryReadUInt64(offset + (ulong)po, out var ptr) && ptr > 0x10000 &&
                        ctx.Memory.TryRead(ptr, peek))
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] ngs2.waveform @+{po} ptr=0x{ptr:X} head={Convert.ToHexString(peek)}");
                    }
                }
            }

            var advance = unchecked((short)next);
            if (advance <= 0)
            {
                return;
            }

            offset += (ulong)advance;
        }
    }

    private static long _waveformDumps;
    private static long _renderInfoDumps;

    [SysAbiExport(
        Nid = "AbYvTOZ8Pts",
        ExportName = "sceNgs2VoiceRunCommands",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceRunCommands(CpuContext ctx) => Ngs2VoiceControl(ctx);

    [SysAbiExport(
        Nid = "i0VnXM-C9fc",
        ExportName = "sceNgs2SystemRender",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemRender(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var bufferInfoAddress = ctx[CpuRegister.Rsi];
        var bufferInfoCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        lock (StateGate)
        {
            if (!Systems.ContainsKey(systemHandle))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }
        }

        if (bufferInfoCount != 0 && bufferInfoAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        for (uint i = 0; i < bufferInfoCount; i++)
        {
            var entryAddress = bufferInfoAddress + (i * RenderBufferInfoSize);
            if (!ctx.TryReadUInt64(entryAddress, out var bufferAddress) ||
                !ctx.TryReadUInt64(entryAddress + 8, out var bufferSize))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }

            if (bufferAddress != 0 && bufferSize != 0)
            {
                if (bufferSize > MaximumRenderBufferSize || !TryClearGuestBuffer(ctx, bufferAddress, bufferSize))
                {
                    return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }

                // SceNgs2RenderBufferInfo: {ptr@0, size@8, waveformType@16,
                // channelsCount@20}. Mix the armed voices into the leading grain
                // as interleaved float32 — this is what the game copies to
                // sceAudioOutOutput, so it is where NGS2 audio must appear.
                var channels = 2;
                if (ctx.TryReadUInt32(entryAddress + 20, out var declaredChannels) &&
                    declaredChannels is > 0 and <= 8)
                {
                    channels = (int)declaredChannels;
                }

                if (UseSoftwareMixer())
                {
                    MixVoicesIntoGrain(ctx, systemHandle, bufferAddress, bufferSize, channels);
                }

                if (ShouldTrace() && Interlocked.Increment(ref _renderInfoDumps) <= 4)
                {
                    Span<byte> rbi = stackalloc byte[RenderBufferInfoSize];
                    ctx.Memory.TryRead(entryAddress, rbi);
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] ngs2.renderbufinfo addr=0x{bufferAddress:X} size={bufferSize} ch={channels} raw={Convert.ToHexString(rbi)}");
                }
            }
        }

        var count = Interlocked.Increment(ref _renderCount);
        if (ShouldTrace() && (count <= 4 || count % 200 == 0))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] ngs2.render#{count} system=0x{systemHandle:X16} buffers={bufferInfoCount}");
        }

        return SetReturn(ctx, 0);
    }

    // Sum every armed voice belonging to this system into the leading grain of
    // the render buffer as interleaved float32. The buffer was just zeroed, so
    // this is a plain additive mix; silence stays silence when nothing plays.
    private static void MixVoicesIntoGrain(
        CpuContext ctx, ulong systemHandle, ulong bufferAddress, ulong bufferSize, int channels)
    {
        int grain;
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system))
            {
                return;
            }

            grain = system.GrainSamples;
        }

        var capacityFrames = (int)Math.Min((ulong)grain, bufferSize / (ulong)(channels * sizeof(float)));
        if (capacityFrames <= 0)
        {
            return;
        }

        var floatCount = capacityFrames * channels;
        var accum = ArrayPool<float>.Shared.Rent(floatCount);
        var mixedAnything = false;
        var armedVoices = 0;
        var playingVoices = 0;
        try
        {
            Array.Clear(accum, 0, floatCount);
            lock (StateGate)
            {
                foreach (var pair in Voices)
                {
                    var voice = pair.Value;
                    RefreshStreamingVoice(ctx, voice);
                    if (voice.Pcm is not null && voice.Pcm.Length != 0)
                    {
                        armedVoices++;
                    }
                    if (voice.Playing)
                    {
                        playingVoices++;
                    }
                    if (!voice.Playing || voice.Pcm is null || voice.Pcm.Length == 0)
                    {
                        continue;
                    }

                    if (!Racks.TryGetValue(voice.RackHandle, out var rack) ||
                        rack.SystemHandle != systemHandle)
                    {
                        continue;
                    }

                    MixOneVoice(accum, capacityFrames, channels, voice);
                    mixedAnything = true;
                }
            }

            if (mixedAnything)
            {
                WriteGrain(ctx, bufferAddress, accum, floatCount);
            }

            TraceMixerHealth(systemHandle, mixedAnything, armedVoices, playingVoices);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(accum);
        }
    }

    private static void TraceMixerHealth(
        ulong systemHandle, bool mixedAnything, int armedVoices, int playingVoices)
    {
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system))
            {
                return;
            }

            if (mixedAnything)
            {
                var previous = system.ConsecutiveSilentRenders;
                system.ConsecutiveSilentRenders = 0;
                if (previous >= 256)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] ngs2.mix_recovered system=0x{systemHandle:X16} " +
                        $"silent_renders={previous} armed={armedVoices} playing={playingVoices}");
                }
                return;
            }

            var count = ++system.ConsecutiveSilentRenders;
            if (count >= 256 && (count & (count - 1)) == 0)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] ngs2.silent_mix system=0x{systemHandle:X16} " +
                    $"renders={count} armed={armedVoices} playing={playingVoices}");
            }
        }
    }

    // Resample one voice from its source rate to 48 kHz (linear interpolation)
    // and add it to the front stereo pair. Advances the voice cursor and handles
    // loop / one-shot end. Must be called under StateGate.
    private static void MixOneVoice(float[] accum, int frames, int channels, VoiceState voice)
    {
        var pcm = voice.Pcm!;
        var pcmRight = voice.PcmRight;
        if (pcmRight is not null && pcmRight.Length < pcm.Length)
        {
            // Guard against a short right channel; treat the tail as mono.
            pcmRight = null;
        }

        var loopEnd = voice.LoopEnd > 0 && voice.LoopEnd <= pcm.Length ? voice.LoopEnd : pcm.Length;
        var loopStart = voice.LoopStart;
        var step = voice.SourceRate / OutputSampleRate;
        var gain = voice.Gain / 32768f;
        var pos = voice.Position;
        for (var f = 0; f < frames; f++)
        {
            var idx = (int)pos;
            if (idx >= loopEnd)
            {
                if (loopStart >= 0 && loopStart < loopEnd)
                {
                    pos = loopStart + (pos - loopEnd);
                    if (pos < loopStart || pos >= loopEnd)
                    {
                        pos = loopStart;
                    }

                    idx = (int)pos;
                }
                else
                {
                    voice.Playing = false;
                    voice.Stopped = true;
                    break;
                }
            }

            if (idx < 0 || idx >= pcm.Length)
            {
                voice.Playing = false;
                voice.Stopped = true;
                break;
            }

            // Linear interpolation between idx and the next source sample
            // (staying inside the loop region) removes the stair-step aliasing
            // a nearest-sample fetch produces on 44.1 -> 48 kHz music.
            var frac = (float)(pos - idx);
            var next = idx + 1;
            if (next >= loopEnd)
            {
                next = loopStart >= 0 && loopStart < loopEnd ? loopStart : idx;
            }

            var left = pcm[idx] + ((pcm[next] - pcm[idx]) * frac);
            var right = pcmRight is null
                ? left
                : pcmRight[idx] + ((pcmRight[next] - pcmRight[idx]) * frac);
            var baseIndex = f * channels;
            accum[baseIndex] += left * gain;
            if (channels > 1)
            {
                accum[baseIndex + 1] += right * gain;
            }

            pos += step;
        }

        voice.Position = pos;
    }

    private static void WriteGrain(CpuContext ctx, ulong address, float[] accum, int count)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(count * sizeof(float));
        try
        {
            var span = bytes.AsSpan(0, count * sizeof(float));
            for (var i = 0; i < count; i++)
            {
                var value = Math.Clamp(accum[i], -1f, 1f);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * sizeof(float), sizeof(float)), value);
            }

            ctx.Memory.TryWrite(address, span);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    [SysAbiExport(
        Nid = "pgFAiLR5qT4",
        ExportName = "sceNgs2SystemQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemQueryBufferSize(CpuContext ctx) => WriteBufferSize(ctx, ctx[CpuRegister.Rsi]);

    [SysAbiExport(
        Nid = "0eFLVCfWVds",
        ExportName = "sceNgs2RackQueryBufferSize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackQueryBufferSize(CpuContext ctx) => WriteBufferSize(ctx, ctx[CpuRegister.Rdx]);

    // Report a fixed working-memory footprint for the requested object. The
    // out struct (SceNgs2BufferAllocator-style) begins with the size field.
    private static int WriteBufferSize(CpuContext ctx, ulong outAddress)
    {
        if (outAddress == 0)
        {
            return SetReturn(ctx, OrbisNgs2ErrorInvalidOutAddress);
        }

        Span<byte> info = stackalloc byte[RenderBufferInfoSize];
        info.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(info[0..8], 0x10000);
        BinaryPrimitives.WriteUInt64LittleEndian(info[8..16], 0x100);
        return ctx.Memory.TryWrite(outAddress, info)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "l4Q2dWEH6UM",
        ExportName = "sceNgs2SystemSetGrainSamples",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetGrainSamples(CpuContext ctx)
    {
        var systemHandle = ctx[CpuRegister.Rdi];
        var grain = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (StateGate)
        {
            if (!Systems.TryGetValue(systemHandle, out var system))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidSystemHandle);
            }

            if (grain > 0 && grain <= 8192)
            {
                system.GrainSamples = grain;
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "-tbc2SxQD60",
        ExportName = "sceNgs2SystemSetSampleRate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemSetSampleRate(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "gThZqM5PYlQ",
        ExportName = "sceNgs2SystemLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemLock(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "JXRC5n0RQls",
        ExportName = "sceNgs2SystemUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2SystemUnlock(CpuContext ctx) => ValidateSystem(ctx);

    [SysAbiExport(
        Nid = "-TOuuAQ-buE",
        ExportName = "sceNgs2VoiceGetState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetState(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var stateAddress = ctx[CpuRegister.Rsi];
        var stateSize = (int)Math.Min(ctx[CpuRegister.Rdx], 0x400);
        uint stateFlags;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }
            stateFlags = GetVoiceStateFlags(voice);
        }

        if (stateAddress != 0 && stateSize > 0)
        {
            if (!TryClearGuestBuffer(ctx, stateAddress, (ulong)stateSize))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
            if (stateSize >= sizeof(uint) && !ctx.TryWriteUInt32(stateAddress, stateFlags))
            {
                return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
        }

        return SetReturn(ctx, 0);
    }

    [SysAbiExport(
        Nid = "rEh728kXk3w",
        ExportName = "sceNgs2VoiceGetStateFlags",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2VoiceGetStateFlags(CpuContext ctx)
    {
        var voiceHandle = ctx[CpuRegister.Rdi];
        var flagsAddress = ctx[CpuRegister.Rsi];
        uint stateFlags;
        lock (StateGate)
        {
            if (!Voices.TryGetValue(voiceHandle, out var voice))
            {
                return SetReturn(ctx, OrbisNgs2ErrorInvalidVoiceHandle);
            }
            stateFlags = GetVoiceStateFlags(voice);
        }

        // The ABI uses a uint32_t output. Writing eight bytes here overwrote
        // adjacent guest state in titles that placed the flag on the stack.
        if (flagsAddress != 0 && !ctx.TryWriteUInt32(flagsAddress, stateFlags))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        return SetReturn(ctx, 0);
    }

    private static uint GetVoiceStateFlags(VoiceState voice) =>
        voice.Playing ? 0x3u :
        voice.Paused ? 0x5u :
        voice.Stopped ? 0xBu : 0u;

    private static int ValidateSystem(CpuContext ctx)
    {
        lock (StateGate)
        {
            return SetReturn(
                ctx,
                Systems.ContainsKey(ctx[CpuRegister.Rdi]) ? 0 : OrbisNgs2ErrorInvalidSystemHandle);
        }
    }

    private static bool TryCreateHandle(CpuContext ctx, uint type, ulong ownerHandle, out ulong handle)
    {
        handle = 0;
        if (!KernelMemoryCompatExports.TryAllocateHleData(ctx, HandleStorageSize, 16, out handle))
        {
            return false;
        }

        Span<byte> data = stackalloc byte[(int)HandleStorageSize];
        data.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(data[0..8], handle);
        BinaryPrimitives.WriteUInt64LittleEndian(data[8..16], ownerHandle);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..20], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..28], type);
        return ctx.Memory.TryWrite(handle, data);
    }

    private static bool TryClearGuestBuffer(CpuContext ctx, ulong address, ulong length)
    {
        Span<byte> zeroes = stackalloc byte[4096];
        zeroes.Clear();
        for (ulong offset = 0; offset < length;)
        {
            var chunkSize = (int)Math.Min((ulong)zeroes.Length, length - offset);
            if (!ctx.Memory.TryWrite(address + offset, zeroes[..chunkSize]))
            {
                return false;
            }

            offset += unchecked((uint)chunkSize);
        }

        return true;
    }

    private static void RemoveRackLocked(ulong rackHandle)
    {
        Racks.Remove(rackHandle);
        foreach (var voiceHandle in Voices
                     .Where(pair => pair.Value.RackHandle == rackHandle)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            Voices.Remove(voiceHandle);
        }
    }

    private static bool ShouldTrace() =>
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_NGS2"),
            "1",
            StringComparison.Ordinal);

    // Keep a diagnostic escape hatch, but use the mixer by default now that
    // Vita/PS4 HEVAG streams are decoded with their native predictor table.
    private static bool UseSoftwareMixer() =>
        !string.Equals(
            Environment.GetEnvironmentVariable("TOUCHEPX5_NGS2_SOFTWARE_MIXER"),
            "0",
            StringComparison.Ordinal);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
    [SysAbiExport(
        Nid = "xa8oL9dmXkM",
        ExportName = "sceNgs2PanInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2PanInit(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "1WsleK-MTkE",
        ExportName = "sceNgs2GeomCalcListener",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomCalcListener(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "0lbbayqDNoE",
        ExportName = "sceNgs2GeomResetSourceParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetSourceParam(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "7Lcfo8SmpsU",
        ExportName = "sceNgs2GeomResetListenerParam",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2GeomResetListenerParam(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "MzTa7VLjogY",
        ExportName = "sceNgs2RackLock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackLock(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "++YZ7P9e87U",
        ExportName = "sceNgs2RackUnlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2RackUnlock(CpuContext ctx) => ctx.SetReturn(0);

    [SysAbiExport(
        Nid = "hyVLT2VlOYk",
        ExportName = "sceNgs2ParseWaveformData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2ParseWaveformData(CpuContext ctx)
    {
        const int waveformInfoSize = 232;
        const uint waveformTypeVag = 0x80;
        var dataAddress = ctx[CpuRegister.Rdi];
        var dataSize = ctx[CpuRegister.Rsi];
        var outInfoAddress = ctx[CpuRegister.Rdx];
        if (dataAddress == 0 || dataSize == 0 || outInfoAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> info = stackalloc byte[waveformInfoSize];
        info.Clear();

        Span<byte> header = stackalloc byte[Ngs2VagDecoder.VagHeaderSize];
        var isVag = ctx.Memory.TryRead(dataAddress, header) && Ngs2VagDecoder.IsVag(header);
        var channels = isVag && header[0x1E] == 2 ? 2u : 1u;
        var sampleRate = isVag
            ? BinaryPrimitives.ReadUInt32BigEndian(header[0x10..])
            : 48_000u;
        if (sampleRate == 0)
        {
            sampleRate = 48_000;
        }

        var declaredDataSize = isVag
            ? BinaryPrimitives.ReadUInt32BigEndian(header[0x0C..])
            : (uint)Math.Min(dataSize, uint.MaxValue);
        var availablePayload = isVag
            ? dataSize > Ngs2VagDecoder.VagHeaderSize
                ? dataSize - Ngs2VagDecoder.VagHeaderSize
                : 0UL
            : dataSize;
        var availableDataSize = (uint)Math.Min(availablePayload, uint.MaxValue);
        var payloadSize = declaredDataSize == 0 || declaredDataSize > availableDataSize
            ? availableDataSize
            : declaredDataSize;
        var frameCount = payloadSize / 16u;
        var samples = isVag ? (frameCount / channels) * 28u : payloadSize / (2u * channels);
        var dataOffset = isVag ? (uint)Ngs2VagDecoder.VagHeaderSize : 0u;

        // Ngs2WaveformFormat (24 bytes).
        BinaryPrimitives.WriteUInt32LittleEndian(
            info, isVag ? waveformTypeVag : Ngs2PcmDecoder.Signed16LittleEndian);
        BinaryPrimitives.WriteUInt32LittleEndian(info[4..], channels);
        BinaryPrimitives.WriteUInt32LittleEndian(info[8..], sampleRate);
        // Ngs2WaveformInfo scalar fields.
        BinaryPrimitives.WriteUInt32LittleEndian(info[24..], dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(info[28..], payloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info[40..], samples);
        BinaryPrimitives.WriteUInt32LittleEndian(info[44..], isVag ? 16u : 2u * channels);
        BinaryPrimitives.WriteUInt32LittleEndian(info[48..], isVag ? 28u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(info[52..], 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(info[56..], isVag ? 16u : 2u * channels);
        BinaryPrimitives.WriteUInt32LittleEndian(info[60..], isVag ? 28u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(info[68..], 1u);
        // First Ngs2WaveformBlock starts at +72 and is 40 bytes.
        BinaryPrimitives.WriteUInt64LittleEndian(info[72..], dataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(info[80..], payloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(info[96..], samples);

        return ctx.Memory.TryWrite(outInfoAddress, info)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        ExportName = "sceNgs2CalcWaveformBlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2CalcWaveformBlock(CpuContext ctx)
    {
        const int waveformBlockSize = 40;
        var formatAddress = ctx[CpuRegister.Rdi];
        var samplePosition = unchecked((uint)ctx[CpuRegister.Rsi]);
        var sampleCount = unchecked((uint)ctx[CpuRegister.Rdx]);
        var blockAddress = ctx[CpuRegister.Rcx];
        if (formatAddress == 0 || blockAddress == 0)
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> format = stackalloc byte[24];
        if (!ctx.Memory.TryRead(formatAddress, format))
        {
            return SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var waveformType = BinaryPrimitives.ReadUInt32LittleEndian(format);
        var channels = Math.Max(1u, BinaryPrimitives.ReadUInt32LittleEndian(format[4..]));
        var vag = waveformType == 0x80;
        var unitSamples = vag ? 28u : 1u;
        var unitBytes = vag ? 16u * channels : 2u * channels;
        var firstUnit = samplePosition / unitSamples;
        var units = (sampleCount + (samplePosition % unitSamples) + unitSamples - 1) / unitSamples;

        Span<byte> block = stackalloc byte[waveformBlockSize];
        block.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(block, (ulong)firstUnit * unitBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], (ulong)units * unitBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(block[20..], samplePosition % unitSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(block[24..], sampleCount);
        return ctx.Memory.TryWrite(blockAddress, block)
            ? SetReturn(ctx, 0)
            : SetReturn(ctx, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
