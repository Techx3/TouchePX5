// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Touche.Firmware;
using Xunit;

namespace Touche.Firmware.Tests;

public sealed class FirmwareModuleCatalogBuilderTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "ToucheFirmwareCatalogTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CatalogReadsElfMetadataAndDeclaredDependencies()
    {
        var objects = Path.Combine(_temporaryDirectory, "objects");
        var main = AddObject(objects, CreateElf64("libDependency.sprx"));
        var dependency = AddObject(objects, CreateElf64());
        var manifest = CreateManifest(
            ("/system/common/lib/libMain.sprx", main),
            ("/system/common/lib/libDependency.sprx", dependency));
        var builder = new FirmwareModuleCatalogBuilder(objects);

        var catalog = await builder.BuildAsync(manifest);

        Assert.Equal(2, catalog.Modules.Count);
        var module = Assert.Single(catalog.Modules, item => item.VirtualPath.EndsWith("libMain.sprx"));
        Assert.Equal(FirmwareModuleFormat.Elf64, module.Format);
        Assert.Equal(FirmwareModuleState.Parseable, module.State);
        Assert.Equal("x86-64", module.Architecture);
        Assert.Equal(0x401000UL, module.EntryPoint);
        Assert.True(module.HasDynamicTable);
        Assert.Equal(["libDependency.sprx"], module.Dependencies);
    }

    [Fact]
    public async Task CatalogReportsMissingDependencyAndEncryptedSelf()
    {
        var objects = Path.Combine(_temporaryDirectory, "objects");
        var elf = AddObject(objects, CreateElf64("missing.sprx"));
        var self = AddObject(objects, [0x4f, 0x15, 0x3d, 0x1d, 0, 0, 0, 0]);
        var manifest = CreateManifest(
            ("/system/common/lib/libMain.sprx", elf),
            ("/system/common/lib/protected.self", self));
        var builder = new FirmwareModuleCatalogBuilder(objects);

        var catalog = await builder.BuildAsync(manifest);

        Assert.Equal(
            FirmwareModuleState.MissingDependencies,
            Assert.Single(catalog.Modules, module => module.Format == FirmwareModuleFormat.Elf64).State);
        Assert.Equal(
            FirmwareModuleState.UnsupportedEncryption,
            Assert.Single(catalog.Modules, module => module.Format == FirmwareModuleFormat.SonySelf).State);
    }

    [Fact]
    public async Task CatalogAssociatesVerifiedElfSidecarWithProtectedSelf()
    {
        var objects = Path.Combine(_temporaryDirectory, "objects");
        var self = AddObject(objects, [0x54, 0x14, 0xf5, 0xee, 0x10, 0x01, 0x01, 0x32]);
        var elf = AddObject(objects, CreateElf64());
        var manifest = CreateManifest(
            ("/system/common/lib/libKernel.sprx", self),
            ("/system/common/lib/libKernel.sprx.elf", elf));

        var catalog = await new FirmwareModuleCatalogBuilder(objects).BuildAsync(manifest);

        var protectedModule = Assert.Single(catalog.Modules, module => module.Format == FirmwareModuleFormat.SonySelf);
        var sidecar = Assert.Single(catalog.Modules, module => module.Format == FirmwareModuleFormat.Elf64);
        Assert.Equal(FirmwareModuleState.UnsupportedEncryption, protectedModule.State);
        Assert.Equal(FirmwareModuleState.Parseable, sidecar.State);
        Assert.Equal(protectedModule.VirtualPath, sidecar.ProvidesVirtualPath);
        Assert.Contains("decrypted ELF override", sidecar.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogReadsBoundedMetadataFromProtectedSelfWithoutMakingItLoadable()
    {
        var objects = Path.Combine(_temporaryDirectory, "objects");
        var selfBytes = new byte[128];
        new byte[] { 0x54, 0x14, 0xf5, 0xee, 0x10, 0x01, 0x01, 0x32 }
            .CopyTo(selfBytes, 0);
        CreateElf64().CopyTo(selfBytes, 64);
        var self = AddObject(objects, selfBytes);
        var manifest = CreateManifest(("/system/common/lib/libKernel.sprx", self));

        var catalog = await new FirmwareModuleCatalogBuilder(objects).BuildAsync(manifest);

        var module = Assert.Single(catalog.Modules);
        Assert.Equal(FirmwareModuleFormat.SonySelf, module.Format);
        Assert.Equal(FirmwareModuleState.UnsupportedEncryption, module.State);
        Assert.Equal("x86-64", module.Architecture);
        Assert.Equal(0x401000UL, module.EntryPoint);
        Assert.Contains("0x40", module.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogDoesNotAliasStandaloneElfByFileNameAlone()
    {
        var objects = Path.Combine(_temporaryDirectory, "objects");
        var elf = AddObject(objects, CreateElf64());
        var manifest = CreateManifest(("/system/common/lib/libKernel.sprx.elf", elf));

        var catalog = await new FirmwareModuleCatalogBuilder(objects).BuildAsync(manifest);

        Assert.Null(Assert.Single(catalog.Modules).ProvidesVirtualPath);
    }

    [Fact]
    public async Task CatalogIsDeterministicAndWritesSeparatelyFromManifest()
    {
        var objects = Path.Combine(_temporaryDirectory, "objects");
        var artifact = AddObject(objects, CreateElf64());
        var manifest = CreateManifest(("/system/lib/example.sprx", artifact));
        var builder = new FirmwareModuleCatalogBuilder(objects);

        var first = await builder.BuildAsync(manifest);
        var second = await builder.BuildAsync(manifest);
        var profileDirectory = Path.Combine(_temporaryDirectory, "profiles", manifest.ProfileId);
        await FirmwareModuleCatalogBuilder.WriteAsync(first, profileDirectory);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.True(File.Exists(Path.Combine(profileDirectory, "modules.json")));
        Assert.Contains(first.ContentHash, File.ReadAllText(Path.Combine(profileDirectory, "modules.json")));
    }

    private static FirmwareProfileManifest CreateManifest(
        params (string VirtualPath, (string Hash, long Size) Object)[] modules) => new()
        {
            ProfileId = "ps5-extracted-test",
            ContentHash = new string('a', 64),
            Artifacts = modules.Select(module => new FirmwareArtifact(
                module.VirtualPath,
                module.Object.Size,
                module.Object.Hash,
                FirmwareArtifactKind.ElfOrSelf,
                FirmwareArtifactState.Recognized)).ToArray(),
        };

    private static (string Hash, long Size) AddObject(string objectsRoot, byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var directory = Path.Combine(objectsRoot, hash[..2]);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, hash), bytes);
        return (hash, bytes.Length);
    }

    private static byte[] CreateElf64(string? dependency = null)
    {
        var bytes = new byte[dependency is null ? 64 : 512];
        bytes[0] = 0x7f;
        "ELF"u8.CopyTo(bytes.AsSpan(1));
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 0x3e);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 0x401000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(52), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), 56);
        if (dependency is null)
        {
            return bytes;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), 2);
        WriteProgramHeader(bytes.AsSpan(64), type: 1, offset: 0x100, virtualAddress: 0x400000, fileSize: 0x100);
        WriteProgramHeader(bytes.AsSpan(120), type: 2, offset: 0x100, virtualAddress: 0x400000, fileSize: 0x40);
        WriteDynamic(bytes.AsSpan(0x100), 5, 0x400080);
        WriteDynamic(bytes.AsSpan(0x110), 10, (ulong)dependency.Length + 2);
        WriteDynamic(bytes.AsSpan(0x120), 1, 1);
        WriteDynamic(bytes.AsSpan(0x130), 0, 0);
        bytes[0x180] = 0;
        Encoding.UTF8.GetBytes(dependency).CopyTo(bytes.AsSpan(0x181));
        bytes[0x181 + dependency.Length] = 0;
        return bytes;
    }

    private static void WriteProgramHeader(
        Span<byte> destination,
        uint type,
        ulong offset,
        ulong virtualAddress,
        ulong fileSize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, type);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], virtualAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[40..], fileSize);
    }

    private static void WriteDynamic(Span<byte> destination, long tag, ulong value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, tag);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], value);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
