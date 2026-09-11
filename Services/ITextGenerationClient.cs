using System.Threading;
using System.Threading.Tasks;

namespace Huaxiazi.Services;

public interface ITextGenerationClient
{
    Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default);
}

public interface IStructuredTextGenerationClient
{
    Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default);
}
