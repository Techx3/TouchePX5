// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using System.Buffers.Binary;
using System.Diagnostics;

namespace SharpEmu.Libs.Pad;

public static class PadExports
{
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);
    private const int OrbisPadErrorNotInitialized = unchecked((int)0x80920005);
    private const int OrbisPadErrorDeviceNotConnected = unchecked((int)0x80920007);
    private const int OrbisPadErrorDeviceNoHandle = unchecked((int)0x80920008);
    // Keep the pad session on the same retail user id returned by
    // libSceUserService.  A mismatched emulator-local id makes games pass a
    // valid 0x10000000 user to scePadOpen and receive DEVICE_NOT_CONNECTED,
    // leaving every later keyboard/gamepad read on an invalid handle.
    private const int PrimaryUserId = 0x10000000;
    private const int StandardPortType = 0;
    private const int PrimaryPadHandle = 1;
    private const int ControllerInformationSize = 0x1C;
    private const int PadDataSize = 0x78;
    private const int PadDeviceClassDataSize = 0x18;

    // Real firmware hands out small non-negative handles; 0 is valid. Some titles
    // (Monster Truck Championship) read pad state with handle 0, and rejecting it
    // leaves their controller/FFB init path polling a never-valid state forever.
    private static bool IsPrimaryPadHandle(int handle) => handle is 0 or PrimaryPadHandle;
    private static readonly long InputSampleIntervalTicks = Math.Max(1, Stopwatch.Frequency / 1000);

    // Sampling is process-wide rather than per-thread: every GetAsyncKeyState
    // enters the system-wide USER critical section, so a per-thread cache
    // multiplies that contention by the number of guest threads polling the pad.
    private sealed class PadSample
    {
        public PadSample(PadState state, long timestamp)
        {
            State = state;
            Timestamp = timestamp;
        }

        public PadState State { get; }

        public long Timestamp { get; }
    }

    private static readonly object InputSampleLock = new();
    private static PadSample? _cachedInputSample;

    private static bool _initialized;
    private static int _controlsAnnouncementLogged;

    [SysAbiExport(
        Nid = "hv1luiJrqQM",
        ExportName = "scePadInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadInit(CpuContext ctx)
    {
        _initialized = true;
        HostPlatform.Current.Input.EnsureStarted();
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xk0AcarP3V4",
        ExportName = "scePadOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpen(CpuContext ctx) => PadOpenCore(ctx, extended: false);

    [SysAbiExport(
        Nid = "WFIiSfXGUq8",
        ExportName = "scePadOpenExt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadOpenExt(CpuContext ctx) => PadOpenCore(ctx, extended: true);

    // scePadGetHandle(userId, type, index): returns the handle of an already-open
    // pad without opening a new one. Dead Cells calls it every frame to poll
    // input; leaving it unresolved returned a garbage handle so the input path
    // (and the game loop that drives it) misbehaved. Same validation as
    // scePadOpen — the one primary pad — returning its handle or a not-connected
    // error, never opening or logging.
    [SysAbiExport(
        Nid = "u1GRHp+oWoY",
        ExportName = "scePadGetHandle",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetHandle(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!_initialized)
        {
            return ctx.SetReturn(OrbisPadErrorNotInitialized);
        }

        if (userId != PrimaryUserId || type is not (0 or 1 or 2) || index != 0)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNotConnected);
        }

        return ctx.SetReturn(PrimaryPadHandle);
    }

    // Gen5 titles use the regular scePadOpen entry point with port types 1/2 as well.
    // Keep Gen4 scePadOpen strict while scePadOpenExt accepts all known port types.
    private static int PadOpenCore(CpuContext ctx, bool extended)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        var parameterAddress = ctx[CpuRegister.Rcx];
        if (!_initialized)
        {
            return ctx.SetReturn(OrbisPadErrorNotInitialized);
        }

        if (userId == -1)
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNoHandle);
        }

        var typeAccepted = IsPortTypeAccepted(ctx.TargetGeneration, extended, type);
        if (userId != PrimaryUserId || !typeAccepted || index != 0 || (!extended && parameterAddress != 0))
        {
            return ctx.SetReturn(OrbisPadErrorDeviceNotConnected);
        }

        var input = HostPlatform.Current.Input;
        input.EnsureStarted();
        if (Interlocked.Exchange(ref _controlsAnnouncementLogged, 1) == 0)
        {
            Console.Error.WriteLine(input.DescribeConnectedGamepad() is { } gamepadName
                ? $"[LOADER][INFO] Controls: {gamepadName} connected (keyboard fallback also active)."
                : "[LOADER][INFO] Keyboard controls: Arrow keys = D-pad, WASD = left stick, IJKL = right stick, Z/Enter = Cross, X/Esc = Circle, C = Square, V = Triangle, Q = L1, E = R1, R = L2, F = R2, Tab/Backspace = Options. A DualSense or Xbox controller will be used automatically when plugged in.");
        }

        return ctx.SetReturn(PrimaryPadHandle);
    }

    internal static bool IsPortTypeAccepted(Generation generation, bool extended, int type) =>
        type is 0 or 1 or 2 &&
        (extended || generation == Generation.Gen5 || type == StandardPortType);

    [SysAbiExport(
        Nid = "6ncge5+l5Qs",
        ExportName = "scePadClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadClose(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsPrimaryPadHandle(handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "clVvL4ZDntw",
        ExportName = "scePadSetMotionSensorState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetMotionSensorState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsPrimaryPadHandle(handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    [SysAbiExport(
        Nid = "vDLMoJLde8I",
        ExportName = "scePadSetTiltCorrectionState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetTiltCorrectionState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        return IsPrimaryPadHandle(handle)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(OrbisPadErrorInvalidHandle);
    }

    // Captured callers reserve eight bytes for this state. Writing a larger
    // guessed structure would overwrite the adjacent stack cookie.
    private const int TriggerEffectStateSize = 8;

    [SysAbiExport(
        Nid = "znaWI0gpuo8",
        ExportName = "scePadGetTriggerEffectState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetTriggerEffectState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var stateAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (stateAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> state = stackalloc byte[TriggerEffectStateSize];
        state.Clear();
        return ctx.Memory.TryWrite(stateAddress, state)
            ? ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "gjP9-KQzoUk",
        ExportName = "scePadGetControllerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetControllerInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> information = stackalloc byte[ControllerInformationSize];
        BinaryPrimitives.WriteSingleLittleEndian(information[0x00..], 44.86f);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x04..], 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x06..], 943);
        information[0x08] = 30;
        information[0x09] = 30;
        information[0x0A] = StandardPortType;
        information[0x0B] = 1;
        information[0x0C] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(information[0x10..], 0);

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "hGbf2QTBmqc",
        ExportName = "scePadGetExtControllerInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadGetExtControllerInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // Base ScePadControllerInformation + device-class/connection fields: report a connected
        // DualSense so the guest's open -> get-ext-info -> close probe loop resolves.
        Span<byte> information = stackalloc byte[0x40];
        information.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(information[0x00..], 44.86f);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x04..], 1920);
        BinaryPrimitives.WriteUInt16LittleEndian(information[0x06..], 943);
        information[0x08] = 30;
        information[0x09] = 30;
        information[0x0A] = StandardPortType;
        information[0x0B] = 1;   // connected count
        information[0x0C] = 1;   // connected
        BinaryPrimitives.WriteInt32LittleEndian(information[0x10..], 0);
        information[0x1C] = 0;   // deviceClass: 0 = standard controller / DualSense
        information[0x1D] = 1;   // connected (ext)
        information[0x1E] = 0;   // connectionType: local

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "AcslpN1jHR8",
        ExportName = "scePadDeviceClassGetExtendedInformation",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadDeviceClassGetExtendedInformation(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var informationAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (informationAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadDeviceClassExtendedInformation: deviceClass 0 = standard pad
        // (DualSense). We emulate no special peripheral (guitar/drums/wheel), so
        // the class-data union stays zeroed — the guest treats it as a plain
        // controller with no extended capabilities.
        Span<byte> information = stackalloc byte[0x20];
        information.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(information[0x00..], 0);

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "IHPqcbc0zCA",
        ExportName = "scePadDeviceClassParseData",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadDeviceClassParseData(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var classDataAddress = ctx[CpuRegister.Rdx];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0 || classDataAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // The emulated controller is the standard class, not a wheel, guitar,
        // drum kit or flight stick. The class-specific payload is therefore
        // present but explicitly invalid and fully zero-initialised.
        Span<byte> classData = stackalloc byte[PadDeviceClassDataSize];
        classData.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(classData[0x00..], 0);
        classData[0x04] = 0;

        return ctx.Memory.TryWrite(classDataAddress, classData)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "YndgXqQVV7c",
        ExportName = "scePadReadState",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadReadState(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "q1cHNfGycLI",
        ExportName = "scePadRead",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadRead(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var dataAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (dataAddress == 0 || count < 1 || count > 64)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return WriteNeutralPadData(ctx, dataAddress)
            ? ctx.SetReturn(1)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
    Nid = "W2G-yoyMF5U",
    ExportName = "scePadSetVibrationMode",
    Target = Generation.Gen4 | Generation.Gen5,
    LibraryName = "libScePad")]
    public static int PadSetVibrationMode(CpuContext ctx)
    {
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "2JgFB2n9oUM",
        ExportName = "scePadSetTriggerEffect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetTriggerEffect(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> parameter = stackalloc byte[120];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        var triggerMask = parameter[0];
        HostPlatform.Current.Input.SetTriggerRumble(
            (triggerMask & 0x01) != 0 ? DecodeTriggerVibration(parameter[8..64]) : null,
            (triggerMask & 0x02) != 0 ? DecodeTriggerVibration(parameter[64..120]) : null);
        return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static byte DecodeTriggerVibration(ReadOnlySpan<byte> command)
    {
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(command);
        var amplitude = mode switch
        {
            3 when command[10] != 0 => command[9],
            6 when command[8] != 0 => command[9..19].ToArray().Max(),
            _ => (byte)0,
        };
        return (byte)(Math.Min(amplitude, (byte)8) * 255 / 8);
    }

    [SysAbiExport(
        Nid = "yFVnOdGxvZY",
        ExportName = "scePadSetVibration",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetVibration(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadVibrationParam: { uint8_t largeMotor; uint8_t smallMotor; }
        Span<byte> parameter = stackalloc byte[2];
        if (!ctx.Memory.TryRead(parameterAddress, parameter))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        HostPlatform.Current.Input.SetRumble(parameter[0], parameter[1]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "RR4novUEENY",
        ExportName = "scePadSetLightBar",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadSetLightBar(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        if (parameterAddress == 0)
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        // ScePadColor: { uint8_t r; uint8_t g; uint8_t b; uint8_t reserved; }
        Span<byte> color = stackalloc byte[4];
        if (!ctx.Memory.TryRead(parameterAddress, color))
        {
            return ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        HostPlatform.Current.Input.SetLightbar(color[0], color[1], color[2]);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "DscD1i9HX1w",
        ExportName = "scePadResetLightBar",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePad")]
    public static int PadResetLightBar(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!IsPrimaryPadHandle(handle))
        {
            return ctx.SetReturn(OrbisPadErrorInvalidHandle);
        }

        HostPlatform.Current.Input.ResetLightbar();
        return ctx.SetReturn(0);
    }

    private static bool WriteNeutralPadData(CpuContext ctx, ulong dataAddress)
    {
        Span<byte> data = stackalloc byte[PadDataSize];
        data.Clear();
        var input = ReadHostInputState();
        var buttons = input.Buttons;
        var leftX = input.LeftX;
        var leftY = input.LeftY;
        var rightX = input.RightX;
        var rightY = input.RightY;
        var l2 = input.L2;
        var r2 = input.R2;

        BinaryPrimitives.WriteUInt32LittleEndian(data[0x00..], buttons);
        data[0x04] = leftX;
        data[0x05] = leftY;
        data[0x06] = rightX;
        data[0x07] = rightY;
        data[0x08] = l2;
        data[0x09] = r2;
        BinaryPrimitives.WriteSingleLittleEndian(data[0x18..], 1.0f);
        data[0x4C] = 1;
        var timestampTicks = Stopwatch.GetTimestamp();
        var timestampMicroseconds =
            ((ulong)(timestampTicks / Stopwatch.Frequency) * 1_000_000UL) +
            ((ulong)(timestampTicks % Stopwatch.Frequency) * 1_000_000UL / (ulong)Stopwatch.Frequency);
        BinaryPrimitives.WriteUInt64LittleEndian(
            data[0x50..],
            timestampMicroseconds);
        data[0x68] = 1;

        return ctx.Memory.TryWrite(dataAddress, data);
    }

    private static PadState ReadHostInputState()
    {
        var sample = Volatile.Read(ref _cachedInputSample);
        if (sample is not null &&
            Stopwatch.GetTimestamp() - sample.Timestamp < InputSampleIntervalTicks)
        {
            return sample.State;
        }

        lock (InputSampleLock)
        {
            // Another thread may have refreshed while this one waited.
            sample = _cachedInputSample;
            if (sample is not null &&
                Stopwatch.GetTimestamp() - sample.Timestamp < InputSampleIntervalTicks)
            {
                return sample.State;
            }

            return SampleHostInputState();
        }
    }

    private static PadState SampleHostInputState()
    {
        var now = Stopwatch.GetTimestamp();
        var input = HostPlatform.Current.Input;
        var acceptsKeyboardInput = input.IsHostWindowFocused();
        var buttons = acceptsKeyboardInput ? ReadKeyboardButtons(input) : 0;
        var leftX = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x41), input.IsKeyDown(0x44)) : (byte)128;
        var leftY = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x57), input.IsKeyDown(0x53)) : (byte)128;
        var rightX = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x4A), input.IsKeyDown(0x4C)) : (byte)128;
        var rightY = acceptsKeyboardInput ? ReadAnalogStick(input.IsKeyDown(0x49), input.IsKeyDown(0x4B)) : (byte)128;
        var l2 = acceptsKeyboardInput && input.IsKeyDown(0x52) ? (byte)255 : (byte)0;
        var r2 = acceptsKeyboardInput && input.IsKeyDown(0x46) ? (byte)255 : (byte)0;

        Span<HostGamepadState> gamepads = stackalloc HostGamepadState[2];
        var gamepadCount = input.GetGamepadStates(gamepads);
        for (var index = 0; index < gamepadCount; index++)
        {
            var pad = gamepads[index];
            buttons |= ToOrbisButtons(pad.Buttons);
            // The controller stick wins whenever it is deflected past a
            // small deadzone; otherwise any keyboard value stays.
            leftX = MergeAxis(pad.LeftX, leftX);
            leftY = MergeAxis(pad.LeftY, leftY);
            rightX = MergeAxis(pad.RightX, rightX);
            rightY = MergeAxis(pad.RightY, rightY);
            l2 = Math.Max(l2, pad.LeftTrigger);
            r2 = Math.Max(r2, pad.RightTrigger);
        }

        if (IsAutoCrossActive())
        {
            buttons |= 0x4000;
        }

        if (IsAutoDownActive())
        {
            buttons |= OrbisPadButton.Down;
        }

        var state = new PadState(
            Connected: true,
            Buttons: buttons,
            LeftX: leftX,
            LeftY: leftY,
            RightX: rightX,
            RightY: rightY,
            L2: l2,
            R2: r2);
        Volatile.Write(ref _cachedInputSample, new PadSample(state, now));
        return state;
    }

    private static readonly long PadStartTimestamp = Stopwatch.GetTimestamp();
    private static readonly double[] AutoCrossTimes = ParseAutoCrossTimes();
    private static readonly double[] AutoDownTimes = ParseAutoButtonTimes("SHARPEMU_AUTO_DOWN");

    private static double[] ParseAutoCrossTimes()
    {
        // SHARPEMU_AUTO_CROSS="40,52,64": presses Cross for 0.4s at each
        // second offset from process start. Debug aid for unattended runs.
        return ParseAutoButtonTimes("SHARPEMU_AUTO_CROSS");
    }

    private static double[] ParseAutoButtonTimes(string environmentVariable)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var values = new List<double>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(token, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                values.Add(value);
            }
        }

        return values.ToArray();
    }

    private static bool IsAutoCrossActive()
    {
        return IsAutoButtonActive(AutoCrossTimes);
    }

    private static bool IsAutoDownActive()
    {
        return IsAutoButtonActive(AutoDownTimes);
    }

    private static bool IsAutoButtonActive(double[] times)
    {
        if (times.Length == 0)
        {
            return false;
        }

        var elapsed = (Stopwatch.GetTimestamp() - PadStartTimestamp) / (double)Stopwatch.Frequency;
        foreach (var time in times)
        {
            if (elapsed >= time && elapsed < time + 0.4)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps the host seam's neutral button flags onto SCE_PAD_BUTTON bits.</summary>
    private static uint ToOrbisButtons(HostGamepadButtons buttons)
    {
        uint result = 0;
        if ((buttons & HostGamepadButtons.Up) != 0) result |= OrbisPadButton.Up;
        if ((buttons & HostGamepadButtons.Down) != 0) result |= OrbisPadButton.Down;
        if ((buttons & HostGamepadButtons.Left) != 0) result |= OrbisPadButton.Left;
        if ((buttons & HostGamepadButtons.Right) != 0) result |= OrbisPadButton.Right;
        if ((buttons & HostGamepadButtons.Cross) != 0) result |= OrbisPadButton.Cross;
        if ((buttons & HostGamepadButtons.Circle) != 0) result |= OrbisPadButton.Circle;
        if ((buttons & HostGamepadButtons.Square) != 0) result |= OrbisPadButton.Square;
        if ((buttons & HostGamepadButtons.Triangle) != 0) result |= OrbisPadButton.Triangle;
        if ((buttons & HostGamepadButtons.L1) != 0) result |= OrbisPadButton.L1;
        if ((buttons & HostGamepadButtons.R1) != 0) result |= OrbisPadButton.R1;
        if ((buttons & HostGamepadButtons.L2) != 0) result |= OrbisPadButton.L2;
        if ((buttons & HostGamepadButtons.R2) != 0) result |= OrbisPadButton.R2;
        if ((buttons & HostGamepadButtons.L3) != 0) result |= OrbisPadButton.L3;
        if ((buttons & HostGamepadButtons.R3) != 0) result |= OrbisPadButton.R3;
        if ((buttons & HostGamepadButtons.Options) != 0) result |= OrbisPadButton.Options;
        if ((buttons & HostGamepadButtons.TouchPad) != 0) result |= OrbisPadButton.TouchPad;
        return result;
    }

    private static uint ReadKeyboardButtons(IHostInput input)
    {
        uint buttons = 0;
        // D-pad
        if (input.IsKeyDown(0x25)) buttons |= OrbisPadButton.Left;
        if (input.IsKeyDown(0x27)) buttons |= OrbisPadButton.Right;
        if (input.IsKeyDown(0x26)) buttons |= OrbisPadButton.Up;
        if (input.IsKeyDown(0x28)) buttons |= OrbisPadButton.Down;
        // Face buttons
        if (input.IsKeyDown(0x5A) || input.IsKeyDown(0x0D)) buttons |= OrbisPadButton.Cross;    // Z / Enter
        if (input.IsKeyDown(0x58) || input.IsKeyDown(0x1B)) buttons |= OrbisPadButton.Circle;   // X / Escape
        if (input.IsKeyDown(0x43)) buttons |= OrbisPadButton.Square;                            // C
        if (input.IsKeyDown(0x56)) buttons |= OrbisPadButton.Triangle;                          // V
        // Shoulder buttons
        if (input.IsKeyDown(0x51)) buttons |= OrbisPadButton.L1;                                // Q
        if (input.IsKeyDown(0x45)) buttons |= OrbisPadButton.R1;                                // E
        if (input.IsKeyDown(0x52)) buttons |= OrbisPadButton.L2;                                // R (digital)
        if (input.IsKeyDown(0x46)) buttons |= OrbisPadButton.R2;                                // F (digital)
        // Options (Start)
        if (input.IsKeyDown(0x09) || input.IsKeyDown(0x08)) buttons |= OrbisPadButton.Options;  // Tab / Backspace
        return buttons;
    }

    private static byte ReadAnalogStick(bool negative, bool positive)
    {
        if (negative && !positive) return 0;
        if (positive && !negative) return 255;
        return 128;
    }

    private static byte MergeAxis(byte controller, byte keyboard)
    {
        const int Deadzone = 10;
        return Math.Abs(controller - 128) > Deadzone ? controller : keyboard;
    }
}
