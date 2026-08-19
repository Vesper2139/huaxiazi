using System.Text.Json.Serialization;

namespace PromptFloat.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ApplicationMode>))]
public enum ApplicationMode
{
    Polish,
    PromptOptimize
}
