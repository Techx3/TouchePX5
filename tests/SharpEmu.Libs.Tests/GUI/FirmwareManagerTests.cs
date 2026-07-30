// Copyright (C) 2026 Touché PX5 Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Security.Cryptography;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class FirmwareManagerTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "TouchePx5FirmwareTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsyncStoresValidatedPackageAndManifest()
    {
        var sourcePath = CreatePackage("valid.PUP", validMagic: true);
        var root = Path.Combine(_temporaryDirectory, "installed");
        var manager = new FirmwareManager(root);

        var result = await manager.InstallAsync(sourcePath);

        Assert.False(result.AlreadyInstalled);
        Assert.Equal("SLB2", result.Firmware.ContainerFormat);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(sourcePath)), Convert.FromHexString(result.Firmware.Sha256));
        Assert.True(File.Exists(result.Firmware.PackagePath));
        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(result.Firmware.PackagePath));

        var installed = Assert.Single(manager.GetInstalled());
        Assert.Equal(result.Firmware.Sha256, installed.Sha256);
    }

    [Fact]
    public async Task InstallingSamePackageTwiceIsIdempotent()
    {
        var sourcePath = CreatePackage("duplicate.pup", validMagic: true);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        var first = await manager.InstallAsync(sourcePath);
        var second = await manager.InstallAsync(sourcePath);

        Assert.False(first.AlreadyInstalled);
        Assert.True(second.AlreadyInstalled);
        Assert.Equal(first.Firmware.Sha256, second.Firmware.Sha256);
        Assert.Single(manager.GetInstalled());
    }

    [Fact]
    public async Task InstallAsyncRejectsWrongContainerMagic()
    {
        var sourcePath = CreatePackage("invalid.PUP", validMagic: false);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(sourcePath));
        Assert.Empty(manager.GetInstalled());
    }

    [Fact]
    public async Task InstallAsyncRejectsNonPupExtension()
    {
        var sourcePath = CreatePackage("firmware.bin", validMagic: true);
        var manager = new FirmwareManager(Path.Combine(_temporaryDirectory, "installed"));

        await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(sourcePath));
    }

    private string CreatePackage(string fileName, bool validMagic)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var data = new byte[1024];
        RandomNumberGenerator.Fill(data);
        (validMagic ? "SLB2"u8 : "NOPE"u8).CopyTo(data);
        var path = Path.Combine(_temporaryDirectory, fileName);
        File.WriteAllBytes(path, data);
        return path;
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
