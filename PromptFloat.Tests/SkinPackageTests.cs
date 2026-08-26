using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class SkinPackageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VesperSkinTests_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Install_ValidDataOnlyPackage_IsInstalledAtomically()
    {
        Directory.CreateDirectory(_root);
        var package = CreatePackage("valid.vesperskin", new Dictionary<string, string>
        {
            ["skin.json"] = """
              { "schemaVersion": 1, "id": "paper-cat", "displayName": "纸猫", "version": "1.0.0",
                "themeResource": "theme.json", "preview": "preview.png", "companion": { "kind": "vector", "states": {} } }
              """,
            ["theme.json"] = CompleteThemeJson,
            ["preview.png"] = "preview"
        });

        var service = new SkinPackageService(Path.Combine(_root, "installed"));
        var manifest = service.Install(package);

        Assert.Equal("paper-cat", manifest.Id);
        Assert.True(File.Exists(Path.Combine(_root, "installed", "paper-cat", "1.0.0", "skin.json")));
        var discovered = service.DiscoverInstalled();
        Assert.Single(discovered);
        Assert.Equal("paper-cat", discovered[0].Id);

        service.Uninstall(discovered[0]);
        Assert.Empty(service.DiscoverInstalled());
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("payload.xaml")]
    [InlineData("payload.dll")]
    public void Install_UnsafeEntry_IsRejectedWithoutChangingCatalog(string unsafePath)
    {
        Directory.CreateDirectory(_root);
        var package = CreatePackage("unsafe.vesperskin", new Dictionary<string, string>
        {
            ["skin.json"] = """
              { "schemaVersion": 1, "id": "unsafe", "displayName": "Unsafe", "version": "1.0.0",
                "themeResource": "theme.json", "companion": { "kind": "vector", "states": {} } }
              """,
            ["theme.json"] = "{}",
            [unsafePath] = "bad"
        });
        var installed = Path.Combine(_root, "installed");
        var service = new SkinPackageService(installed);

        Assert.Throws<InvalidDataException>(() => service.Install(package));
        Assert.False(Directory.Exists(Path.Combine(installed, "unsafe")));
    }

    [Fact]
    public void Install_ImageCompanionWithoutAllTwelveStates_IsRejected()
    {
        Directory.CreateDirectory(_root);
        var package = CreatePackage("missing-state.vesperskin", new Dictionary<string, string>
        {
            ["skin.json"] = """
              { "schemaVersion": 1, "id": "missing", "displayName": "Missing", "version": "1.0.0",
                "themeResource": "theme.json", "companion": { "kind": "image", "states": { "Idle": "idle.png" } } }
              """,
            ["theme.json"] = "{}",
            ["idle.png"] = "image"
        });

        var service = new SkinPackageService(Path.Combine(_root, "installed"));
        Assert.Throws<InvalidDataException>(() => service.Install(package));
    }

    [Fact]
    public void Install_VectorCompanionWithIncompleteAnimationManifest_IsRejected()
    {
        Directory.CreateDirectory(_root);
        var package = CreatePackage("missing-vector-animation.vesperskin", new Dictionary<string, string>
        {
            ["skin.json"] = """
              { "schemaVersion": 1, "id": "missing-vector", "displayName": "Missing vector", "version": "1.0.0",
                "themeResource": "theme.json", "companion": { "kind": "vector-layered", "vectorPath": "character/vector.json" } }
              """,
            ["theme.json"] = CompleteThemeJson,
            ["character/vector.json"] = """
              { "schemaVersion": 1, "canvas": { "width": 88, "height": 88 },
                "anchor": { "x": 44, "y": 44 }, "layers": [], "animations": {} }
              """
        });

        var service = new SkinPackageService(Path.Combine(_root, "installed"));
        Assert.Throws<InvalidDataException>(() => service.Install(package));
        Assert.False(Directory.Exists(Path.Combine(_root, "installed", "missing-vector")));
    }

    [Fact]
    public void Install_ThemeMissingTheSharedSemanticContract_IsRejected()
    {
        Directory.CreateDirectory(_root);
        var package = CreatePackage("incomplete-theme.vesperskin", new Dictionary<string, string>
        {
            ["skin.json"] = """
              { "schemaVersion": 1, "id": "incomplete", "displayName": "Incomplete", "version": "1.0.0",
                "themeResource": "theme.json", "companion": { "kind": "vector", "states": {} } }
              """,
            ["theme.json"] = "{ \"BrandColor\": \"#55613C\" }"
        });

        var service = new SkinPackageService(Path.Combine(_root, "installed"));
        var error = Assert.Throws<InvalidDataException>(() => service.Install(package));

        Assert.Contains("语义令牌", error.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "installed", "incomplete")));
    }

    [Fact]
    public void DiscoverInstalled_DamagedSkinThatBypassesInstaller_IsIgnored()
    {
        var install = Path.Combine(_root, "installed", "damaged", "1.0.0");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "skin.json"), """
            { "schemaVersion": 1, "id": "damaged", "displayName": "Damaged", "version": "1.0.0",
              "themeResource": "theme.json", "companion": { "kind": "vector", "states": {} } }
            """);
        File.WriteAllText(Path.Combine(install, "theme.json"), "{}");

        var discovered = new SkinPackageService(Path.Combine(_root, "installed")).DiscoverInstalled();

        Assert.Empty(discovered);
    }

    private string CreatePackage(string name, IReadOnlyDictionary<string, string> entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    private const string CompleteThemeJson = """
        {
          "BrandColor":"#55613C", "BgColor":"#EEE9D8", "PanelColor":"#F7F2E5",
          "TextColor":"#2D271D", "BodyTextColor":"#433A2B", "MutedColor":"#766B58",
          "DisabledTextColor":"#A39A8B", "BorderColor":"#776A53", "EditorFillColor":"#FFFAEE",
          "InputFillColor":"#FFFAEE", "InputBorderColor":"#92836A", "FocusBorderColor":"#55613C",
          "DockColor":"#E5DCC8", "CompanionSurfaceColor":"#2D271D", "CompanionFaceColor":"#F7F2E5",
          "CompanionAccentColor":"#7A844D", "CompanionBorderColor":"#4E4434",
          "WindowRadius":12, "InputRadius":9, "ButtonRadius":8, "SmallRadius":6, "CardRadius":10,
          "SkinCompanionSize":44, "SkinTitleBarHeight":52, "SkinToolButtonSize":26,
          "SkinIconStrokeWidth":1.5, "SkinControlHeight":34, "SkinContentSpacing":10,
          "SkinCompanionOverscan":1.18, "SkinCompanionOffsetY":-13
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
