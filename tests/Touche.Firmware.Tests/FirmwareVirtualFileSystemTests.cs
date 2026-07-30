// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using Touche.Firmware;
using Xunit;

namespace Touche.Firmware.Tests;

public sealed class FirmwareVirtualFileSystemTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "ToucheFirmwareVfsTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MountedProfileOpensVerifiedObjectsByGuestPath()
    {
        var (store, result, expected) = await ImportProfileAsync();
        var fileSystem = FirmwareVirtualFileSystem.Mount(store, result.Manifest.ProfileId);
        const string path = "/system/config/version.json";

        await using var handle = await fileSystem.OpenReadAsync(path);

        Assert.NotNull(handle);
        Assert.False(handle.Content is FileStream);
        Assert.False(handle.Content.CanWrite);
        Assert.True(fileSystem.Exists(path));
        Assert.Equal(path, fileSystem.GetArtifact(path)!.VirtualPath);
        using var memory = new MemoryStream();
        await handle.Content.CopyToAsync(memory);
        Assert.Equal(expected, memory.ToArray());
        Assert.Null(await fileSystem.OpenReadAsync("/system/config/missing.json"));
    }

    [Theory]
    [InlineData("system/config/version.json")]
    [InlineData("/system/../version.json")]
    [InlineData("/system\\config\\version.json")]
    [InlineData("/system//config/version.json")]
    [InlineData("/")]
    public async Task GuestPathValidationRejectsUnsafeOrNonCanonicalPaths(string path)
    {
        var (store, result, _) = await ImportProfileAsync();
        var fileSystem = FirmwareVirtualFileSystem.Mount(store, result.Manifest.ProfileId);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fileSystem.OpenReadAsync(path));
    }

    [Fact]
    public async Task VerificationDetectsCasTamperingAfterMount()
    {
        var (store, result, _) = await ImportProfileAsync();
        var fileSystem = FirmwareVirtualFileSystem.Mount(store, result.Manifest.ProfileId);
        var artifact = result.Manifest.Artifacts.Single();
        var objectPath = Path.Combine(store, "objects", artifact.Sha256[..2], artifact.Sha256);
        var bytes = File.ReadAllBytes(objectPath);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(objectPath, bytes);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fileSystem.VerifyAsync(artifact.VirtualPath));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MountIsCaseSensitiveAndDoesNotExposePhysicalPaths()
    {
        var (store, result, _) = await ImportProfileAsync();
        var fileSystem = FirmwareVirtualFileSystem.Mount(store, result.Manifest.ProfileId);

        Assert.False(fileSystem.Exists("/SYSTEM/config/version.json"));
        Assert.Null(fileSystem.GetArtifact("/SYSTEM/config/version.json"));
        Assert.DoesNotContain(
            typeof(FirmwareFileHandle).GetProperties(),
            property => property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(string Store, FirmwareImportResult Result, byte[] Content)> ImportProfileAsync()
    {
        var source = Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N"), "source");
        var store = Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N"), "store");
        Directory.CreateDirectory(Path.Combine(source, "system", "config"));
        var content = "{\"version\":\"test\"}"u8.ToArray();
        File.WriteAllBytes(Path.Combine(source, "system", "config", "version.json"), content);
        var result = await new FirmwareDirectoryImporter(store).ImportAsync(source);
        return (store, result, content);
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
