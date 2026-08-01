// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text.Json;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.ShaderCompiler.Vulkan;

public sealed record Gen5SpirvDiagnosticBinding(
    uint Pc,
    string GuestOpcode,
    string ExpectedSpirvOpcode,
    string VulkanDescriptorType,
    uint Dmask,
    uint Dimension,
    bool IsArray,
    bool A16,
    bool D16,
    uint? MipLevel,
    string ResourceDescriptorHex,
    string SamplerDescriptorHex,
    Gen5ImageDescriptor? Descriptor,
    string? DescriptorError);

public sealed record Gen5SpirvDiagnosticArtifact(
    string Stage,
    ulong ShaderAddress,
    IReadOnlyList<string> GuestInstructions,
    IReadOnlyList<Gen5SpirvDiagnosticBinding> ImageBindings,
    IReadOnlyDictionary<string, int> SpirvImageOperations)
{
    public static Gen5SpirvDiagnosticArtifact Create(
        Gen5SpirvStage stage,
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        Gen5SpirvShader shader)
    {
        var bindings = evaluation.ImageBindings.Select(binding =>
        {
            var storage = Gen5ShaderTranslator.RequiresStorageImage(
                binding,
                evaluation.ImageBindings);
            var expectedOperation = binding.Opcode switch
            {
                "ImageLoad" or "ImageLoadMip" =>
                    storage ? nameof(SpirvOp.ImageRead) : nameof(SpirvOp.ImageFetch),
                "ImageStore" or "ImageStoreMip" => nameof(SpirvOp.ImageWrite),
                _ when binding.Opcode.StartsWith("ImageSample", StringComparison.Ordinal) =>
                    "OpImageSample*",
                _ when binding.Opcode.StartsWith("ImageGather4", StringComparison.Ordinal) =>
                    "OpImageGather*",
                _ when binding.Opcode.StartsWith("ImageAtomic", StringComparison.Ordinal) =>
                    "OpAtomic*",
                _ => "query-or-unknown",
            };
            var decoded = Gen5ImageDescriptor.TryDecode(
                binding.ResourceDescriptor,
                out var descriptor,
                out var descriptorError);
            return new Gen5SpirvDiagnosticBinding(
                binding.Pc,
                binding.Opcode,
                expectedOperation,
                storage ? "storage_image" : "combined_image_sampler",
                binding.Control.Dmask,
                binding.Control.Dimension,
                binding.Control.IsArray,
                binding.Control.A16,
                binding.Control.D16,
                binding.MipLevel,
                FormatDwords(binding.ResourceDescriptor),
                FormatDwords(binding.SamplerDescriptor),
                decoded ? descriptor : null,
                decoded ? null : descriptorError);
        }).ToArray();

        var imageOperations = EnumerateOpcodes(shader.Spirv)
            .Where(IsImageOperation)
            .GroupBy(static operation => operation.ToString())
            .ToDictionary(static group => group.Key, static group => group.Count());
        var instructions = state.Program.Instructions.Select(instruction =>
            $"0x{instruction.Pc:X4} " +
            $"{string.Join('_', instruction.Words.Select(static word => $"{word:X8}"))} " +
            $"{instruction.Opcode} " +
            $"{string.Join(',', instruction.Destinations)} <- " +
            $"{string.Join(',', instruction.Sources)} {instruction.Control}").ToArray();

        return new Gen5SpirvDiagnosticArtifact(
            stage.ToString(),
            state.Program.Address,
            instructions,
            bindings,
            imageOperations);
    }

    internal static void TryWrite(
        Gen5SpirvStage stage,
        Gen5ShaderState state,
        Gen5ShaderEvaluation evaluation,
        Gen5SpirvShader shader)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "TOUCHEPX5_DUMP_SHADER_DIAGNOSTICS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var directory = Environment.GetEnvironmentVariable(
                "TOUCHEPX5_SHADER_DIAGNOSTICS_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Path.Combine(
                    AppContext.BaseDirectory,
                    "shader-diagnostics");
            }

            Directory.CreateDirectory(directory);
            var artifact = Create(stage, state, evaluation, shader);
            var path = Path.Combine(
                directory,
                $"{state.Program.Address:X16}.{stage.ToString().ToLowerInvariant()}.json");
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    artifact,
                    new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllBytes(
                Path.ChangeExtension(path, ".spv"),
                shader.Spirv);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[SHADER][WARN] diagnostic artifact failed: {exception.Message}");
        }
    }

    private static string FormatDwords(IReadOnlyList<uint> values) =>
        values.Count == 0
            ? string.Empty
            : string.Join(' ', values.Select(static value => $"{value:X8}"));

    private static IEnumerable<SpirvOp> EnumerateOpcodes(byte[] spirv)
    {
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            if (wordCount <= 0 || offset + wordCount * sizeof(uint) > spirv.Length)
            {
                yield break;
            }

            yield return (SpirvOp)(ushort)header;
            offset += wordCount * sizeof(uint);
        }
    }

    private static bool IsImageOperation(SpirvOp operation) => operation is
        SpirvOp.Image or
        SpirvOp.ImageFetch or
        SpirvOp.ImageRead or
        SpirvOp.ImageWrite or
        SpirvOp.ImageQuerySize or
        SpirvOp.ImageQuerySizeLod or
        SpirvOp.ImageSampleImplicitLod or
        SpirvOp.ImageSampleExplicitLod or
        SpirvOp.ImageDrefGather or
        SpirvOp.ImageGather;
}
