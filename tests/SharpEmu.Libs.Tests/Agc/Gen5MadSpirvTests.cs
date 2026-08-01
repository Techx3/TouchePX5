// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5MadSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const ushort OpExtInst = 12;
    private const ushort OpDecorate = 71;
    private const ushort OpFAdd = 129;
    private const ushort OpFMul = 133;
    private const uint GlslFma = 50;
    private const uint NoContraction = 42;

    [Theory]
    [InlineData(0x3E080416u, 0u)] // V_MAC_F32 v4, s22, v2
    [InlineData(0xD5410003u, 0x041C3D02u)] // V_MAD_F32 v3, v2, s30, v7
    public void NonFusedMadInstructions_PreserveIntermediateRounding(
        uint firstWord,
        uint secondWord)
    {
        var words = secondWord == 0
            ? new[] { firstWord }
            : new[] { firstWord, secondWord };
        var spirv = Compile(words);
        var instructions = EnumerateInstructions(spirv).ToArray();

        var arithmeticIds = instructions
            .Where(instruction => instruction.Op is OpFMul or OpFAdd)
            .Select(instruction => ReadWord(spirv, instruction.Offset + 8))
            .ToArray();
        var noContractionIds = instructions
            .Where(instruction =>
                instruction.Op == OpDecorate &&
                instruction.WordCount >= 3 &&
                ReadWord(spirv, instruction.Offset + 8) == NoContraction)
            .Select(instruction => ReadWord(spirv, instruction.Offset + 4))
            .ToHashSet();

        Assert.Equal(2, arithmeticIds.Length);
        Assert.All(arithmeticIds, id => Assert.Contains(id, noContractionIds));
        Assert.DoesNotContain(
            instructions,
            instruction =>
                instruction.Op == OpExtInst &&
                instruction.WordCount >= 5 &&
                ReadWord(spirv, instruction.Offset + 16) == GlslFma);
    }

    private static byte[] Compile(uint[] programWords)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
        };

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out error),
            error);
        return shader.Spirv;
    }

    private static IEnumerable<(ushort Op, int WordCount, int Offset)>
        EnumerateInstructions(byte[] spirv)
    {
        for (var offset = 5 * sizeof(uint);
             offset + sizeof(uint) <= spirv.Length;)
        {
            var word = ReadWord(spirv, offset);
            var wordCount = (int)(word >> 16);
            if (wordCount <= 0)
            {
                yield break;
            }

            yield return ((ushort)word, wordCount, offset);
            offset += wordCount * sizeof(uint);
        }
    }

    private static uint ReadWord(byte[] spirv, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            spirv.AsSpan(offset, sizeof(uint)));
}
