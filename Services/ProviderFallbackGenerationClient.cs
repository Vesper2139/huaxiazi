using System;
using System.Threading;
using System.Threading.Tasks;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>
/// Retries on one explicitly configured backup after a provider generation failure.
/// The backup becomes the sole client for the rest of the current task so repair calls
/// cannot silently switch back to the failed provider.
/// </summary>
internal sealed class ProviderFallbackGenerationClient : ITextGenerationClient, IStructuredTextGenerationClient, IDisposable
{
    private readonly ITextGenerationClient _primary;
    private readonly ProviderProfile? _fallbackProfile;
    private readonly Func<ProviderProfile, ITextGenerationClient> _fallbackFactory;
    private readonly Action<ProviderProfile> _onFallbackSelected;
    private ITextGenerationClient? _fallback;
    private bool _fallbackSelected;

    public ProviderFallbackGenerationClient(
        ITextGenerationClient primary,
        ProviderProfile? fallbackProfile,
        Func<ProviderProfile, ITextGenerationClient> fallbackFactory,
        Action<ProviderProfile> onFallbackSelected)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallbackProfile = fallbackProfile;
        _fallbackFactory = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));
        _onFallbackSelected = onFallbackSelected ?? throw new ArgumentNullException(nameof(onFallbackSelected));
    }

    public Task<string> GenerateAsync(string systemPrompt, string userInput, CancellationToken cancellationToken = default) =>
        ExecuteAsync(client => client.GenerateAsync(systemPrompt, userInput, cancellationToken), cancellationToken);

    public Task<string> GenerateStructuredAsync(string systemPrompt, string userInput, string jsonSchema, CancellationToken cancellationToken = default) =>
        ExecuteAsync(client => client is IStructuredTextGenerationClient structured
            ? structured.GenerateStructuredAsync(systemPrompt, userInput, jsonSchema, cancellationToken)
            : client.GenerateAsync(systemPrompt, userInput, cancellationToken), cancellationToken);

    private async Task<string> ExecuteAsync(Func<ITextGenerationClient, Task<string>> operation, CancellationToken cancellationToken)
    {
        if (_fallbackSelected) return await operation(_fallback!).ConfigureAwait(false);

        try
        {
            return await operation(_primary).ConfigureAwait(false);
        }
        catch (GenerationFailureException exception) when (
            exception.Kind != GenerationFailureKind.Refused &&
            exception.Kind != GenerationFailureKind.StructuredOutputUnsupported &&
            _fallbackProfile is not null && !cancellationToken.IsCancellationRequested)
        {
            _fallback = _fallbackFactory(_fallbackProfile);
            _fallbackSelected = true;
            _onFallbackSelected(_fallbackProfile);
            return await operation(_fallback).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_primary is IDisposable primaryDisposable) primaryDisposable.Dispose();
        if (_fallback is IDisposable fallbackDisposable && !ReferenceEquals(_fallback, _primary)) fallbackDisposable.Dispose();
    }
}
