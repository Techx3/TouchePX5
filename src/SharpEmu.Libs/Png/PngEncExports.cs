// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Png;

public static class PngEncExports
{
    private const int InvalidAddress = unchecked((int)0x80690101);
    private const int InvalidSize = unchecked((int)0x80690102);
    private const int InvalidParameter = unchecked((int)0x80690103);
    private const int InvalidHandle = unchecked((int)0x80690104);
    private const int DataOverflow = unchecked((int)0x80690110);
    private const int Fatal = unchecked((int)0x80690120);

    private const uint CreateParameterSize = 0x10;
    private const uint EncodeParameterSize = 0x30;
    private const uint HandleMemorySize = 0x10;
    private const uint MaxImageWidth = 1_000_000;
    private const ulong MaxInputBytes = 512UL * 1024 * 1024;

    [SysAbiExport(
        Nid = "9030RnBDoh4",
        ExportName = "scePngEncQueryMemorySize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePngEnc")]
    public static int PngEncQueryMemorySize(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        var validation = ValidateCreateParameters(ctx, parameterAddress);
        return ctx.SetReturn(validation == 0 ? (int)HandleMemorySize : validation);
    }

    [SysAbiExport(
        Nid = "7aGTPfrqT9s",
        ExportName = "scePngEncCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePngEnc")]
    public static int PngEncCreate(CpuContext ctx)
    {
        var parameterAddress = ctx[CpuRegister.Rdi];
        var memoryAddress = ctx[CpuRegister.Rsi];
        var memorySize = unchecked((uint)ctx[CpuRegister.Rdx]);
        var handleAddress = ctx[CpuRegister.Rcx];

        var validation = ValidateCreateParameters(ctx, parameterAddress);
        if (validation != 0)
        {
            return ctx.SetReturn(validation);
        }

        if (memoryAddress == 0 || handleAddress == 0)
        {
            return ctx.SetReturn(InvalidAddress);
        }

        if (memorySize < HandleMemorySize)
        {
            return ctx.SetReturn(InvalidSize);
        }

        Span<byte> state = stackalloc byte[(int)HandleMemorySize];
        state.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(state, HandleMemorySize);
        if (!ctx.Memory.TryWrite(memoryAddress, state) ||
            !ctx.TryWriteUInt64(handleAddress, memoryAddress))
        {
            return ctx.SetReturn(InvalidAddress);
        }

        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "xgDjJKpcyHo",
        ExportName = "scePngEncEncode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePngEnc")]
    public static int PngEncEncode(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var parameterAddress = ctx[CpuRegister.Rsi];
        var outputInfoAddress = ctx[CpuRegister.Rdx];
        if (handle == 0 || !ctx.TryReadUInt32(handle, out var handleMarker) || handleMarker != HandleMemorySize)
        {
            return ctx.SetReturn(InvalidHandle);
        }

        if (parameterAddress == 0 || outputInfoAddress == 0)
        {
            return ctx.SetReturn(InvalidAddress);
        }

        Span<byte> parameterBytes = stackalloc byte[(int)EncodeParameterSize];
        if (!ctx.Memory.TryRead(parameterAddress, parameterBytes))
        {
            return ctx.SetReturn(InvalidAddress);
        }

        var imageAddress = BinaryPrimitives.ReadUInt64LittleEndian(parameterBytes[0x00..]);
        var pngAddress = BinaryPrimitives.ReadUInt64LittleEndian(parameterBytes[0x08..]);
        var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(parameterBytes[0x10..]);
        var pngCapacity = BinaryPrimitives.ReadUInt32LittleEndian(parameterBytes[0x14..]);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(parameterBytes[0x18..]);
        var height = BinaryPrimitives.ReadUInt32LittleEndian(parameterBytes[0x1C..]);
        var pitch = BinaryPrimitives.ReadUInt32LittleEndian(parameterBytes[0x20..]);
        var pixelFormat = BinaryPrimitives.ReadUInt16LittleEndian(parameterBytes[0x24..]);
        var colorSpace = BinaryPrimitives.ReadUInt16LittleEndian(parameterBytes[0x26..]);
        var bitDepth = BinaryPrimitives.ReadUInt16LittleEndian(parameterBytes[0x28..]);
        var compressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(parameterBytes[0x2E..]);

        if (imageAddress == 0 || pngAddress == 0)
        {
            return ctx.SetReturn(InvalidAddress);
        }

        if (width == 0 || width > MaxImageWidth || height == 0 ||
            pixelFormat > 1 || (colorSpace != 3 && colorSpace != 19) || bitDepth != 8)
        {
            return ctx.SetReturn(InvalidParameter);
        }

        ulong minimumPitch;
        ulong requiredInput;
        try
        {
            minimumPitch = checked((ulong)width * 4);
            requiredInput = checked((ulong)pitch * height);
        }
        catch (OverflowException)
        {
            return ctx.SetReturn(InvalidSize);
        }

        if (pitch < minimumPitch || requiredInput > imageSize || requiredInput > MaxInputBytes)
        {
            return ctx.SetReturn(InvalidSize);
        }

        try
        {
            var encoded = EncodePng(
                ctx,
                imageAddress,
                width,
                height,
                pitch,
                pixelFormat,
                colorSpace,
                compressionLevel);
            if (encoded is null)
            {
                return ctx.SetReturn(InvalidAddress);
            }

            if (!WriteOutputInfo(ctx, outputInfoAddress, (uint)encoded.Length, height))
            {
                return ctx.SetReturn(InvalidAddress);
            }

            if ((ulong)encoded.Length > pngCapacity)
            {
                return ctx.SetReturn(DataOverflow);
            }

            return ctx.Memory.TryWrite(pngAddress, encoded)
                ? ctx.SetReturn(0)
                : ctx.SetReturn(InvalidAddress);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OverflowException or OutOfMemoryException)
        {
            return ctx.SetReturn(Fatal);
        }
    }

    [SysAbiExport(
        Nid = "RUrWdwTWZy8",
        ExportName = "scePngEncDelete",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libScePngEnc")]
    public static int PngEncDelete(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (handle == 0 || !ctx.TryReadUInt32(handle, out var marker) || marker != HandleMemorySize)
        {
            return ctx.SetReturn(InvalidHandle);
        }

        Span<byte> state = stackalloc byte[(int)HandleMemorySize];
        state.Clear();
        return ctx.Memory.TryWrite(handle, state)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(InvalidHandle);
    }

    private static int ValidateCreateParameters(CpuContext ctx, ulong parameterAddress)
    {
        if (parameterAddress == 0)
        {
            return InvalidAddress;
        }

        Span<byte> parameters = stackalloc byte[(int)CreateParameterSize];
        if (!ctx.Memory.TryRead(parameterAddress, parameters))
        {
            return InvalidAddress;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(parameters);
        var maxWidth = BinaryPrimitives.ReadUInt32LittleEndian(parameters[8..]);
        if (size != CreateParameterSize)
        {
            return InvalidSize;
        }

        return maxWidth > MaxImageWidth ? InvalidParameter : 0;
    }

    private static byte[]? EncodePng(
        CpuContext ctx,
        ulong imageAddress,
        uint width,
        uint height,
        uint pitch,
        ushort pixelFormat,
        ushort colorSpace,
        ushort requestedCompressionLevel)
    {
        var channelCount = colorSpace == 19 ? 4 : 3;
        var sourceRow = new byte[checked((int)pitch)];
        var filteredRow = new byte[checked((int)((ulong)width * (uint)channelCount + 1))];

        using var compressedStream = new MemoryStream();
        using (var zlib = new ZLibStream(compressedStream, MapCompressionLevel(requestedCompressionLevel), leaveOpen: true))
        {
            for (uint y = 0; y < height; y++)
            {
                if (!ctx.Memory.TryRead(imageAddress + ((ulong)y * pitch), sourceRow))
                {
                    return null;
                }

                filteredRow[0] = 0;
                var destinationOffset = 1;
                for (uint x = 0; x < width; x++)
                {
                    var sourceOffset = checked((int)(x * 4));
                    if (pixelFormat == 0)
                    {
                        filteredRow[destinationOffset++] = sourceRow[sourceOffset];
                        filteredRow[destinationOffset++] = sourceRow[sourceOffset + 1];
                        filteredRow[destinationOffset++] = sourceRow[sourceOffset + 2];
                    }
                    else
                    {
                        filteredRow[destinationOffset++] = sourceRow[sourceOffset + 2];
                        filteredRow[destinationOffset++] = sourceRow[sourceOffset + 1];
                        filteredRow[destinationOffset++] = sourceRow[sourceOffset];
                    }

                    if (channelCount == 4)
                    {
                        filteredRow[destinationOffset++] = sourceRow[sourceOffset + 3];
                    }
                }

                zlib.Write(filteredRow);
            }
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = colorSpace == 19 ? (byte)6 : (byte)2;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressedStream.ToArray());
        WriteChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static CompressionLevel MapCompressionLevel(ushort level) => level switch
    {
        0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        _ => CompressionLevel.Optimal,
    };

    private static bool WriteOutputInfo(CpuContext ctx, ulong address, uint size, uint processedHeight)
    {
        Span<byte> output = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(output, size);
        BinaryPrimitives.WriteUInt32LittleEndian(output[4..], processedHeight);
        return ctx.Memory.TryWrite(address, output);
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, checked((uint)data.Length));
        stream.Write(value);
        stream.Write(type);
        stream.Write(data);

        var crc = ComputeCrc32(type, data);
        BinaryPrimitives.WriteUInt32BigEndian(value, crc);
        stream.Write(value);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        UpdateCrc32(ref crc, type);
        UpdateCrc32(ref crc, data);
        return ~crc;
    }

    private static void UpdateCrc32(ref uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
            }
        }
    }
}
