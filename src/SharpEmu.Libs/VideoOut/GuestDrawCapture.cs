// Copyright (C) 2026 Touché PX5 Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Opt-in capture at the guest/backend seam. The resulting payload contains
/// host-independent guest texture bytes and can be reconstructed without
/// Vulkan by scripts/reconstruct_guest_draw.py.
/// </summary>
internal static class GuestDrawCapture
{
    private const int DefaultMaxCaptures = 256;
    private static readonly ConcurrentDictionary<string, byte> CapturedKeys = new();
    private static readonly string? CaptureDirectory =
        GetEnvironmentVariable("DRAW_CAPTURE_DIR");
    private static int _captureCount;

    public static bool Enabled => !string.IsNullOrWhiteSpace(CaptureDirectory);

    public static void CaptureIfRequested(
        byte[] vertexSpirv,
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestVertexBuffer> vertexBuffers,
        GuestIndexBuffer? indexBuffer,
        GuestRenderState renderState,
        GuestDepthTarget? depthTarget,
        IReadOnlyList<GuestRenderTarget> targets,
        ulong shaderAddress,
        uint vertexCount,
        uint instanceCount,
        uint primitiveType)
    {
        var directory = CaptureDirectory;
        if (string.IsNullOrWhiteSpace(directory) || targets.Count == 0)
        {
            return;
        }

        var triggerFile = GetEnvironmentVariable("DRAW_CAPTURE_TRIGGER_FILE");
        if (!string.IsNullOrWhiteSpace(triggerFile) && !File.Exists(triggerFile))
        {
            return;
        }

        var target = targets[0];
        var expectedWidth = GetUIntEnvironmentVariable("DRAW_CAPTURE_WIDTH", 512);
        var expectedHeight = GetUIntEnvironmentVariable("DRAW_CAPTURE_HEIGHT", 384);
        if (target.Width != expectedWidth || target.Height != expectedHeight)
        {
            return;
        }

        var shaderFilter = GetEnvironmentVariable("DRAW_CAPTURE_SHADER");
        if (!string.IsNullOrWhiteSpace(shaderFilter) &&
            (!TryParseUInt64(shaderFilter, out var expectedShader) || expectedShader != shaderAddress))
        {
            return;
        }

        // The Castlevania/NDS path under investigation is an indexed image and
        // palette pair. Keeping the default capture this narrow prevents videos
        // and ordinary RGBA draws from exhausting the diagnostic budget.
        var indexedOnly = !string.Equals(
            GetEnvironmentVariable("DRAW_CAPTURE_ALL"),
            "1",
            StringComparison.Ordinal);
        if (indexedOnly &&
            (!textures.Any(static texture => texture.Format == 1) ||
             !textures.Any(static texture => texture.Format == 10 && texture.Height == 1)))
        {
            return;
        }

        var payloads = new CapturedTexture[textures.Count];
        var key = new StringBuilder()
            .Append(shaderAddress).Append('|')
            .Append(target.Address).Append('|')
            .Append(vertexCount).Append('|')
            .Append(instanceCount).Append('|')
            .Append(primitiveType);

        AppendHash(key, vertexSpirv);
        foreach (var vertexBuffer in vertexBuffers)
        {
            var length = Math.Clamp(vertexBuffer.Length, 0, vertexBuffer.Data.Length);
            key.Append('|').Append(vertexBuffer.Location)
                .Append(':').Append(vertexBuffer.ComponentCount)
                .Append(':').Append(vertexBuffer.DataFormat)
                .Append(':').Append(vertexBuffer.NumberFormat)
                .Append(':').Append(vertexBuffer.BaseAddress)
                .Append(':').Append(vertexBuffer.Stride)
                .Append(':').Append(vertexBuffer.OffsetBytes)
                .Append(':').Append(vertexBuffer.PerInstance);
            AppendHash(key, vertexBuffer.Data.AsSpan(0, length));
        }

        if (indexBuffer is not null)
        {
            var length = Math.Clamp(indexBuffer.Length, 0, indexBuffer.Data.Length);
            key.Append("|index:").Append(indexBuffer.Is32Bit);
            AppendHash(key, indexBuffer.Data.AsSpan(0, length));
        }

        AppendRenderStateKey(key, renderState, depthTarget);

        for (var index = 0; index < textures.Count; index++)
        {
            var texture = textures[index];
            var (sourceKind, bytes) = GetTexturePayload(texture);
            var hash = bytes.Length == 0
                ? string.Empty
                : Convert.ToHexString(SHA256.HashData(bytes));
            var nonZeroBytes = 0;
            foreach (var value in bytes)
            {
                if (value != 0)
                {
                    nonZeroBytes++;
                }
            }

            payloads[index] = new CapturedTexture(
                index,
                texture,
                sourceKind,
                bytes,
                hash,
                nonZeroBytes);
            key.Append('|').Append(texture.Address)
                .Append(':').Append(texture.Width)
                .Append('x').Append(texture.Height)
                .Append(':').Append(texture.Pitch)
                .Append(':').Append(texture.Format)
                .Append(':').Append(texture.NumberType)
                .Append(':').Append(texture.DstSelect)
                .Append(':').Append(hash);
        }

        if (!CapturedKeys.TryAdd(key.ToString(), 0))
        {
            return;
        }

        var captureNumber = Interlocked.Increment(ref _captureCount);
        var maxCaptures = (int)Math.Clamp(
            GetUIntEnvironmentVariable("DRAW_CAPTURE_MAX", DefaultMaxCaptures),
            1,
            4096);
        if (captureNumber > maxCaptures)
        {
            return;
        }

        try
        {
            var captureDirectory = Path.Combine(directory, $"draw-{captureNumber:D4}");
            Directory.CreateDirectory(captureDirectory);
            string? vertexSpirvFile = null;
            if (vertexSpirv.Length != 0)
            {
                vertexSpirvFile = "vertex.spv";
                File.WriteAllBytes(Path.Combine(captureDirectory, vertexSpirvFile), vertexSpirv);
            }

            string? pixelSpirvFile = null;
            if (pixelSpirv.Length != 0)
            {
                pixelSpirvFile = "pixel.spv";
                File.WriteAllBytes(Path.Combine(captureDirectory, pixelSpirvFile), pixelSpirv);
            }

            var textureReports = new object[payloads.Length];
            for (var index = 0; index < payloads.Length; index++)
            {
                var payload = payloads[index];
                string? fileName = null;
                if (payload.Bytes.Length != 0)
                {
                    fileName = $"texture-{payload.Index:D2}-{payload.SourceKind}.bin";
                    File.WriteAllBytes(Path.Combine(captureDirectory, fileName), payload.Bytes);
                }

                var texture = payload.Texture;
                textureReports[index] = new
                {
                    payload.Index,
                    Address = $"0x{texture.Address:X16}",
                    texture.Width,
                    texture.Height,
                    texture.Pitch,
                    texture.Format,
                    texture.NumberType,
                    texture.DstSelect,
                    texture.Type,
                    texture.Depth,
                    texture.ArrayLayers,
                    texture.TileMode,
                    texture.IsStorage,
                    texture.IsFallback,
                    payload.SourceKind,
                    File = fileName,
                    ByteLength = payload.Bytes.Length,
                    payload.NonZeroBytes,
                    Sha256 = payload.Hash,
                };
            }

            var vertexBufferReports = new object[vertexBuffers.Count];
            for (var index = 0; index < vertexBuffers.Count; index++)
            {
                var buffer = vertexBuffers[index];
                var length = Math.Clamp(buffer.Length, 0, buffer.Data.Length);
                var bytes = buffer.Data.AsSpan(0, length).ToArray();
                var fileName = $"vertex-{index:D2}.bin";
                File.WriteAllBytes(Path.Combine(captureDirectory, fileName), bytes);
                vertexBufferReports[index] = new
                {
                    Index = index,
                    buffer.Location,
                    buffer.ComponentCount,
                    buffer.DataFormat,
                    buffer.NumberFormat,
                    Address = $"0x{buffer.BaseAddress:X16}",
                    buffer.Stride,
                    buffer.OffsetBytes,
                    buffer.PerInstance,
                    File = fileName,
                    ByteLength = bytes.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                };
            }

            object? indexBufferReport = null;
            if (indexBuffer is not null)
            {
                var length = Math.Clamp(indexBuffer.Length, 0, indexBuffer.Data.Length);
                var bytes = indexBuffer.Data.AsSpan(0, length).ToArray();
                const string fileName = "index.bin";
                File.WriteAllBytes(Path.Combine(captureDirectory, fileName), bytes);
                indexBufferReport = new
                {
                    indexBuffer.Is32Bit,
                    File = fileName,
                    ByteLength = bytes.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                };
            }

            var report = new
            {
                Capture = captureNumber,
                ShaderAddress = $"0x{shaderAddress:X16}",
                VertexSpirvSha256 = Convert.ToHexString(SHA256.HashData(vertexSpirv)),
                VertexSpirvFile = vertexSpirvFile,
                PixelSpirvSha256 = Convert.ToHexString(SHA256.HashData(pixelSpirv)),
                PixelSpirvFile = pixelSpirvFile,
                vertexCount,
                instanceCount,
                primitiveType,
                VertexBuffers = vertexBufferReports,
                IndexBuffer = indexBufferReport,
                RenderState = renderState,
                DepthTarget = depthTarget,
                Target = new
                {
                    Address = $"0x{target.Address:X16}",
                    target.Width,
                    target.Height,
                    target.Format,
                    target.NumberType,
                },
                Textures = textureReports,
            };
            File.WriteAllText(
                Path.Combine(captureDirectory, "manifest.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Guest draw capture failed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Guest draw capture failed: {exception.Message}");
        }
    }

    private static void AppendHash(StringBuilder key, ReadOnlySpan<byte> bytes)
    {
        key.Append(':');
        if (!bytes.IsEmpty)
        {
            key.Append(Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }

    private static void AppendRenderStateKey(
        StringBuilder key,
        GuestRenderState renderState,
        GuestDepthTarget? depthTarget)
    {
        key.Append("|state:")
            .Append(JsonSerializer.Serialize(renderState))
            .Append("|depth:")
            .Append(JsonSerializer.Serialize(depthTarget));
    }

    private static (string Kind, byte[] Bytes) GetTexturePayload(GuestDrawTexture texture)
    {
        if (texture.RgbaPixels.Length != 0)
        {
            return ("linear", texture.RgbaPixels);
        }

        if (texture.TiledSource is { Length: > 0 } tiledSource)
        {
            return ("tiled", tiledSource);
        }

        return ("empty", []);
    }

    private static string? GetEnvironmentVariable(string suffix) =>
        Environment.GetEnvironmentVariable($"TOUCHEPX5_{suffix}") ??
        Environment.GetEnvironmentVariable($"SHARPEMU_{suffix}");

    private static uint GetUIntEnvironmentVariable(string suffix, uint fallback) =>
        uint.TryParse(GetEnvironmentVariable(suffix), out var value) ? value : fallback;

    private static bool TryParseUInt64(string text, out ulong value)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value)
            : ulong.TryParse(text, out value);
    }

    private sealed record CapturedTexture(
        int Index,
        GuestDrawTexture Texture,
        string SourceKind,
        byte[] Bytes,
        string Hash,
        int NonZeroBytes);
}
