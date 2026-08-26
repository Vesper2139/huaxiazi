using System;
using System.IO;
using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class VectorSkinTests
{
    [Fact]
    public void LuoXiaoHeiVectorManifestContainsAllLayeredStatesAndFrames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Skins", "LuoXiaoHei", "vector.json");
        using var stream = File.OpenRead(path);
        var manifest = VectorCharacterManifest.Parse(stream);

        Assert.Equal(88, manifest.Canvas.Width);
        Assert.Equal(88, manifest.Canvas.Height);
        Assert.Contains(manifest.Layers, layer => layer.Id == "leftPupil" && layer.Gaze);
        Assert.Equal(Enum.GetNames<CompanionVisualState>().Length, manifest.Animations.Count);
        Assert.All(manifest.Animations.Values, clip => Assert.True(clip.Frames.Count >= 3));
    }

    [Fact]
    public void VectorManifestRejectsNonMonotonicKeyframes()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
        {
          "schemaVersion":1,
          "canvas":{"width":88,"height":88},
          "anchor":{"x":44,"y":44},
          "layers":[
            {"id":"body","type":"ellipse","bounds":[8,8,72,72],"fill":"#000000"},
            {"id":"leftPupil","type":"ellipse","bounds":[20,30,10,15],"fill":"#FFFFFF","gaze":true},
            {"id":"rightPupil","type":"ellipse","bounds":[58,30,10,15],"fill":"#FFFFFF","gaze":true},
            {"id":"mouth","type":"path","path":"M40 50 L48 50","stroke":"#FFFFFF","strokeWidth":1}
          ],
          "animations":{"Idle":{"durationMs":100,"frames":[
            {"timeMs":0},{"timeMs":80},{"timeMs":70},{"timeMs":100}
          ]}}
        }
        """));

        Assert.Throws<InvalidDataException>(() => VectorCharacterManifest.Parse(stream));
    }
}
