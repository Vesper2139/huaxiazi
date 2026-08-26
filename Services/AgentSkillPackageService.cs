using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using PromptFloat.Models;

namespace PromptFloat.Services;

/// <summary>Imports Agent Skills as inert prompt-only snapshots. It never executes package content.</summary>
public sealed class AgentSkillPackageService
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".txt", ".json", ".yaml", ".yml", ".csv" };
    private readonly string _installRoot;

    public AgentSkillPackageService(string installRoot)
    {
        _installRoot = Path.GetFullPath(installRoot ?? throw new ArgumentNullException(nameof(installRoot)));
        Directory.CreateDirectory(_installRoot);
    }

    public IReadOnlyList<AgentSkillCandidate> Inspect(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Skill source is required.", nameof(sourcePath));
        var source = Path.GetFullPath(sourcePath);
        if (File.Exists(source) && string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
            return InspectZip(source);
        if (File.Exists(source) && string.Equals(Path.GetFileName(source), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            return [InspectDirectory(Path.GetDirectoryName(source)!, source)];
        if (Directory.Exists(source))
        {
            var direct = Path.Combine(source, "SKILL.md");
            if (File.Exists(direct)) return [InspectDirectory(source, source)];
            return Directory.EnumerateFiles(source, "SKILL.md", SearchOption.AllDirectories)
                .Select(file => InspectDirectory(Path.GetDirectoryName(file)!, source)).ToArray();
        }
        throw new FileNotFoundException("找不到 Skill 文件、目录或 ZIP。", source);
    }

    public AgentSkillInstallation Install(AgentSkillCandidate candidate, ApplicationMode mode)
        => Install(candidate, mode, AgentSkillSource.User, enabled: true, candidate.Name, null);

    private AgentSkillInstallation Install(AgentSkillCandidate candidate, ApplicationMode mode, AgentSkillSource source, bool enabled, string installId, SkillPresetManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.CanEnable) throw new InvalidOperationException("此 Skill 依赖工具或需要安全复核，不能启用。\n它可以保留为审查记录，但不能进入表达流水线。");
        if (candidate.Modes.Count > 0 && !candidate.Modes.Contains(mode)) throw new InvalidOperationException("所选模式与 Skill 声明不兼容。");
        var target = SafeChild(_installRoot, installId);
        var staging = SafeChild(_installRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        CopySupportedFiles(candidate.PackageRoot, staging);
        WriteInstallMetadata(staging, mode, candidate.Sha256, source, enabled, installId, manifest);
        if (Directory.Exists(target))
        {
            var backup = SafeChild(_installRoot, ".backup-" + Guid.NewGuid().ToString("N"));
            Directory.Move(target, backup);
            try { Directory.Move(staging, target); Directory.Delete(backup, true); }
            catch { if (Directory.Exists(target)) Directory.Delete(target, true); Directory.Move(backup, target); throw; }
        }
        else Directory.Move(staging, target);
        CleanupInspectionSnapshot(candidate.PackageRoot);
        return new AgentSkillInstallation { Name = installId, InstallPath = target, Sha256 = candidate.Sha256, Mode = mode };
    }

    public IReadOnlyList<AgentSkillRecord> ImportPresets(string presetRoot)
    {
        var root = Path.GetFullPath(presetRoot ?? throw new ArgumentNullException(nameof(presetRoot)));
        if (!Directory.Exists(root)) return [];
        var catalogPath = Path.Combine(root, "catalog.json");
        if (File.Exists(catalogPath))
        {
            var catalog = JsonSerializer.Deserialize<SkillPresetCatalog>(File.ReadAllText(catalogPath, Encoding.UTF8), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }) ?? throw new InvalidDataException("Skill 预置清单无效。");
            if (catalog.SchemaVersion != 1) throw new InvalidDataException("不支持的 Skill 预置清单版本。");
            foreach (var manifest in catalog.Skills)
            {
                if (!Regex.IsMatch(manifest.Id, "^[a-z0-9]+(?:-[a-z0-9]+)*$") || string.IsNullOrWhiteSpace(manifest.PackagePath))
                    throw new InvalidDataException("Skill 预置清单包含无效标识。");
                var directory = Path.GetFullPath(Path.Combine(root, manifest.PackagePath));
                if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(directory, "SKILL.md")))
                    throw new InvalidDataException("Skill 预置路径越界或缺少 SKILL.md。");
                var candidate = InspectDirectory(directory, directory);
                if (!candidate.CanEnable) continue;
                var target = SafeChild(_installRoot, manifest.Id);
                var enabled = manifest.DefaultEnabled;
                if (Directory.Exists(target))
                {
                    var existing = RequireInstalled(manifest.Id);
                    if (existing.Source != AgentSkillSource.Preset) continue;
                    enabled = existing.IsEnabled;
                    if (string.Equals(existing.PackageSha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.SourceRevision, manifest.SourceRevision, StringComparison.Ordinal)) continue;
                }
                Install(candidate, manifest.Mode, AgentSkillSource.Preset, enabled, manifest.Id, manifest);
            }
            RetireLegacyPreset("text-polisher");
            RetireLegacyPreset("prompt-optimizer");
            return ListInstalled().Where(item => item.Source == AgentSkillSource.Preset).ToArray();
        }
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            var skillFile = Path.Combine(directory, "SKILL.md");
            if (!File.Exists(skillFile)) continue;
            var candidate = InspectDirectory(directory, directory);
            if (!candidate.CanEnable || candidate.Modes.Count != 1) continue;
            var target = SafeChild(_installRoot, candidate.Name);
            if (!Directory.Exists(target))
                Install(candidate, candidate.Modes[0], AgentSkillSource.Preset, enabled: true, candidate.Name, null);
            else
            {
                var existing = RequireInstalled(candidate.Name);
                if (existing.Source == AgentSkillSource.Preset &&
                    !string.Equals(existing.PackageSha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                    Install(candidate, candidate.Modes[0], AgentSkillSource.Preset, existing.IsEnabled, candidate.Name, null);
            }
        }
        return ListInstalled().Where(item => item.Source == AgentSkillSource.Preset).ToArray();
    }

    public IReadOnlyList<AgentSkillRecord> ListInstalled()
    {
        if (!Directory.Exists(_installRoot)) return [];
        var result = new List<AgentSkillRecord>();
        foreach (var directory in Directory.EnumerateDirectories(_installRoot)
                     .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var candidate = InspectDirectory(directory, directory);
                result.Add(ReadRecord(directory, candidate));
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    public void SetEnabled(string name, bool enabled)
    {
        var record = RequireInstalled(name);
        WriteInstallMetadata(record.Candidate.PackageRoot, record.Mode, record.Candidate.Sha256, record.Source, enabled, record.Id, ToManifest(record));
    }

    public AgentSkillRecord CloneForEditing(string name, string cloneName)
    {
        var source = RequireInstalled(name);
        if (string.IsNullOrWhiteSpace(cloneName) || !Regex.IsMatch(cloneName, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
            throw new ArgumentException("自定义 Skill 名称必须使用小写字母、数字和连字符。", nameof(cloneName));
        var target = SafeChild(_installRoot, cloneName);
        if (Directory.Exists(target)) throw new InvalidOperationException("同名 Skill 已存在。");
        CopySupportedFiles(source.Candidate.PackageRoot, target);
        var skillFile = Path.Combine(target, "SKILL.md");
        var content = File.ReadAllText(skillFile, Encoding.UTF8);
        content = new Regex("(?m)^name:\\s*.*$").Replace(content, "name: " + cloneName, 1);
        File.WriteAllText(skillFile, content, Encoding.UTF8);
        var candidate = InspectDirectory(target, target);
        WriteInstallMetadata(target, source.Mode, candidate.Sha256, AgentSkillSource.User, enabled: true, cloneName, new SkillPresetManifest
        {
            Id = cloneName,
            DisplayName = cloneName,
            Mode = source.Mode,
            RoutingTags = source.RoutingTags
        });
        return RequireInstalled(cloneName);
    }

    public void SaveInstructions(string name, string instructions)
    {
        var record = RequireInstalled(name);
        if (!record.CanEdit) throw new InvalidOperationException("默认 Skill 为只读；请先创建自定义副本。");
        if (string.IsNullOrWhiteSpace(instructions)) throw new ArgumentException("Skill 指令不能为空。", nameof(instructions));
        var skillFile = Path.Combine(record.Candidate.PackageRoot, "SKILL.md");
        var content = File.ReadAllText(skillFile, Encoding.UTF8).Replace("\r\n", "\n", StringComparison.Ordinal);
        var end = content.StartsWith("---", StringComparison.Ordinal) ? content.IndexOf("\n---", 3, StringComparison.Ordinal) : -1;
        var updated = end >= 0 ? content[..(end + 4)].TrimEnd() + "\n\n" + instructions.Trim() + "\n" : instructions.Trim() + "\n";
        File.WriteAllText(skillFile, updated, Encoding.UTF8);
        var candidate = InspectDirectory(record.Candidate.PackageRoot, record.Candidate.PackageRoot);
        WriteInstallMetadata(record.Candidate.PackageRoot, record.Mode, candidate.Sha256, record.Source, record.IsEnabled, record.Id, ToManifest(record));
    }

    public void Remove(string name)
    {
        var record = RequireInstalled(name);
        if (!record.CanDelete) throw new InvalidOperationException("默认 Skill 不能删除，只能停用。");
        Directory.Delete(record.Candidate.PackageRoot, recursive: true);
    }

    public string PreserveForReview(AgentSkillCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Status is not (SkillCompatibilityStatus.RequiresTools or SkillCompatibilityStatus.ReviewRequired))
            throw new InvalidOperationException("只有被安全隔离的 Skill 才需要保留审查副本。");
        var reviewRoot = SafeChild(_installRoot, ".review");
        Directory.CreateDirectory(reviewRoot);
        var target = Path.GetFullPath(Path.Combine(reviewRoot, candidate.Name + "-" + candidate.Sha256[..12]));
        if (!target.StartsWith(reviewRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("审查目录越界。");
        if (!Directory.Exists(target)) CopyAllFiles(candidate.PackageRoot, target);
        CleanupInspectionSnapshot(candidate.PackageRoot);
        return target;
    }

    public AgentSkillCandidate? LoadInstalled(string name, ApplicationMode mode)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var directory = SafeChild(_installRoot, name);
        if (!Directory.Exists(directory)) return null;
        var candidate = InspectDirectory(directory, directory);
        try
        {
            var record = ReadRecord(directory, candidate);
            return record.IsEnabled && record.Mode == mode ? candidate : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidDataException) { return null; }
    }

    public void Export(string name, string destinationZip)
    {
        var source = SafeChild(_installRoot, name);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("未找到已安装的 Skill。\n" + source);
        var output = Path.GetFullPath(destinationZip);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (File.Exists(output)) File.Delete(output);
        ZipFile.CreateFromDirectory(source, output, CompressionLevel.Optimal, includeBaseDirectory: true);
    }

    private AgentSkillRecord RequireInstalled(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Skill 名称不能为空。", nameof(name));
        var directory = SafeChild(_installRoot, name);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("未找到已安装的 Skill。");
        return ReadRecord(directory, InspectDirectory(directory, directory));
    }

    private static AgentSkillRecord ReadRecord(string directory, AgentSkillCandidate candidate)
    {
        var mode = candidate.Modes.Count == 1 ? candidate.Modes[0] : candidate.SuggestedMode;
        var source = AgentSkillSource.User;
        var enabled = true;
        var packageSha256 = candidate.Sha256;
        var manifest = Path.Combine(directory, ".vesper.json");
        if (File.Exists(manifest))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            var root = document.RootElement;
            if (root.TryGetProperty("enabled", out var enabledValue)) enabled = enabledValue.GetBoolean();
            if (root.TryGetProperty("mode", out var modeValue) && Enum.TryParse<ApplicationMode>(modeValue.GetString(), true, out var parsedMode)) mode = parsedMode;
            if (root.TryGetProperty("source", out var sourceValue) && Enum.TryParse<AgentSkillSource>(sourceValue.GetString(), true, out var parsedSource)) source = parsedSource;
            if (root.TryGetProperty("sha256", out var hashValue) && !string.IsNullOrWhiteSpace(hashValue.GetString())) packageSha256 = hashValue.GetString()!;
        }
        var id = Path.GetFileName(directory);
        var displayName = string.Empty;
        var displayDescription = string.Empty;
        var sourceRepository = string.Empty;
        var sourceRevision = string.Empty;
        var manifestLicense = string.Empty;
        IReadOnlyList<string> routingTags = [];
        IReadOnlyList<string> excludedSections = [];
        if (File.Exists(manifest))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var idValue) && !string.IsNullOrWhiteSpace(idValue.GetString())) id = idValue.GetString()!;
            if (root.TryGetProperty("displayName", out var displayValue)) displayName = displayValue.GetString() ?? string.Empty;
            if (root.TryGetProperty("displayDescription", out var descriptionValue)) displayDescription = descriptionValue.GetString() ?? string.Empty;
            if (root.TryGetProperty("sourceRepository", out var repositoryValue)) sourceRepository = repositoryValue.GetString() ?? string.Empty;
            if (root.TryGetProperty("sourceRevision", out var revisionValue)) sourceRevision = revisionValue.GetString() ?? string.Empty;
            if (root.TryGetProperty("license", out var licenseValue)) manifestLicense = licenseValue.GetString() ?? string.Empty;
            if (root.TryGetProperty("routingTags", out var tagsValue) && tagsValue.ValueKind == JsonValueKind.Array)
                routingTags = tagsValue.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray();
            if (root.TryGetProperty("excludedSections", out var exclusionsValue) && exclusionsValue.ValueKind == JsonValueKind.Array)
                excludedSections = exclusionsValue.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray();
        }
        return new AgentSkillRecord
        {
            Candidate = candidate,
            Source = source,
            Mode = mode,
            IsEnabled = enabled,
            PackageSha256 = packageSha256,
            Id = id,
            DisplayName = displayName,
            DisplayDescription = displayDescription,
            SourceRepository = sourceRepository,
            SourceRevision = sourceRevision,
            ManifestLicense = manifestLicense,
            RoutingTags = routingTags,
            ExcludedSections = excludedSections
        };
    }

    private static void WriteInstallMetadata(string directory, ApplicationMode mode, string sha256, AgentSkillSource source, bool enabled, string id, SkillPresetManifest? manifest)
    {
        var json = JsonSerializer.Serialize(new
        {
            mode = mode.ToString(),
            sha256,
            source = source.ToString(),
            importedAt = DateTimeOffset.UtcNow,
            enabled,
            id,
            displayName = manifest?.DisplayName ?? string.Empty,
            displayDescription = manifest?.DisplayDescription ?? string.Empty,
            sourceRepository = manifest?.SourceRepository ?? string.Empty,
            sourceRevision = manifest?.SourceRevision ?? string.Empty,
            license = manifest?.License ?? string.Empty,
            routingTags = manifest?.RoutingTags ?? [],
            excludedSections = manifest?.ExcludedSections ?? []
        });
        File.WriteAllText(Path.Combine(directory, ".vesper.json"), json, Encoding.UTF8);
    }

    private IReadOnlyList<AgentSkillCandidate> InspectZip(string zipPath)
    {
        var stagingRoot = SafeChild(_installRoot, ".inspect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count > 500) throw new InvalidDataException("Skill ZIP 文件数量超出安全限制。");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                total += entry.Length;
                if (total > 20 * 1024 * 1024) throw new InvalidDataException("Skill ZIP 解压大小超出安全限制。");
                var destination = Path.GetFullPath(Path.Combine(stagingRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(stagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Skill ZIP 包含越界路径。");
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
            var candidates = Directory.EnumerateFiles(stagingRoot, "SKILL.md", SearchOption.AllDirectories)
                .Select(file => InspectDirectory(Path.GetDirectoryName(file)!, zipPath)).ToArray();
            if (candidates.Length == 0) throw new InvalidDataException("ZIP 中没有标准 SKILL.md。");
            // Candidate package roots must remain available for a subsequent Install call.
            return candidates;
        }
        catch
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, true);
            throw;
        }
    }

    private AgentSkillCandidate InspectDirectory(string packageRoot, string sourcePath)
    {
        var skillFile = Path.Combine(packageRoot, "SKILL.md");
        var content = File.ReadAllText(skillFile, Encoding.UTF8);
        var (metadata, body) = ParseFrontMatter(content);
        metadata.TryGetValue("name", out var name);
        metadata.TryGetValue("description", out var description);
        metadata.TryGetValue("license", out var license);
        metadata.TryGetValue("metadata.author", out var author);
        metadata.TryGetValue("metadata.version", out var version);
        metadata.TryGetValue("metadata.vesper.modes", out var modeValue);
        metadata.TryGetValue("allowed-tools", out var allowedTools);
        var validName = !string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, "^[a-z0-9]+(?:-[a-z0-9]+)*$");
        var modes = ParseModes(modeValue);
        var hasTools = !string.IsNullOrWhiteSpace(allowedTools) || Directory.Exists(Path.Combine(packageRoot, "scripts"));
        var unknownFiles = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Any(path => !AllowedExtensions.Contains(Path.GetExtension(path)));
        var status = !validName || string.IsNullOrWhiteSpace(description)
            ? SkillCompatibilityStatus.Invalid
            : hasTools ? SkillCompatibilityStatus.RequiresTools
            : unknownFiles ? SkillCompatibilityStatus.ReviewRequired
            : modes.Count == 0 ? SkillCompatibilityStatus.NeedsMapping
            : SkillCompatibilityStatus.Ready;
        return new AgentSkillCandidate
        {
            Name = name?.Trim() ?? string.Empty,
            Description = description?.Trim() ?? string.Empty,
            Author = author?.Trim() ?? string.Empty,
            Version = version?.Trim(' ', '"', '\'') ?? string.Empty,
            License = license?.Trim() ?? string.Empty,
            SourcePath = sourcePath,
            PackageRoot = packageRoot,
            Instructions = (body.Trim() + BuildReferenceSection(packageRoot)).Trim(),
            Sha256 = ComputeHash(packageRoot),
            Status = status,
            Modes = modes,
            SuggestedMode = SuggestMode(description, body)
        };
    }

    private static (Dictionary<string, string> Metadata, string Body) ParseFrontMatter(string content)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!content.StartsWith("---", StringComparison.Ordinal)) return (values, content);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (values, content);
        string section = normalized[3..end];
        string parent = string.Empty;
        foreach (var raw in section.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#')) continue;
            var indent = raw.Length - raw.TrimStart().Length;
            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            var key = raw[..colon].Trim();
            var value = raw[(colon + 1)..].Trim().Trim('"', '\'');
            if (indent == 0) { parent = string.IsNullOrWhiteSpace(value) ? key : string.Empty; if (!string.IsNullOrWhiteSpace(value)) values[key] = value; }
            else if (!string.IsNullOrWhiteSpace(parent)) values[parent + "." + key] = value;
        }
        return (values, normalized[(end + 4)..]);
    }

    private static IReadOnlyList<ApplicationMode> ParseModes(string? value)
    {
        var result = new List<ApplicationMode>();
        foreach (var item in (value ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (item.Equals("polish", StringComparison.OrdinalIgnoreCase)) result.Add(ApplicationMode.Polish);
            if (item.Equals("prompt", StringComparison.OrdinalIgnoreCase) || item.Equals("prompt-optimize", StringComparison.OrdinalIgnoreCase)) result.Add(ApplicationMode.PromptOptimize);
        }
        return result.Distinct().ToArray();
    }

    private static ApplicationMode SuggestMode(string? description, string body)
    {
        var text = (description ?? string.Empty) + " " + body;
        return text.Contains("prompt", StringComparison.OrdinalIgnoreCase) ? ApplicationMode.PromptOptimize : ApplicationMode.Polish;
    }

    private static string BuildReferenceSection(string packageRoot)
    {
        var builder = new StringBuilder();
        var remaining = 16_000;
        foreach (var file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(Path.GetFileName(path), ".vesper.json", StringComparison.OrdinalIgnoreCase)
                                    && !Path.GetFileName(path).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
                                    && AllowedExtensions.Contains(Path.GetExtension(path)))
                     .OrderBy(path => Path.GetRelativePath(packageRoot, path), StringComparer.Ordinal)
                     .Take(8))
        {
            if (remaining <= 0) break;
            string content;
            try { content = File.ReadAllText(file, Encoding.UTF8); } catch { continue; }
            if (content.Length > remaining) content = content[..remaining];
            remaining -= content.Length;
            builder.Append("\n\n[参考文件：").Append(Path.GetRelativePath(packageRoot, file).Replace('\\', '/')).AppendLine("]");
            builder.Append(content.Replace("</external_expression_strategy>", "[结束标记已转义]", StringComparison.OrdinalIgnoreCase));
        }
        return builder.ToString();
    }

    private static string SafeChild(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出 Skill 数据目录。");
        return path;
    }

    private static void CopySupportedFiles(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (!AllowedExtensions.Contains(Path.GetExtension(file))) continue;
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.GetFullPath(Path.Combine(target, relative));
            if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Skill 包含越界路径。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static void CopyAllFiles(string source, string target)
    {
        Directory.CreateDirectory(target);
        long total = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Skill 包含不允许的链接文件。");
            if (++count > 500) throw new InvalidDataException("Skill 文件数量超出安全限制。");
            total += new FileInfo(file).Length;
            if (total > 20 * 1024 * 1024) throw new InvalidDataException("Skill 大小超出安全限制。");
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.GetFullPath(Path.Combine(target, relative));
            if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Skill 包含越界路径。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }

    private static string ComputeHash(string root)
    {
        using var sha = SHA256.Create();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(Path.GetFileName(path), ".vesper.json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal))
        {
            var relative = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file).Replace('\\', '/'));
            sha.TransformBlock(relative, 0, relative.Length, null, 0);
            var bytes = File.ReadAllBytes(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static SkillPresetManifest ToManifest(AgentSkillRecord record) => new()
    {
        Id = record.Id,
        DisplayName = record.EffectiveDisplayName,
        DisplayDescription = record.EffectiveDescription,
        Mode = record.Mode,
        SourceRepository = record.SourceRepository,
        SourceRevision = record.SourceRevision,
        License = record.License,
        RoutingTags = record.RoutingTags,
        ExcludedSections = record.ExcludedSections
    };

    private void RetireLegacyPreset(string id)
    {
        var directory = SafeChild(_installRoot, id);
        if (!Directory.Exists(directory)) return;
        try
        {
            if (RequireInstalled(id).Source == AgentSkillSource.Preset) Directory.Delete(directory, recursive: true);
        }
        catch (InvalidDataException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void CleanupInspectionSnapshot(string packageRoot)
    {
        try
        {
            var full = Path.GetFullPath(packageRoot);
            if (full.StartsWith(_installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetRelativePath(_installRoot, full).Split(Path.DirectorySeparatorChar)[0].StartsWith(".inspect-", StringComparison.Ordinal))
            {
                var inspectionRoot = Path.Combine(_installRoot, Path.GetRelativePath(_installRoot, full).Split(Path.DirectorySeparatorChar)[0]);
                if (Directory.Exists(inspectionRoot)) Directory.Delete(inspectionRoot, true);
            }
        }
        catch { }
    }
}
