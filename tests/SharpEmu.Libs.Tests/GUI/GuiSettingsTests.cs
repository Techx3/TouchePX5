// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GuiSettingsTests
{
    [Fact]
    public void NormalizeFromJson_AllPropertiesNull_FallsBackToDefaults()
    {
        const string json = """
            {
              "LogLevel": null,
              "GameFolders": null,
              "ExcludedGames": null,
              "EnvironmentToggles": null,
              "Language": null,
              "DiscordClientId": null
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal("Info", settings.LogLevel);
        Assert.Equal("en", settings.Language);
        Assert.Equal("1525606762248540221", settings.DiscordClientId);
        Assert.Empty(settings.GameFolders);
        Assert.Empty(settings.ExcludedGames);
        Assert.Empty(settings.EnvironmentToggles);
    }

    [Fact]
    public void NormalizeFromJson_ValidValues_ArePreserved()
    {
        const string json = """
            {
              "LogLevel": "Debug",
              "GameFolders": ["C:\\Games"],
              "ExcludedGames": ["C:\\Games\\skip.bin"],
              "EnvironmentToggles": ["SHARPEMU_TRACE"],
              "Language": "pt-BR",
              "DiscordClientId": "999"
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal("Debug", settings.LogLevel);
        Assert.Equal("pt-BR", settings.Language);
        Assert.Equal("999", settings.DiscordClientId);
        Assert.Equal(["C:\\Games"], settings.GameFolders);
        Assert.Equal(["C:\\Games\\skip.bin"], settings.ExcludedGames);
        Assert.Equal(["SHARPEMU_TRACE"], settings.EnvironmentToggles);
    }

    // An empty Discord client ID intentionally disables Rich Presence.
    [Fact]
    public void NormalizeFromJson_EmptyDiscordClientId_IsPreservedNotNormalized()
    {
        const string json = """{ "DiscordClientId": "" }""";

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal(string.Empty, settings.DiscordClientId);
    }

    [Fact]
    public void NormalizeFromJson_NullOrEmptyListEntries_AreFilteredOut()
    {
        const string json = """
            {
              "GameFolders": ["C:\\Games", null, ""],
              "ExcludedGames": [null],
              "EnvironmentToggles": [null, "SHARPEMU_TRACE", ""]
            }
            """;

        var settings = GuiSettings.NormalizeFromJson(json);

        Assert.Equal(["C:\\Games"], settings.GameFolders);
        Assert.Empty(settings.ExcludedGames);
        Assert.Equal(["SHARPEMU_TRACE"], settings.EnvironmentToggles);
    }

    [Fact]
    public void NormalizeFromJson_EmptyObject_UsesConstructorDefaults()
    {
        var settings = GuiSettings.NormalizeFromJson("{}");

        Assert.Equal("Info", settings.LogLevel);
        Assert.Equal("en", settings.Language);
        Assert.Equal("1525606762248540221", settings.DiscordClientId);
        Assert.Empty(settings.GameFolders);
        Assert.Empty(settings.ExcludedGames);
        Assert.Empty(settings.EnvironmentToggles);
    }
}
