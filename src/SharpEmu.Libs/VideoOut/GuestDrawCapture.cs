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
    private static int _captureCount;

    public static void CaptureIfRequested(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestRenderTarget> targets,
        ulong shaderAddress,
        uint vertexCount,
        uint instanceCount,
        uint primitiveType)
    {
        var directory = GetEnvironmentVariable("DRAW_CAPTURE_DIR");
        if (string.IsNullOrWhiteSpace(directory) || targets.Count == 0)
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

            var report = new
            {
                Capture = captureNumber,
                ShaderAddress = $"0x{shaderAddress:X16}",
                PixelSpirvSha256 = Convert.ToHexString(SHA256.HashData(pixelSpirv)),
                vertexCount,
                instanceCount,
                primitiveType,
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
