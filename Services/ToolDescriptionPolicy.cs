using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Huaxiazi.Services;

public sealed record ToolParameterDescription(string Name, string Type, string Description, bool Required = false);
public sealed record ToolDescription(string Name, string Description, AgentToolSafety Safety, IReadOnlyList<ToolParameterDescription> Parameters);
public sealed record SafeToolDescription(string Name, string Description, AgentToolSafety Safety, IReadOnlyList<ToolParameterDescription> Parameters);

public static class ToolDescriptionPolicy
{
    public static SafeToolDescription Project(ToolDescription source, int maxDescriptionCharacters = 1_000)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Regex.IsMatch(source.Name ?? string.Empty, "^[a-zA-Z][a-zA-Z0-9_-]{0,63}$")) throw new ArgumentException("invalid_tool_name", nameof(source));
        if (maxDescriptionCharacters is < 100 or > 8_000) throw new ArgumentOutOfRangeException(nameof(maxDescriptionCharacters));
        var description = PromptInjectionSanitizer.ReplaceUnsafePhrases(source.Description);
        if (description.Length > maxDescriptionCharacters) description = description[..maxDescriptionCharacters].TrimEnd() + "…";
        var rawParameters = source.Parameters ?? [];
        if (rawParameters.Any(parameter => !Regex.IsMatch(parameter.Name ?? string.Empty, "^[a-zA-Z][a-zA-Z0-9_-]{0,63}$")))
            throw new ArgumentException("invalid_parameter_name", nameof(source));
        if (rawParameters.GroupBy(parameter => parameter.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new ArgumentException("duplicate_parameter_name", nameof(source));
        var parameters = rawParameters
            .Select(parameter => new ToolParameterDescription(parameter.Name, parameter.Type, Truncate(PromptInjectionSanitizer.ReplaceUnsafePhrases(parameter.Description), 500), parameter.Required)).ToArray();
        if (parameters.Any(parameter => string.IsNullOrWhiteSpace(parameter.Type))) throw new ArgumentException("parameter_type_missing", nameof(source));
        return new SafeToolDescription(source.Name!.Trim(), description, source.Safety, parameters);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
