// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using Touche.PS5.Modules;
using Xunit;

namespace Touche.PS5.Modules.Tests;

public sealed class ModuleResolutionPolicyRepositoryTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"touche-module-policy-tests-{Guid.NewGuid():N}");

    [Fact]
    public void SaveAndLoadRoundTripsValidatedPolicy()
    {
        var path = Path.Combine(_temporaryDirectory, "module-policy.json");
        var repository = new ModuleResolutionPolicyRepository();
        var policy = new ModuleResolutionPolicy
        {
            HleModules =
            [
                new HleModuleDescriptor("libExample.sprx", HleImplementationQuality.Partial),
            ],
            GameOverrides =
            [
                new GameModuleResolutionOverride("PPSA00001", "libExample.sprx", ModuleResolutionMode.PreferLle),
            ],
        };

        repository.Save(path, policy);
        var loaded = repository.Load(path);

        Assert.Equal(policy.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(policy.HleModules, loaded.HleModules);
        Assert.Equal(policy.GameOverrides, loaded.GameOverrides);
        Assert.Contains("\"PreferLle\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_temporaryDirectory, "*.tmp"));
    }

    [Fact]
    public void SaveRejectsUnsupportedSchemaWithoutWritingFile()
    {
        var path = Path.Combine(_temporaryDirectory, "module-policy.json");
        var repository = new ModuleResolutionPolicyRepository();
        var policy = new ModuleResolutionPolicy { SchemaVersion = 999 };

        Assert.Throws<InvalidDataException>(() => repository.Save(path, policy));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void LoadRejectsMalformedJson()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var path = Path.Combine(_temporaryDirectory, "module-policy.json");
        File.WriteAllText(path, "{ not-json }");

        var exception = Assert.Throws<InvalidDataException>(
            () => new ModuleResolutionPolicyRepository().Load(path));

        Assert.IsType<System.Text.Json.JsonException>(exception.InnerException);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
