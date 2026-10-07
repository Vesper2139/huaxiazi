using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Huaxiazi.Models;

[JsonConverter(typeof(JsonStringEnumConverter<LocalModelState>))]
public enum LocalModelState
{
    NotInstalled,
    Downloading,
    Verifying,
    Available,
    Corrupted,
    Incompatible,
    Running,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter<LocalGpuMode>))]
public enum LocalGpuMode
{
    Auto,
    Off
}

public sealed class LocalRuntimeOptions
{
    public int ContextSize { get; set; } = 4096;
    public int CpuThreads { get; set; }
    public LocalGpuMode GpuMode { get; set; } = LocalGpuMode.Auto;
    public int BatchSize { get; set; } = 512;
    public bool KeepLoaded { get; set; } = true;
    public double AdapterScale { get; set; } = 1.0;
    public int Seed { get; set; } = -1;
    public double RepeatPenalty { get; set; } = 1.05;

    public void Normalize()
    {
        ContextSize = Math.Clamp(ContextSize, 512, 131072);
        CpuThreads = Math.Clamp(CpuThreads, 0, 256);
        BatchSize = Math.Clamp(BatchSize, 32, 4096);
        AdapterScale = Math.Clamp(AdapterScale, 0, 2);
        RepeatPenalty = Math.Clamp(RepeatPenalty, 0.5, 2);
        if (!Enum.IsDefined(GpuMode)) GpuMode = LocalGpuMode.Auto;
    }
}

public sealed class LocalModelDescriptor
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public string FileName { get; set; } = "model.gguf";
    public string DownloadUrl { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int ContextTokens { get; set; } = 4096;
    public long MinimumMemoryBytes { get; set; }
    public string LicenseId { get; set; } = string.Empty;
    public string LicenseUrl { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public List<string> AllowedRedirectHosts { get; set; } = [];
}

public sealed class LocalModelCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public string CatalogVersion { get; set; } = string.Empty;
    public List<LocalModelDescriptor> Models { get; set; } = [];
    public List<LocalAdapterDescriptor> Adapters { get; set; } = [];
}

public sealed class LocalAdapterDescriptor
{
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string BaseModelSha256 { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public double DefaultScale { get; set; } = 1.0;
    public string EvaluationVersion { get; set; } = string.Empty;
}

public sealed class InstalledLocalModel
{
    public string InstallationId { get; set; } = string.Empty;
    public string CatalogId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public bool IsUserImported { get; set; }
    public LocalModelState State { get; set; } = LocalModelState.NotInstalled;
}

public sealed class InstalledLocalAdapter
{
    public string InstallationId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string BaseModelSha256 { get; set; } = string.Empty;
    public double DefaultScale { get; set; } = 1.0;
}

internal sealed class LocalModelRegistry
{
    public int SchemaVersion { get; set; } = 1;
    public List<InstalledLocalModel> Models { get; set; } = [];
    public List<InstalledLocalAdapter> Adapters { get; set; } = [];
}
