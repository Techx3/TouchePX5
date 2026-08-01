// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Ime;

public static class ImeExports
{
    private const int ImeKeyboardInfoSize = 0x24;
    private const int ImeErrorInvalidAddress = unchecked((int)0x80BC0001);

    // Quake (KEX) calls this from its main loop and from the audio bring-up path with
    // an event-handler pointer. No IME session ever exists here, so report success
    // without invoking the handler ("no pending IME events"). This NID was previously
    // misbound as an sceNgs2VoiceControl alias, which fed the game NGS2 errors.
    [SysAbiExport(
        Nid = "-4GCfYdNF1s",
        ExportName = "sceImeUpdate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeUpdate(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "eaFXjfJv3xs",
        ExportName = "sceImeKeyboardOpen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardOpen(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "dKadqZFgKKQ",
        ExportName = "sceImeKeyboardGetResourceId",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetResourceId(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "VkqLPArfFdc",
        ExportName = "sceImeKeyboardGetInfo",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceIme")]
    public static int ImeKeyboardGetInfo(CpuContext ctx)
    {
        var informationAddress = ctx[CpuRegister.Rsi];
        if (informationAddress == 0)
        {
            return ctx.SetReturn(ImeErrorInvalidAddress);
        }

        // No physical keyboard is exposed as a console IME device. Return a
        // complete, deterministic disconnected record so callers do not keep
        // uninitialised stack data while probing the optional keyboard path.
        Span<byte> information = stackalloc byte[ImeKeyboardInfoSize];
        information.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(information[0x00..], -1); // invalid user
        BinaryPrimitives.WriteInt32LittleEndian(information[0x04..], 0);  // keyboard
        BinaryPrimitives.WriteInt32LittleEndian(information[0x08..], 4);  // English (US)
        BinaryPrimitives.WriteUInt32LittleEndian(information[0x0C..], 1); // repeat delay
        BinaryPrimitives.WriteUInt32LittleEndian(information[0x10..], 1); // repeat rate
        BinaryPrimitives.WriteInt32LittleEndian(information[0x14..], 0);  // disconnected

        return ctx.Memory.TryWrite(informationAddress, information)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(ImeErrorInvalidAddress);
    }
}
