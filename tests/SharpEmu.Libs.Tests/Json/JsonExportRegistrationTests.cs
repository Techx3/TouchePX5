// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Json;
using Xunit;

namespace SharpEmu.Libs.Tests.Json;

// These NIDs came back "unresolved" in the Quake (PPSA01880) import log right before its
// access violation. This asserts they now resolve to the Json handlers and dispatch cleanly,
// which is the plumbing the direct-call tests cannot cover.
[Collection("JsonObjectHeap")]
public sealed class JsonExportRegistrationTests
{
    private static readonly (string Nid, string Name)[] ExpectedExports =
    {
        ("qBMjqyBn3OM", "_ZN3sce4Json5ValueC1Ev"),
        ("5yHuiWXo2gg", "_ZN3sce4Json5Value3setEb"),
        ("QxVVYhP-mvg", "_ZN3sce4Json5Value3setEl"),
        ("SIe1ZmW7e7s", "_ZN3sce4Json5Value3setEm"),
        ("BSmWDIkV4w4", "_ZN3sce4Json5Value3setEd"),
        ("IKQimvG9Wqs", "_ZN3sce4Json5Value3setENS0_9ValueTypeE"),
        ("6l3Bv2gysNc", "_ZN3sce4Json5Value3setERKNS0_6StringE"),
        ("wLsJlmgEIaI", "_ZN3sce4Json5Value10referValueERKNS0_6StringE"),
        ("9KUZFjI1IxA", "_ZN3sce4Json6StringC1EPKc"),
        ("cG1VE2HMl6c", "_ZN3sce4Json6StringD1Ev"),
        ("+drDFyAS6u4", "_ZN3sce4Json11Initializer27setGlobalNullAccessCallbackEPFRKNS0_5ValueENS0_9ValueTypeEPS3_PvES7_"),
    };

    private static ModuleManager CreateRegisteredManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    [Fact]
    public void QuakeUnresolvedJsonNids_ResolveToJsonExports()
    {
        var manager = CreateRegisteredManager();

        foreach (var (nid, name) in ExpectedExports)
        {
            Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
            Assert.Equal(name, export.Name);
            Assert.Equal("libSceJson", export.LibraryName);
        }
    }

    [Fact]
    public void SetGlobalNullAccessCallback_StoresHookAndReturnsOk()
    {
        JsonObjectHeap.ResetForTests();
        var manager = CreateRegisteredManager();
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1_0000_0000; // Initializer instance
        ctx[CpuRegister.Rsi] = 0x8_0012_3456; // guest callback
        ctx[CpuRegister.Rdx] = 0x1_0000_0800; // user context

        Assert.True(manager.TryDispatch("+drDFyAS6u4", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(0x8_0012_3456UL, JsonObjectHeap.GlobalNullAccessCallback);
        Assert.Equal(0x1_0000_0800UL, JsonObjectHeap.GlobalNullAccessCallbackContext);
    }

    [Fact]
    public void DispatchValueConstructor_RunsHandlerAndReturnsThis()
    {
        JsonObjectHeap.ResetForTests();
        var manager = CreateRegisteredManager();
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x1_0000_0000;

        Assert.True(manager.TryDispatch("qBMjqyBn3OM", ctx, out var result));
        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0x1_0000_0000UL, ctx[CpuRegister.Rax]);
        Assert.Equal(JsonValueKind.Null, JsonObjectHeap.Values[0x1_0000_0000].Kind);
    }

    [Fact]
    public void ReferValue_UsesStringConstructedByCompleteObjectExport()
    {
        JsonObjectHeap.ResetForTests();
        const ulong baseAddress = 0x1_0000_0000;
        const ulong valueAddress = baseAddress + 0x100;
        const ulong keyStringAddress = baseAddress + 0x200;
        const ulong keyTextAddress = baseAddress + 0x300;
        const ulong jsonAddress = baseAddress + 0x400;
        var memory = new FakeCpuMemory(baseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var json = System.Text.Encoding.UTF8.GetBytes("{\"answer\":42}");
        Assert.True(memory.TryWrite(jsonAddress, json));

        ctx[CpuRegister.Rdi] = valueAddress;
        ctx[CpuRegister.Rsi] = jsonAddress;
        ctx[CpuRegister.Rdx] = (ulong)json.Length;
        Assert.Equal(0, JsonExports.ParserParseBuffer(ctx));

        memory.WriteCString(keyTextAddress, "answer");
        ctx[CpuRegister.Rdi] = keyStringAddress;
        ctx[CpuRegister.Rsi] = keyTextAddress;
        Assert.Equal(0, JsonValueExports.StringCStringConstructor(ctx));

        ctx[CpuRegister.Rdi] = valueAddress;
        ctx[CpuRegister.Rsi] = keyStringAddress;
        Assert.Equal(0, JsonExports.ValueReferValue(ctx));
        var childAddress = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, childAddress);

        ctx[CpuRegister.Rdi] = childAddress;
        Assert.Equal(0, JsonExports.ValueGetType(ctx));
        Assert.Equal(2UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = childAddress;
        Assert.Equal(0, JsonExports.ValueGetInteger(ctx));
        Assert.True(ctx.TryReadUInt64(ctx[CpuRegister.Rax], out var value));
        Assert.Equal(42UL, value);
    }
}
