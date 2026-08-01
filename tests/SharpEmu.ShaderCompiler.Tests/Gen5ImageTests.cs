// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5ImageTests
{
    private const ulong ShaderAddress = 0x1_0000_C000;
    private const uint SEndpgm = 0xBF810000;

    [Theory]
    [InlineData(1u, SpirvImageDim.Dim2D, 2u)]
    [InlineData(2u, SpirvImageDim.Dim3D, 3u)]
    public void ImageStoreDimensionControlsImageAndCoordinateTypes(
        uint dimension,
        SpirvImageDim expectedImageDimension,
        uint expectedCoordinateComponents)
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageStore", dimension));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)expectedImageDimension, imageType.Operands[2]);
        Assert.Equal(2u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageWrite,
            coordinateOperand: 1,
            expectedComponents: expectedCoordinateComponents);

        var sizeQuery = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageQuerySize);
        AssertVectorTypeWidth(
            instructions,
            sizeQuery.Operands[0],
            expectedCoordinateComponents);
    }

    [Fact]
    public void ImageSampleDim3DUsesThreeComponentSampleCoordinates()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageSampleLz", dimension: 2));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);

        Assert.Equal((uint)SpirvImageDim.Dim3D, imageType.Operands[2]);
        Assert.Equal(1u, imageType.Operands[6]);
        AssertCoordinateVectorWidth(
            instructions,
            SpirvOp.ImageSampleExplicitLod,
            coordinateOperand: 3,
            expectedComponents: 3);
    }

    [Fact]
    public void SampledImageLoadUsesIntegerFetchInsteadOfStorageRead()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageLoad", dimension: 1));

        Assert.Single(instructions, item => item.Opcode == SpirvOp.ImageFetch);
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageRead);
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);
        Assert.Equal(1u, imageType.Operands[6]);
    }

    [Fact]
    public void ImageLoadSharingDescriptorWithStoreUsesStorageRead()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation(
                "ImageLoad",
                dimension: 1,
                storageAliasOpcode: "ImageStore"));

        Assert.Single(instructions, item => item.Opcode == SpirvOp.ImageRead);
        Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ImageFetch);
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);
        Assert.Equal(2u, imageType.Operands[6]);
    }

    [Theory]
    [InlineData(0x1u, 1)]
    [InlineData(0x5u, 2)]
    [InlineData(0xFu, 4)]
    public void ImageLoadDmaskControlsReturnedComponentCount(
        uint dmask,
        int expectedComponents)
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageLoad", dimension: 1, dmask: dmask));
        var fetch = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageFetch);
        var resultId = fetch.Operands[1];

        Assert.Equal(
            expectedComponents,
            instructions.Count(item =>
                item.Opcode == SpirvOp.CompositeExtract &&
                item.Operands.Length >= 3 &&
                item.Operands[2] == resultId));
    }

    [Theory]
    [InlineData(44u, SpirvOp.TypeFloat, -1)] // RGBA8_UNORM
    [InlineData(48u, SpirvOp.TypeInt, 0)]    // RGBA8_UINT
    [InlineData(49u, SpirvOp.TypeInt, 1)]    // RGBA8_SINT
    public void ImageLoadPreservesDescriptorNumericKind(
        uint unifiedFormat,
        SpirvOp expectedScalarType,
        int expectedSignedness)
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation(
                "ImageLoad",
                dimension: 1,
                unifiedFormat: unifiedFormat));
        var imageType = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.TypeImage);
        var scalarTypeId = imageType.Operands[1];
        var scalarType = Assert.Single(
            instructions,
            item => item.Opcode == expectedScalarType &&
                    item.Operands[0] == scalarTypeId);

        if (expectedScalarType == SpirvOp.TypeInt)
        {
            Assert.Equal((uint)expectedSignedness, scalarType.Operands[2]);
        }
    }

    [Fact]
    public void OneDimensionalImageLoadSynthesizesZeroHostYCoordinate()
    {
        var instructions = ReadSpirvInstructions(
            CompileImageOperation("ImageLoad", dimension: 0));
        var fetch = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.ImageFetch);
        var coordinateId = fetch.Operands[3];
        var coordinate = Assert.Single(
            instructions,
            item => item.Opcode == SpirvOp.CompositeConstruct &&
                    item.Operands.Length == 4 &&
                    item.Operands[1] == coordinateId);
        var syntheticY = coordinate.Operands[3];

        Assert.Contains(
            instructions,
            item => item.Opcode == SpirvOp.Constant &&
                    item.Operands.Length == 3 &&
                    item.Operands[1] == syntheticY &&
                    item.Operands[2] == 0);
    }

    [Fact]
    public void PaletteDescriptorDecodesSizeMinusOneFieldsExactly()
    {
        const ulong address = 0x0000_1234_5678_0000;
        var descriptor = CreateDescriptor(
            unifiedFormat: 44,
            width: 256,
            height: 1,
            address: address);

        Assert.True(
            Gen5ImageDescriptor.TryDecode(
                descriptor,
                out var decoded,
                out var error),
            error);
        Assert.Equal(address, decoded.BaseAddress);
        Assert.Equal(256u, decoded.Width);
        Assert.Equal(1u, decoded.Height);
        Assert.Equal(8u, decoded.DataFormat);
        Assert.Equal(0u, decoded.NumberFormat);
        Assert.Equal(9u, decoded.ResourceType);
        Assert.Equal(0xFACu, decoded.DstSelect);
    }

    [Fact]
    public void ImageDescriptorDecodesMaximumSixteenBitExtents()
    {
        var descriptor = CreateDescriptor(
            unifiedFormat: 44,
            width: 65536,
            height: 65536,
            address: 0x0000_1234_5678_0000);

        Assert.True(
            Gen5ImageDescriptor.TryDecode(
                descriptor,
                out var decoded,
                out var error),
            error);
        Assert.Equal(65536u, decoded.Width);
        Assert.Equal(65536u, decoded.Height);
    }

    [Fact]
    public void ArrayDescriptorDecodesRangesSwizzleAndMetadataAddress()
    {
        var descriptor = CreateDescriptor(
            unifiedFormat: 44,
            width: 512,
            height: 384,
            address: 0x0000_1234_5678_0000,
            resourceType: 13);
        descriptor[3] =
            0xF2Eu |
            (2u << 12) |
            (5u << 16) |
            (24u << 20) |
            (13u << 28);
        descriptor[4] = 7u | (3u << 16);
        descriptor[6] = 0x00A5_5AA5u | (0xABu << 24);
        descriptor[7] = 0x0012_3456u;

        Assert.True(
            Gen5ImageDescriptor.TryDecode(
                descriptor,
                out var decoded,
                out var error),
            error);
        Assert.Equal(2u, decoded.BaseLevel);
        Assert.Equal(5u, decoded.LastLevel);
        Assert.Equal(3u, decoded.BaseArray);
        Assert.Equal(7u, decoded.LastArray);
        Assert.Equal(8u, decoded.Depth);
        Assert.Equal(0xF2Eu, decoded.DstSelect);
        Assert.Equal(24u, decoded.SwizzleMode);
        Assert.Equal(0x00A5_5AA5u, decoded.DescriptorFlags);
        Assert.Equal(0x0000_0012_3456_AB00ul, decoded.MetadataAddress);
    }

    [Fact]
    public void DiagnosticArtifactCorrelatesGuestBindingWithSpirvFetch()
    {
        var compilation = CompileImageOperationWithContext(
            "ImageLoad",
            dimension: 0,
            dmask: 0xF,
            unifiedFormat: 44);
        var artifact = Gen5SpirvDiagnosticArtifact.Create(
            Gen5SpirvStage.Compute,
            compilation.State,
            compilation.Evaluation,
            compilation.Shader);

        var binding = Assert.Single(artifact.ImageBindings);
        Assert.Equal(nameof(SpirvOp.ImageFetch), binding.ExpectedSpirvOpcode);
        Assert.Equal("combined_image_sampler", binding.VulkanDescriptorType);
        Assert.Equal(256u, binding.Descriptor?.Width);
        Assert.True(artifact.SpirvImageOperations.ContainsKey(nameof(SpirvOp.ImageFetch)));
    }

    private static byte[] CompileImageOperation(
        string opcode,
        uint dimension,
        uint dmask = 0xF,
        uint unifiedFormat = 71,
        string? storageAliasOpcode = null) =>
        CompileImageOperationWithContext(
            opcode,
            dimension,
            dmask,
            unifiedFormat,
            storageAliasOpcode).Shader.Spirv;

    private static CompiledImageOperation CompileImageOperationWithContext(
        string opcode,
        uint dimension,
        uint dmask = 0xF,
        uint unifiedFormat = 71,
        string? storageAliasOpcode = null)
    {
        var addressRegisters = dimension == 2
            ? new uint[] { 0, 1, 2 }
            : [0, 1];
        var control = new Gen5ImageControl(
            Dmask: dmask,
            VectorAddress: 0,
            AddressRegisters: addressRegisters,
            VectorData: 4,
            ScalarResource: 8,
            ScalarSampler: 16,
            Dimension: dimension,
            IsArray: false,
            Glc: false,
            Slc: false,
            A16: false,
            D16: false);
        var imageInstruction = new Gen5ShaderInstruction(
            0,
            Gen5ShaderEncoding.Mimg,
            opcode,
            [],
            [],
            [],
            control);
        var end = new Gen5ShaderInstruction(
            8,
            Gen5ShaderEncoding.Sopp,
            "SEndpgm",
            [SEndpgm],
            [],
            [],
            null);
        var state = new Gen5ShaderState(
            new Gen5ShaderProgram(ShaderAddress, [imageInstruction, end]),
            [],
            null);
        var scalarRegisters = new uint[256];
        var descriptor = CreateDescriptor(
            unifiedFormat,
            width: 256,
            height: 1,
            address: 0x0000_1234_5678_0000,
            resourceType: dimension == 2 ? 10u : 9u);
        var bindings = new List<Gen5ImageBinding>
        {
            new(
                imageInstruction.Pc,
                imageInstruction.Opcode,
                control,
                descriptor,
                new uint[4],
                null),
        };
        if (storageAliasOpcode is not null)
        {
            bindings.Add(
                new Gen5ImageBinding(
                    imageInstruction.Pc + 8,
                    storageAliasOpcode,
                    control,
                    descriptor,
                    new uint[4],
                    null));
        }
        var evaluation = new Gen5ShaderEvaluation(
            scalarRegisters,
            scalarRegisters,
            bindings,
            []);

        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out var error),
            error);
        return new CompiledImageOperation(state, evaluation, shader);
    }

    private static uint[] CreateDescriptor(
        uint unifiedFormat,
        uint width,
        uint height,
        ulong address,
        uint resourceType = 9)
    {
        Assert.InRange(width, 1u, 65536u);
        Assert.InRange(height, 1u, 65536u);
        var widthMinusOne = width - 1;
        var descriptor = new uint[8];
        var shiftedAddress = address >> 8;
        descriptor[0] = (uint)shiftedAddress;
        descriptor[1] =
            (uint)((shiftedAddress >> 32) & 0xFFu) |
            (unifiedFormat << 20) |
            ((widthMinusOne & 0x3u) << 30);
        descriptor[2] =
            ((widthMinusOne >> 2) & 0x3FFFu) |
            (((height - 1) & 0xFFFFu) << 14);
        descriptor[3] = 0xFACu | (resourceType << 28);
        descriptor[4] = width - 1;
        return descriptor;
    }

    private static IReadOnlyList<ParsedSpirvInstruction> ReadSpirvInstructions(
        byte[] spirv)
    {
        var instructions = new List<ParsedSpirvInstruction>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset));
            var wordCount = checked((int)(instruction >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var operand = 0; operand < operands.Length; operand++)
            {
                operands[operand] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + (operand + 1) * sizeof(uint)));
            }

            instructions.Add(
                new ParsedSpirvInstruction((SpirvOp)(ushort)instruction, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static void AssertCoordinateVectorWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        SpirvOp operation,
        int coordinateOperand,
        uint expectedComponents)
    {
        var imageOperation = Assert.Single(
            instructions,
            item => item.Opcode == operation);
        var coordinateId = imageOperation.Operands[coordinateOperand];
        var coordinate = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.CompositeConstruct &&
                item.Operands.Length >= 2 &&
                item.Operands[1] == coordinateId);
        AssertVectorTypeWidth(
            instructions,
            coordinate.Operands[0],
            expectedComponents);
    }

    private static void AssertVectorTypeWidth(
        IReadOnlyList<ParsedSpirvInstruction> instructions,
        uint vectorTypeId,
        uint expectedComponents)
    {
        var vectorType = Assert.Single(
            instructions,
            item =>
                item.Opcode == SpirvOp.TypeVector &&
                item.Operands[0] == vectorTypeId);
        Assert.Equal(expectedComponents, vectorType.Operands[2]);
    }

    private readonly record struct ParsedSpirvInstruction(
        SpirvOp Opcode,
        uint[] Operands);

    private readonly record struct CompiledImageOperation(
        Gen5ShaderState State,
        Gen5ShaderEvaluation Evaluation,
        Gen5SpirvShader Shader);
}
