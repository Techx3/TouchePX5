// Copyright (C) 2026 Touché PX5 contributors
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace Touche.PS5.Modules;

public sealed class ModuleResolutionPolicyRepository
{
    private const long MaximumPolicyBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    public ModuleResolutionPolicy Load(string path)
    {
        var fullPath = ValidatePath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The module resolution policy was not found.", fullPath);
        }
        if (info.Length is <= 0 or > MaximumPolicyBytes)
        {
            throw new InvalidDataException("The module resolution policy has an invalid size.");
        }

        ModuleResolutionPolicy policy;
        try
        {
            policy = JsonSerializer.Deserialize<ModuleResolutionPolicy>(
                File.ReadAllText(fullPath),
                ReadOptions) ?? throw new InvalidDataException("The module resolution policy is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The module resolution policy is not valid JSON.", exception);
        }

        Validate(policy);
        return policy;
    }

    public void Save(string path, ModuleResolutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Validate(policy);
        var fullPath = ValidatePath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(policy, WriteOptions));
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void Validate(ModuleResolutionPolicy policy)
    {
        if (policy.SchemaVersion != ModuleResolutionPolicy.CurrentSchemaVersion ||
            policy.HleModules is null ||
            policy.LleCompatibility is null ||
            policy.GameOverrides is null)
        {
            throw new InvalidDataException("The module resolution policy is invalid or unsupported.");
        }

        _ = new HybridModuleResolver(
            catalog: null,
            policy.HleModules,
            policy.LleCompatibility,
            policy.GameOverrides);
    }

    private static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
        {
            throw new ArgumentException("A policy file path is required.", nameof(path));
        }
        return fullPath;
    }
}
