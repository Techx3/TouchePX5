// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Png;
using Xunit;

namespace SharpEmu.Libs.Tests.Png;

public sealed class PngEncExportsTests
{
    private const ulong Base = 0x1_0000_0000;

    [Fact]
    public void QueryMemorySize_ReturnsSmallPositiveWorkspaceSize()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteCreateParameters(memory, Base + 0x100, maxWidth: 1920);
        context[CpuRegister.Rdi] = Base + 0x100;

        var result = PngEncExports.PngEncQueryMemorySize(context);

        Assert.Equal(0x10, result);
        Assert.Equal(0x10UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void CastlevaniaPngEncoderNids_AreRegistered()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        AssertExport(manager, "9030RnBDoh4", "scePngEncQueryMemorySize");
        AssertExport(manager, "7aGTPfrqT9s", "scePngEncCreate");
        AssertExport(manager, "xgDjJKpcyHo", "scePngEncEncode");
        AssertExport(manager, "RUrWdwTWZy8", "scePngEncDelete");
    }

    [Fact]
    public void Encode_WritesValidRgbaPngAndOutputInfo()
    {
        var memory = new FakeCpuMemory(Base, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        var createParameters = Base + 0x100;
        var handleMemory = Base + 0x200;
        var handleOut = Base + 0x300;
        var encodeParameters = Base + 0x400;
        var image = Base + 0x500;
        var png = Base + 0x1000;
        var outputInfo = Base + 0x3000;

        WriteCreateParameters(memory, createParameters, maxWidth: 2);
        context[CpuRegister.Rdi] = createParameters;
        context[CpuRegister.Rsi] = handleMemory;
        context[CpuRegister.Rdx] = 0x10;
        context[CpuRegister.Rcx] = handleOut;
        Assert.Equal(0, PngEncExports.PngEncCreate(context));
        Assert.True(context.TryReadUInt64(handleOut, out var handle));
        Assert.Equal(handleMemory, handle);

        Assert.True(memory.TryWrite(image, [255, 0, 0, 255, 0, 255, 0, 128]));
        Span<byte> parameters = stackalloc byte[0x30];
        BinaryPrimitives.WriteUInt64LittleEndian(parameters, image);
        BinaryPrimitives.WriteUInt64LittleEndian(parameters[0x08..], png);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[0x10..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[0x14..], 0x1000);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[0x18..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[0x1C..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[0x20..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(parameters[0x24..], 0); // RGBA byte order
        BinaryPrimitives.WriteUInt16LittleEndian(parameters[0x26..], 19); // RGBA PNG
        BinaryPrimitives.WriteUInt16LittleEndian(parameters[0x28..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(parameters[0x2E..], 6);
        Assert.True(memory.TryWrite(encodeParameters, parameters));

        context[CpuRegister.Rdi] = handle;
        context[CpuRegister.Rsi] = encodeParameters;
        context[CpuRegister.Rdx] = outputInfo;
        Assert.Equal(0, PngEncExports.PngEncEncode(context));

        Span<byte> signature = stackalloc byte[8];
        Assert.True(memory.TryRead(png, signature));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature.ToArray());
        Assert.True(context.TryReadUInt32(outputInfo, out var encodedSize));
        Assert.True(encodedSize > 32);
        Assert.True(context.TryReadUInt32(outputInfo + 4, out var processedHeight));
        Assert.Equal(1u, processedHeight);
    }

    private static void WriteCreateParameters(FakeCpuMemory memory, ulong address, uint maxWidth)
    {
        Span<byte> parameters = stackalloc byte[0x10];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters, 0x10);
        BinaryPrimitives.WriteUInt32LittleEndian(parameters[8..], maxWidth);
        Assert.True(memory.TryWrite(address, parameters));
    }

    private static void AssertExport(ModuleManager manager, string nid, string name)
    {
        Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
        Assert.Equal(name, export.Name);
        Assert.Equal("libScePngEnc", export.LibraryName);
    }
}
