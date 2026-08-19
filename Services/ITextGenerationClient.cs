using System.Threading;
using System.Threading.Tasks;

namespace PromptFloat.Services;

public interface ITextGenerationClient
{
    Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default);
}
