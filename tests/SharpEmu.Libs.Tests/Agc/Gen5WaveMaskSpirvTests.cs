// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Regression tests for how a VCC/EXEC wave mask consumed as a per-lane predicate
// is lowered to SPIR-V. A wave mask must be tested at the current lane's bit
// (mask & lane_bit) — exactly as the hardware evaluates the VCndmask condition or
// a VCC/EXEC branch — not with a whole-word "the 64-bit value is non-zero" test.
//
// The two agree for comparison results (only the lane's own bit is ever set), but
// diverge for the bitwise-complement wave-mask idioms (S_NOT / S_ORN2 / S_ANDN2 /
// S_NAND / S_NOR), which set the unused upper 63 bits. A whole-word test then
// reports the lane active even when its bit is clear. Unity's PostProcessing NaN
// killer combines its channels as `anyNaN | ~allFinite` (S_ORN2_B64); under the
// whole-word test every valid pixel read as NaN and was replaced with 0, zeroing
// the whole HDR scene before tone-mapping.
public sealed class Gen5WaveMaskSpirvTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void WaveMaskPredicate_IsTestedAtCurrentLaneBit()
    {
        // V_CMP_EQ_F32 vcc, v0, v1 writes VCC at run time, which re-materialises
        // the per-lane _vcc predicate from the wave mask via IsWaveMaskActive.
        var spirv = Compile([0x7C04_0300u]);

        // The lane's bit in single-lane emulation is the 64-bit constant 1, so the
        // predicate is `(mask & 1) != 0`. The whole-word bug emitted `mask != 0`
        // with no such mask. Require the lane-bit AND to be present.
        Assert.True(
            ContainsLaneBitMaskedWaveTest(spirv),
            "wave-mask predicate must be tested at the current lane bit "
                + "(mask & lane_bit), not as a whole-word non-zero test");
    }

    [Fact]
    public void StructuredWave64ExecControl_AvoidsWorkgroupBarriers()
    {
        // v_cmp_eq_f32 vcc, v0, v1
        // s_and_saveexec_b64 vcc, vcc
        // s_mov_b64 exec, vcc
        // s_mov_b32 vcc_lo, literal
        // v_add_f32 v2, vcc_lo, v2 (SDWA)
        var spirv = Compile(
            [
                0x7C04_0300u,
                0xBEEA_246Au,
                0xBEFE_046Au,
                0xBEEA_03FFu,
                0xBCFA_00FAu,
                0x0604_04F9u,
                0x2686_066Au,
            ],
            waveLaneCount: 64,
            localSizeX: 8,
            localSizeY: 8);

        Assert.DoesNotContain(
            (ushort)SpirvOp.ControlBarrier,
            EnumerateInstructions(spirv).Select(static item => item.Op));
    }

    [Fact]
    public void StructuredWave64VcmpxLoop_AvoidsWorkgroupBarriers()
    {
        // s_mov_b64 s[2:3], exec
        // v_cmpx_le_u32 exec, s4, v18
        // s_mov_b64 exec, s[2:3]
        // This is the divergent loop shape used by the hot Demon's Souls
        // post-processing kernel. Its lanes do not exchange data, so both
        // native wave32 halves can execute independently.
        var spirv = Compile(
            [
                0xBE82_047Eu,
                0xBEEB_03FFu,
                0x0000_0000u,
                0xBF0D_886Bu,
                0x7DA6_2404u,
                0xBEFE_0402u,
                0xB06A_0048u,
                0x7DA8_1C6Au,
            ],
            waveLaneCount: 64,
            localSizeX: 8,
            localSizeY: 8);

        Assert.DoesNotContain(
            (ushort)SpirvOp.ControlBarrier,
            EnumerateInstructions(spirv).Select(static item => item.Op));
    }

    // True when the module contains an OpBitwiseAnd whose operand is a 64-bit
    // constant of value 1 — the current-lane bit that IsCurrentLaneSet masks the
    // wave mask with before the non-zero test.
    private static bool ContainsLaneBitMaskedWaveTest(byte[] spirv)
    {
        var laneBitConstIds = new HashSet<uint>();

        // Pass 1: collect 64-bit OpConstant result-ids whose value is 1.
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpConstant = 43; a 64-bit constant occupies 5 words
            // (opcode, resultType, resultId, valueLow, valueHigh).
            if (op != 43 || wordCount != 5)
            {
                continue;
            }

            var resultId = ReadWord(spirv, offset + 8);
            var low = ReadWord(spirv, offset + 12);
            var high = ReadWord(spirv, offset + 16);
            if (low == 1 && high == 0)
            {
                laneBitConstIds.Add(resultId);
            }
        }

        // Pass 2: look for an OpBitwiseAnd that consumes one of those constants.
        foreach (var (op, wordCount, offset) in EnumerateInstructions(spirv))
        {
            // OpBitwiseAnd = 199 (opcode, resultType, resultId, operand0, operand1).
            if (op != 199 || wordCount != 5)
            {
                continue;
            }

            var operand0 = ReadWord(spirv, offset + 12);
            var operand1 = ReadWord(spirv, offset + 16);
            if (laneBitConstIds.Contains(operand0) || laneBitConstIds.Contains(operand1))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(ushort Op, int WordCount, int Offset)> EnumerateInstructions(
        byte[] spirv)
    {
        // 5-word SPIR-V header, then (wordCount << 16 | opcode) packed instructions.
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
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
        BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset, sizeof(uint)));

    private static byte[] Compile(
        uint[] programWords,
        uint waveLaneCount = 32,
        uint localSizeX = 1,
        uint localSizeY = 1)
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
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                localSizeX,
                localSizeY,
                1,
                out var shader,
                out error,
                waveLaneCount: waveLaneCount),
            error);
        return shader.Spirv;
    }
}
