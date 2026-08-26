using System.Text.Json.Serialization;

namespace Huaxiazi.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ApplicationMode>))]
public enum ApplicationMode
{
    Polish,
    PromptOptimize
}
