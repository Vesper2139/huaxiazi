using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

internal enum ProviderCapabilitySupport
{
    Unknown,
    Supported,
    Unsupported
}

internal enum ProviderStructuredOutputSupport
{
    Unknown,
    JsonObjectOnly,
    JsonSchema
}

internal enum ProviderReasoningOutputPolicy
{
    Default,
    ExcludeWithIncludeReasoning,
    HideWithReasoningFormat
}

internal sealed record ProviderCapabilities(
    ProviderCapabilitySupport Temperature,
    ProviderCapabilitySupport TopP,
    bool UsesMaxCompletionTokens,
    IReadOnlySet<string> ReasoningEffortValues,
    string EvidenceId,
    ProviderStructuredOutputSupport StructuredOutput = ProviderStructuredOutputSupport.Unknown,
    bool UsesDeveloperRole = false,
    bool OmitSamplingParameters = false,
    double? MaximumTemperature = null,
    double? MaximumTopPExclusive = null,
    int? MaximumOutputTokens = null,
    double? MinimumTopP = null,
    double? MinimumTopPExclusive = null,
    ProviderReasoningOutputPolicy ReasoningOutputPolicy = ProviderReasoningOutputPolicy.Default,
    ProviderCapabilitySupport Streaming = ProviderCapabilitySupport.Unknown,
    ProviderCapabilitySupport ToolCalling = ProviderCapabilitySupport.Unknown,
    int? ContextWindowTokens = null,
    bool ToolCallingRequiresNoReasoningEffort = false,
    bool SupportsThinkingToggle = false);

/// <summary>Conservative, exact-model request capabilities backed by dated provider documentation.</summary>
internal static class ProviderCapabilityResolver
{
    private static readonly IReadOnlySet<string> Gpt6AstraReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high", "xhigh", "max"
    };

    private static readonly IReadOnlySet<string> Gpt6SolReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "none", "low", "medium", "high", "xhigh", "max"
    };

    private static readonly IReadOnlySet<string> OpenAiOSeriesReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high"
    };

    private static readonly IReadOnlySet<string> OpenAiOSeriesModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "o3", "o3-2025-04-16", "o4-mini", "o4-mini-2025-04-16"
    };

    private static readonly IReadOnlySet<string> OpenAiStructuredOutputModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-4o", "gpt-4o-mini"
    };

    private static readonly IReadOnlySet<string> AnthropicOpusReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high", "xhigh", "max"
    };

    private static readonly IReadOnlySet<string> AnthropicSonnet46ReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high", "max"
    };

    private static readonly IReadOnlySet<string> Gemini3ThinkingLevels = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high"
    };

    private static readonly IReadOnlySet<string> AlibabaQwen38ReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "xhigh"
    };

    private static readonly IReadOnlySet<string> VolcengineSeed20LiteReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high"
    };

    private static readonly IReadOnlySet<string> KimiK3ReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "high", "max"
    };

    private static readonly IReadOnlySet<string> ZhipuGlm53ReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "high", "max"
    };

    private static readonly IReadOnlySet<string> ZhipuGlm52ReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "none", "minimal", "low", "medium", "high", "xhigh", "max"
    };

    private static readonly IReadOnlySet<string> GroqReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high"
    };

    private static readonly IReadOnlySet<string> GroqReasoningModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "openai/gpt-oss-20b", "openai/gpt-oss-120b", "qwen/qwen3.8-27b"
    };

    private static readonly IReadOnlySet<string> DeepSeekChatModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Current API IDs. The V4 names below are explicit compatibility aliases documented by DeepSeek.
        "deepseek-flash", "deepseek-v4-pro", "deepseek-v4-flash", "deepseek-v4-flash-vision-exp"
    };

    private static readonly IReadOnlySet<string> DeepSeekReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "high", "max"
    };

    private static readonly IReadOnlyDictionary<string, int> SparkMaximumOutputTokens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["4.0Ultra"] = 32768,
        ["max-32k"] = 32768,
        ["generalv3.5"] = 8192,
        ["generalv3"] = 8192,
        ["pro-128k"] = 32768,
        ["lite"] = 4096
    };

    private static readonly IReadOnlySet<string> MistralReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high"
    };

    private static readonly IReadOnlySet<string> SiliconFlowReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "high", "max"
    };

    private static readonly IReadOnlySet<string> BaichuanSamplingModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Baichuan4-Turbo", "Baichuan4-Air", "Baichuan4", "Baichuan3-Turbo", "Baichuan3-Turbo-128k", "Baichuan2-Turbo"
    };

    private static readonly IReadOnlySet<string> BaichuanJsonModeModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Baichuan4-Turbo", "Baichuan4-Air", "Baichuan4", "Baichuan3-Turbo", "Baichuan3-Turbo-128k"
    };

    private static readonly IReadOnlySet<string> StepFunChatModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "step-5-preview", "step-3.7-flash", "step-3.5-flash", "step-3.5-flash-2603"
    };

    private static readonly IReadOnlySet<string> StepFunThreeLevelReasoningModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "step-5-preview", "step-3.7-flash"
    };

    private static readonly IReadOnlySet<string> MiniMaxCurrentOpenAiModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MiniMax-M3", "MiniMax-M2.7", "MiniMax-M2.7-highspeed"
    };

    private static readonly IReadOnlySet<string> TogetherStructuredOutputModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "thinkingmachines/Inkling",
        "meta-llama/Llama-3.3-70B-Instruct-Turbo",
        "Qwen/Qwen3.5-9B",
        "moonshotai/Kimi-K3",
        "zai-org/GLM-5.3-Flash",
        "zai-org/GLM-5.3",
        "zai-org/GLM-5.2",
        "deepseek-ai/DeepSeek-V4-Flash-0731",
        "deepseek-ai/DeepSeek-V4-Pro-0813",
        "deepseek-ai/DeepSeek-V4.1-Flash",
        "MiniMaxAI/MiniMax-M3",
        "openai/gpt-oss-120b"
    };

    private static readonly IReadOnlySet<string> MiMoCurrentOpenAiModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mimo-v2.5-pro", "mimo-v2.5"
    };

    private static readonly IReadOnlySet<string> MiMoV26OpenAiModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mimo-v2.6-pro", "mimo-v2.6-flash", "mimo-v2.6-pro-ultraspeed"
    };

    private static readonly IReadOnlySet<string> Grok47ReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "low", "medium", "high", "xhigh"
    };

    private static readonly IReadOnlySet<string> Grok43ReasoningEfforts = new HashSet<string>(StringComparer.Ordinal)
    {
        "none", "low", "medium", "high"
    };

    private static readonly IReadOnlySet<string> Grok43ModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "grok-4.3", "grok-4.3-latest"
    };

    private static readonly ProviderCapabilities Unknown = new(
        ProviderCapabilitySupport.Unknown,
        ProviderCapabilitySupport.Unknown,
        UsesMaxCompletionTokens: false,
        ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
        EvidenceId: "unknown");

    public static ProviderCapabilities Resolve(ProviderPlatform platform, ProviderProtocol protocol, string resolvedModel)
    {
        if (string.IsNullOrWhiteSpace(resolvedModel)) return Unknown;

        if (platform == ProviderPlatform.DeepSeek && protocol == ProviderProtocol.OpenAICompatible &&
            DeepSeekChatModelIds.Contains(resolvedModel))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: DeepSeekReasoningEfforts,
                EvidenceId: "deepseek-chat-api-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly,
                MaximumTemperature: 2.0,
                MinimumTopPExclusive: 0.0);
        }

        if (platform == ProviderPlatform.Baichuan && protocol == ProviderProtocol.OpenAICompatible &&
            BaichuanSamplingModels.Contains(resolvedModel))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "baichuan-chat-api-2026-10-07",
                StructuredOutput: BaichuanJsonModeModels.Contains(resolvedModel)
                    ? ProviderStructuredOutputSupport.JsonObjectOnly
                    : ProviderStructuredOutputSupport.Unknown,
                MaximumTemperature: 1.0,
                MaximumTopPExclusive: 1.0,
                MaximumOutputTokens: 2048);
        }

        if (platform == ProviderPlatform.StepFun && protocol == ProviderProtocol.OpenAICompatible &&
            StepFunChatModels.Contains(resolvedModel))
        {
            var isStep37Flash = string.Equals(resolvedModel, "step-3.7-flash", StringComparison.OrdinalIgnoreCase);
            var reasoningEfforts = StepFunThreeLevelReasoningModels.Contains(resolvedModel)
                ? new HashSet<string>(StringComparer.Ordinal) { "low", "medium", "high" }
                : string.Equals(resolvedModel, "step-3.5-flash-2603", StringComparison.OrdinalIgnoreCase)
                    ? new HashSet<string>(StringComparer.Ordinal) { "low", "high" }
                    : new HashSet<string>(StringComparer.Ordinal);

            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: reasoningEfforts,
                EvidenceId: isStep37Flash ? "stepfun-3.7-flash-model-2026-10-07" : "stepfun-chat-api-2026-10-07",
                StructuredOutput: string.Equals(resolvedModel, "step-5-preview", StringComparison.OrdinalIgnoreCase)
                    ? ProviderStructuredOutputSupport.JsonSchema
                    : ProviderStructuredOutputSupport.Unknown,
                MaximumTemperature: 2.0,
                MaximumOutputTokens: string.Equals(resolvedModel, "step-5-preview", StringComparison.OrdinalIgnoreCase)
                    ? 65536
                    : null,
                Streaming: isStep37Flash
                    ? ProviderCapabilitySupport.Supported
                    : ProviderCapabilitySupport.Unknown,
                ToolCalling: isStep37Flash
                    ? ProviderCapabilitySupport.Supported
                    : ProviderCapabilitySupport.Unknown,
                ContextWindowTokens: isStep37Flash ? 256_000 : null);
        }

        if (platform == ProviderPlatform.MiniMax && protocol == ProviderProtocol.OpenAICompatible &&
            MiniMaxCurrentOpenAiModels.Contains(resolvedModel))
        {
            var isMiniMaxM3 = string.Equals(resolvedModel, "MiniMax-M3", StringComparison.OrdinalIgnoreCase);
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: isMiniMaxM3,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "minimax-openai-chat-2026-10-07",
                MaximumTemperature: 2.0);
        }

        if (platform == ProviderPlatform.MiMo && protocol == ProviderProtocol.OpenAICompatible &&
            MiMoV26OpenAiModels.Contains(resolvedModel))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: true,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "xiaomi-mimo-v2.6-openai-chat-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly,
                MaximumTemperature: 1.5,
                MinimumTopP: 0.01);
        }

        if (platform == ProviderPlatform.MiMo && protocol == ProviderProtocol.OpenAICompatible &&
            MiMoCurrentOpenAiModels.Contains(resolvedModel))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: true,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "xiaomi-mimo-v2.5-openai-chat-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly,
                MaximumTemperature: 1.5,
                MinimumTopP: 0.01);
        }

        if (platform == ProviderPlatform.Together && protocol == ProviderProtocol.OpenAICompatible &&
            TogetherStructuredOutputModels.Contains(resolvedModel))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "together-serverless-chat-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonSchema,
                MaximumTemperature: 1.0);
        }

        if (platform == ProviderPlatform.Grok && protocol is ProviderProtocol.OpenAICompatible or ProviderProtocol.OpenAIResponses)
        {
            var reasoningEfforts = string.Equals(resolvedModel, "grok-4.7", StringComparison.OrdinalIgnoreCase)
                ? Grok47ReasoningEfforts
                : Grok43ModelIds.Contains(resolvedModel)
                    ? Grok43ReasoningEfforts
                    : null;
            if (reasoningEfforts is not null)
            {
                var isGrok47 = string.Equals(resolvedModel, "grok-4.7", StringComparison.OrdinalIgnoreCase);
                return new(
                    ProviderCapabilitySupport.Supported,
                    ProviderCapabilitySupport.Supported,
                    UsesMaxCompletionTokens: protocol == ProviderProtocol.OpenAICompatible,
                    ReasoningEffortValues: reasoningEfforts,
                    EvidenceId: isGrok47 ? "xai-grok-4.7-2026-10-07" : "xai-grok-4.3-2026-10-07",
                    StructuredOutput: ProviderStructuredOutputSupport.JsonSchema,
                    MaximumTemperature: 2.0);
            }
        }

        if (platform == ProviderPlatform.Yi && protocol == ProviderProtocol.OpenAICompatible &&
            IsExactModelId(resolvedModel, "yi-lightning", "yi-large"))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "yi-official-chat-api-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.Unknown);
        }

        if (platform == ProviderPlatform.OpenAI && protocol is ProviderProtocol.OpenAICompatible or ProviderProtocol.OpenAIResponses &&
            OpenAiStructuredOutputModelIds.Contains(resolvedModel))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: protocol == ProviderProtocol.OpenAICompatible,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "openai-gpt-4o-chat-responses-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonSchema,
                MaximumTemperature: 2.0,
                MaximumOutputTokens: 16_384,
                Streaming: ProviderCapabilitySupport.Supported,
                ToolCalling: ProviderCapabilitySupport.Supported,
                ContextWindowTokens: 128_000);
        }

        if (platform == ProviderPlatform.OpenAI && protocol is ProviderProtocol.OpenAICompatible or ProviderProtocol.OpenAIResponses &&
            OpenAiOSeriesModelIds.Contains(resolvedModel))
        {
            return new(
                ProviderCapabilitySupport.Unknown,
                ProviderCapabilitySupport.Unknown,
                UsesMaxCompletionTokens: protocol == ProviderProtocol.OpenAICompatible,
                ReasoningEffortValues: OpenAiOSeriesReasoningEfforts,
                EvidenceId: "openai-o-series-2026-10-06",
                StructuredOutput: ProviderStructuredOutputSupport.JsonSchema,
                UsesDeveloperRole: protocol == ProviderProtocol.OpenAICompatible,
                OmitSamplingParameters: true);
        }

        if (platform == ProviderPlatform.OpenAI && protocol is ProviderProtocol.OpenAICompatible or ProviderProtocol.OpenAIResponses &&
            string.Equals(resolvedModel, "gpt-6-astra", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProviderCapabilitySupport.Unsupported,
                ProviderCapabilitySupport.Unsupported,
                UsesMaxCompletionTokens: protocol == ProviderProtocol.OpenAICompatible,
                Gpt6AstraReasoningEfforts,
                EvidenceId: "openai-gpt-6-astra-2026-10-05",
                StructuredOutput: ProviderStructuredOutputSupport.JsonSchema,
                MaximumOutputTokens: 128_000,
                Streaming: ProviderCapabilitySupport.Supported,
                ToolCalling: protocol == ProviderProtocol.OpenAIResponses
                    ? ProviderCapabilitySupport.Supported
                    : ProviderCapabilitySupport.Unsupported,
                ContextWindowTokens: 1_050_000);
        }

        if (platform == ProviderPlatform.OpenAI && protocol is ProviderProtocol.OpenAICompatible or ProviderProtocol.OpenAIResponses)
        {
            var isGpt61Sol = string.Equals(resolvedModel, "gpt-6.1-sol", StringComparison.OrdinalIgnoreCase);
            var supportsNone = string.Equals(resolvedModel, "gpt-6-sol", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(resolvedModel, "gpt-6-luna", StringComparison.OrdinalIgnoreCase);
            if (isGpt61Sol || supportsNone)
            {
                return new(
                    ProviderCapabilitySupport.Unsupported,
                    ProviderCapabilitySupport.Unsupported,
                    UsesMaxCompletionTokens: protocol == ProviderProtocol.OpenAICompatible,
                    ReasoningEffortValues: supportsNone ? Gpt6SolReasoningEfforts : Gpt6AstraReasoningEfforts,
                    EvidenceId: "openai-gpt-6-family-2026-10-06",
                    StructuredOutput: ProviderStructuredOutputSupport.JsonSchema,
                    MaximumOutputTokens: 128_000,
                    Streaming: ProviderCapabilitySupport.Supported,
                    ToolCalling: protocol == ProviderProtocol.OpenAIResponses || supportsNone
                        ? ProviderCapabilitySupport.Supported
                        : ProviderCapabilitySupport.Unsupported,
                    ContextWindowTokens: 1_050_000,
                    ToolCallingRequiresNoReasoningEffort:
                        protocol == ProviderProtocol.OpenAICompatible && supportsNone);
            }
        }

        if (platform == ProviderPlatform.OpenAI && protocol == ProviderProtocol.OpenAIResponses &&
            string.Equals(resolvedModel, "gpt-4.1-mini", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "openai-responses-gpt-4.1-mini-2026-10-06",
                StructuredOutput: ProviderStructuredOutputSupport.JsonSchema);
        }

        if (platform == ProviderPlatform.OpenAI && protocol == ProviderProtocol.OpenAICompatible &&
            string.Equals(resolvedModel, "gpt-4.1-mini", StringComparison.OrdinalIgnoreCase))
        {
            return Unknown with
            {
                EvidenceId = "openai-gpt-4.1-mini-structured-outputs-2026-10-06",
                StructuredOutput = ProviderStructuredOutputSupport.JsonSchema
            };
        }

        if (platform == ProviderPlatform.Qwen && protocol == ProviderProtocol.OpenAICompatible &&
            IsExactModelId(resolvedModel,
                "qwen3.8-max", "qwen3.8-max-0902", "qwen3.8-flash", "qwen3.8-2.4t-a95b", "qwen3.8-27b"))
        {
            var isCompletionTokenModel = IsExactModelId(resolvedModel,
                "qwen3.8-max", "qwen3.8-max-0902", "qwen3.8-flash");
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: isCompletionTokenModel,
                ReasoningEffortValues: AlibabaQwen38ReasoningEfforts,
                EvidenceId: "aliyun-qwen3.8-openai-compatible-2026-10-07",
                StructuredOutput: isCompletionTokenModel
                    ? ProviderStructuredOutputSupport.JsonSchema
                    : ProviderStructuredOutputSupport.JsonObjectOnly,
                MaximumTemperature: double.BitDecrement(2.0),
                MinimumTopPExclusive: 0.0);
        }

        if (platform == ProviderPlatform.Qwen && protocol == ProviderProtocol.OpenAICompatible &&
            IsExactModelId(resolvedModel, "qwen-plus", "qwen-turbo", "qwen-max", "qwen-long"))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "aliyun-qwen-openai-compatible-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly,
                MaximumTemperature: double.BitDecrement(2.0),
                MinimumTopPExclusive: 0.0);
        }

        if (platform == ProviderPlatform.Doubao && protocol == ProviderProtocol.OpenAICompatible &&
            IsExactModelOrSnapshot(resolvedModel, "doubao-seed-2-0-lite-260428"))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: VolcengineSeed20LiteReasoningEfforts,
                EvidenceId: "volcengine-doubao-seed-2-0-lite-260428-2026-10-06");
        }

        if (platform == ProviderPlatform.Doubao && protocol == ProviderProtocol.OpenAICompatible &&
            IsExactModelOrSnapshot(resolvedModel, "doubao-seed-2-0-lite-260215", "doubao-seed-2-0-pro-260215"))
        {
            return new(
                ProviderCapabilitySupport.Unsupported,
                ProviderCapabilitySupport.Unsupported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "volcengine-doubao-seed-2-0-260215-fixed-sampling-2026-10-06");
        }

        if (platform == ProviderPlatform.Kimi && protocol == ProviderProtocol.OpenAICompatible)
        {
            if (IsExactModelId(resolvedModel, "kimi-k3"))
            {
                return new(
                    ProviderCapabilitySupport.Unsupported,
                    ProviderCapabilitySupport.Unsupported,
                    UsesMaxCompletionTokens: false,
                    ReasoningEffortValues: KimiK3ReasoningEfforts,
                    EvidenceId: "moonshot-kimi-k3-chat-2026-10-06",
                    ContextWindowTokens: string.Equals(resolvedModel, "kimi-k3", StringComparison.OrdinalIgnoreCase)
                        ? 1_000_000
                        : null);
            }

            if (IsExactModelId(resolvedModel, "kimi-k2.6", "kimi-k2.7-code", "kimi-k2.7-code-highspeed"))
            {
                var exactContextWindow = resolvedModel.ToLowerInvariant() switch
                {
                    "kimi-k2.6" or "kimi-k2.7-code" or "kimi-k2.7-code-highspeed" => 256_000,
                    _ => (int?)null
                };
                return new(
                    ProviderCapabilitySupport.Unsupported,
                    ProviderCapabilitySupport.Unsupported,
                    UsesMaxCompletionTokens: false,
                    ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                    EvidenceId: "moonshot-kimi-k2-current-chat-2026-10-06",
                    ContextWindowTokens: exactContextWindow,
                    SupportsThinkingToggle: string.Equals(resolvedModel, "kimi-k2.6", StringComparison.OrdinalIgnoreCase));
            }
        }

        if (platform == ProviderPlatform.Zhipu && protocol == ProviderProtocol.OpenAICompatible)
        {
            if (IsExactModelOrSnapshot(resolvedModel, "glm-5.3"))
            {
                return new(
                    ProviderCapabilitySupport.Supported,
                    ProviderCapabilitySupport.Supported,
                    UsesMaxCompletionTokens: false,
                    ReasoningEffortValues: ZhipuGlm53ReasoningEfforts,
                    EvidenceId: "zhipu-glm-5.3-chat-2026-10-06",
                    StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly);
            }

            if (IsExactModelOrSnapshot(resolvedModel, "glm-5.2"))
            {
                return new(
                    ProviderCapabilitySupport.Supported,
                    ProviderCapabilitySupport.Supported,
                    UsesMaxCompletionTokens: false,
                    ReasoningEffortValues: ZhipuGlm52ReasoningEfforts,
                    EvidenceId: "zhipu-glm-5.2-chat-2026-10-06",
                    StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly);
            }
        }

        if (platform == ProviderPlatform.Spark && protocol == ProviderProtocol.OpenAICompatible &&
            SparkMaximumOutputTokens.TryGetValue(resolvedModel, out var sparkMaximumOutputTokens))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "xfyun-spark-openai-compatible-chat-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly,
                MaximumTemperature: 2.0,
                MaximumOutputTokens: sparkMaximumOutputTokens,
                MinimumTopPExclusive: 0.0);
        }

        if (platform == ProviderPlatform.SiliconFlow && protocol == ProviderProtocol.OpenAICompatible &&
            string.Equals(resolvedModel, "deepseek-ai/DeepSeek-V3", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "siliconflow-deepseek-v3-json-mode-2026-10-06",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly);
        }

        if (platform == ProviderPlatform.SiliconFlow && protocol == ProviderProtocol.OpenAICompatible &&
            string.Equals(resolvedModel, "Qwen/Qwen2.5-7B-Instruct", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "siliconflow-qwen2.5-7b-json-mode-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly,
                MaximumTemperature: 2.0);
        }

        if (platform == ProviderPlatform.SiliconFlow && protocol == ProviderProtocol.OpenAICompatible &&
            (string.Equals(resolvedModel, "Pro/deepseek-ai/DeepSeek-V4", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(resolvedModel, "deepseek-ai/DeepSeek-V4-Flash", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(resolvedModel, "Pro/zai-org/GLM-5.2", StringComparison.OrdinalIgnoreCase)))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: SiliconFlowReasoningEfforts,
                EvidenceId: "siliconflow-v4-glm52-thinking-json-mode-2026-10-06",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly);
        }

        if (platform == ProviderPlatform.Groq && protocol == ProviderProtocol.OpenAICompatible)
        {
            if (GroqReasoningModelIds.Contains(resolvedModel))
            {
                return new(
                    ProviderCapabilitySupport.Supported,
                    ProviderCapabilitySupport.Supported,
                    UsesMaxCompletionTokens: true,
                    ReasoningEffortValues: GroqReasoningEfforts,
                    EvidenceId: "groq-openai-compatible-reasoning-strict-schema-2026-10-06",
                    StructuredOutput: ProviderStructuredOutputSupport.JsonSchema,
                    ReasoningOutputPolicy: string.Equals(resolvedModel, "qwen/qwen3.8-27b", StringComparison.OrdinalIgnoreCase)
                        ? ProviderReasoningOutputPolicy.HideWithReasoningFormat
                        : ProviderReasoningOutputPolicy.ExcludeWithIncludeReasoning);
            }

            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: true,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "groq-openai-compatible-json-mode-2026-10-06",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly);
        }

        if (platform == ProviderPlatform.Mistral && protocol == ProviderProtocol.OpenAICompatible &&
            string.Equals(resolvedModel, "mistral-large-latest", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "mistral-large-latest-json-mode-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonObjectOnly);
        }

        if (platform == ProviderPlatform.Mistral && protocol == ProviderProtocol.OpenAICompatible &&
            string.Equals(resolvedModel, "ministral-8b-2512", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProviderCapabilitySupport.Supported,
                ProviderCapabilitySupport.Supported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "mistral-ministral-3-8b-structured-output-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonSchema);
        }

        if (platform == ProviderPlatform.Mistral && protocol == ProviderProtocol.OpenAICompatible)
        {
            if (IsExactModelOrSnapshot(resolvedModel, "mistral-small-latest", "mistral-medium-3-5"))
            {
                return new(
                    ProviderCapabilitySupport.Supported,
                    ProviderCapabilitySupport.Supported,
                    UsesMaxCompletionTokens: false,
                    ReasoningEffortValues: MistralReasoningEfforts,
                    EvidenceId: "mistral-chat-reasoning-2026-10-06",
                    StructuredOutput: ProviderStructuredOutputSupport.JsonSchema);
            }

            if (string.Equals(resolvedModel, "mistral-small-2603", StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    ProviderCapabilitySupport.Supported,
                    ProviderCapabilitySupport.Supported,
                    UsesMaxCompletionTokens: false,
                    ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                    EvidenceId: "mistral-small-4-structured-output-2026-10-07",
                    StructuredOutput: ProviderStructuredOutputSupport.JsonSchema,
                    ToolCalling: ProviderCapabilitySupport.Supported,
                    ContextWindowTokens: 256_000);
            }
        }

        if (platform == ProviderPlatform.Anthropic && protocol == ProviderProtocol.AnthropicMessages &&
            IsExactModelOrSnapshot(resolvedModel, "claude-sonnet-4-5", "claude-haiku-4-5"))
        {
            return Unknown with
            {
                EvidenceId = "anthropic-structured-outputs-sonnet-haiku-4-5-2026-10-06",
                StructuredOutput = ProviderStructuredOutputSupport.JsonSchema
            };
        }

        if (platform == ProviderPlatform.Anthropic && protocol == ProviderProtocol.AnthropicMessages &&
            IsExactModelOrSnapshot(resolvedModel,
                "claude-opus-4-5", "claude-opus-4-6", "claude-opus-4-7", "claude-opus-4-8", "claude-opus-5", "claude-opus-5-5",
                "claude-sonnet-4-6", "claude-sonnet-5", "claude-sonnet-5-5"))
        {
            var reasoningEfforts = IsExactModelOrSnapshot(resolvedModel, "claude-sonnet-4-6")
                ? AnthropicSonnet46ReasoningEfforts
                : AnthropicOpusReasoningEfforts;
            return new(
                ProviderCapabilitySupport.Unsupported,
                ProviderCapabilitySupport.Unsupported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: reasoningEfforts,
                EvidenceId: "anthropic-messages-post-opus-4-6-2026-10-06",
                StructuredOutput: ProviderStructuredOutputSupport.JsonSchema);
        }

        if (platform == ProviderPlatform.Gemini && protocol == ProviderProtocol.GeminiGenerateContent &&
            string.Equals(resolvedModel, "gemini-3.5-flash", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ProviderCapabilitySupport.Unsupported,
                ProviderCapabilitySupport.Unsupported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: Gemini3ThinkingLevels,
                EvidenceId: "google-gemini-3.5-flash-generate-content-2026-10-07",
                StructuredOutput: ProviderStructuredOutputSupport.JsonSchema,
                MaximumOutputTokens: 65_536,
                Streaming: ProviderCapabilitySupport.Supported,
                ToolCalling: ProviderCapabilitySupport.Supported,
                ContextWindowTokens: 1_048_576);
        }

        if (platform == ProviderPlatform.Gemini && protocol == ProviderProtocol.GeminiGenerateContent &&
            IsExactModelOrSnapshot(resolvedModel,
                "gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.5-flash",
                "gemini-3.5-flash-lite", "gemini-3.1-flash-lite", "gemini-3.1-pro-preview",
                "gemini-3-flash-preview"))
        {
            return new(
                ProviderCapabilitySupport.Unsupported,
                ProviderCapabilitySupport.Unsupported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: Gemini3ThinkingLevels,
                EvidenceId: "google-gemini-3-generate-content-2026-10-06",
                StructuredOutput: ProviderStructuredOutputSupport.JsonSchema);
        }

        if (platform == ProviderPlatform.Gemini && protocol == ProviderProtocol.GeminiGenerateContent &&
            IsExactModelOrSnapshot(resolvedModel, "gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.5-flash-lite"))
        {
            return Unknown with
            {
                EvidenceId = "google-gemini-2.5-generate-content-structured-outputs-2026-10-06",
                StructuredOutput = ProviderStructuredOutputSupport.JsonSchema
            };
        }

        return Unknown;
    }

    public static ProviderCapabilities Resolve(ProviderProfile profile, string resolvedModel)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Platform == ProviderPlatform.OpenRouter &&
            profile.Protocol == ProviderProtocol.OpenAICompatible &&
            !string.IsNullOrWhiteSpace(resolvedModel) &&
            string.Equals(profile.OpenRouterCapabilitiesModelId, resolvedModel, StringComparison.OrdinalIgnoreCase) &&
            profile.OpenRouterSupportedParameters is { } parameterSnapshot)
        {
            var parameters = new HashSet<string>(parameterSnapshot, StringComparer.OrdinalIgnoreCase);
            var supportsNativeSchema = parameters.Contains("structured_outputs") &&
                                       parameters.Contains("response_format") &&
                                       parameters.Contains("max_tokens");
            return new ProviderCapabilities(
                Temperature: parameters.Contains("temperature") ? ProviderCapabilitySupport.Supported : ProviderCapabilitySupport.Unsupported,
                TopP: parameters.Contains("top_p") ? ProviderCapabilitySupport.Supported : ProviderCapabilitySupport.Unsupported,
                UsesMaxCompletionTokens: false,
                ReasoningEffortValues: new HashSet<string>(StringComparer.Ordinal),
                EvidenceId: "openrouter-model-catalog-parameters",
                StructuredOutput: supportsNativeSchema ? ProviderStructuredOutputSupport.JsonSchema : ProviderStructuredOutputSupport.Unknown);
        }

        return Resolve(profile.Platform, profile.Protocol, resolvedModel);
    }

    public static bool UsesDeepSeekV4ThinkingControls(ProviderPlatform platform, ProviderProtocol protocol, string resolvedModel) =>
        platform == ProviderPlatform.DeepSeek && protocol == ProviderProtocol.OpenAICompatible &&
        DeepSeekChatModelIds.Contains(resolvedModel);

    public static string ResolveModelId(ProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.EnableModelMapping && profile.ModelMapping is { Count: > 0 } map &&
               map.TryGetValue(profile.Model, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : profile.Model;
    }

    public static string ResolveModelId(ProviderProfile profile, IEnumerable<ModelMappingEntry> currentMappings)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(currentMappings);
        if (!profile.EnableModelMapping) return profile.Model;
        foreach (var entry in currentMappings)
        {
            if (string.Equals(entry.Key?.Trim(), profile.Model, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.Value))
                return entry.Value.Trim();
        }

        return ResolveModelId(profile);
    }

    public static string Describe(ProviderPlatform platform, ProviderProtocol protocol, string resolvedModel, InferenceLevel inferenceLevel)
    {
        var parameters = DescribeParameters(platform, protocol, resolvedModel, inferenceLevel);
        var structuredOutput = platform == ProviderPlatform.ManagedLocal
            ? "本地结构化约束由已安装的运行时和模型决定；应用仍会进行本地校验。"
            : Resolve(platform, protocol, resolvedModel).StructuredOutput switch
            {
                ProviderStructuredOutputSupport.JsonSchema => "结构化输出使用已核验的原生 JSON Schema；应用仍会进行本地校验。",
                ProviderStructuredOutputSupport.JsonObjectOnly => "结构化输出使用 JSON Mode；Schema 由应用本地校验。",
                _ => "结构化输出能力未核验：不发送原生约束，只在提示中附带 Schema 并执行本地校验。"
            };
        return $"{parameters} {structuredOutput}";
    }

    public static string Describe(ProviderProfile profile, string resolvedModel)
    {
        var capabilities = Resolve(profile, resolvedModel);
        if (capabilities.EvidenceId == "openrouter-model-catalog-parameters")
            return $"OpenRouter 目录显示此型号的候选参数：{string.Join(", ", profile.OpenRouterSupportedParameters!.OrderBy(value => value, StringComparer.Ordinal))}；请求原生 Schema 时强制路由到支持全部请求参数的端点，并保留本地 Schema 校验。目录能力是型号级提示，不代表某个端点保证支持。";
        return Describe(profile.Platform, profile.Protocol, resolvedModel, profile.InferenceLevel);
    }

    private static string DescribeParameters(ProviderPlatform platform, ProviderProtocol protocol, string resolvedModel, InferenceLevel inferenceLevel)
    {
        if (UsesDeepSeekV4ThinkingControls(platform, protocol, resolvedModel))
        {
            var effort = inferenceLevel switch
            {
                InferenceLevel.Low => "low 映射为 low",
                InferenceLevel.Medium => "medium 映射为 high",
                InferenceLevel.High => "high 映射为 high",
                _ => "自定义推理档位使用服务默认值"
            };
            return $"DeepSeek 思考模式已启用；{effort}；temperature 不生效；top_p 按 0.95–1.0 范围发送。";
        }

        var capabilities = Resolve(platform, protocol, resolvedModel);
        if (capabilities.EvidenceId.StartsWith("together-serverless-chat-", StringComparison.Ordinal))
            return $"已核验 Together {resolvedModel}：temperature 限制在 0–1，top_p 按设置发送（官方未注明范围）；max_tokens 按设置发送；不发送未经逐型号核实的推理强度字段。";

        if (capabilities.EvidenceId.StartsWith("xiaomi-mimo-v2.", StringComparison.Ordinal))
        {
            var thinkingEnabled = inferenceLevel != InferenceLevel.Low;
            var thinking = thinkingEnabled
                ? "开启 thinking（MiMo 不支持细分 effort 档位）"
                : "Low 档关闭 thinking";
            var sampling = thinkingEnabled
                ? "thinking 模式下 temperature/top_p 不生效，请求中省略"
                : "thinking 关闭时发送 temperature（上限 1.5）和 top_p（范围 0.01–1.0）";
            var lifecycle = resolvedModel.StartsWith("mimo-v2.5", StringComparison.OrdinalIgnoreCase)
                ? "该 V2.5 型号计划于 2026-10-21 退役，建议迁移至 V2.6"
                : "当前 V2.6 API 型号";
            return $"已核验 MiMo {resolvedModel}（{lifecycle}）：{thinking}；{sampling}；输出预算使用 max_completion_tokens（包含 thinking 与最终答案 token）；结构化输出使用 JSON Mode，Schema 继续由应用本地校验。";
        }

        if (capabilities.EvidenceId.StartsWith("openai-o-series-", StringComparison.Ordinal))
        {
            var effort = inferenceLevel switch
            {
                InferenceLevel.Low => "reasoning_effort=low",
                InferenceLevel.Medium => "reasoning_effort=medium",
                InferenceLevel.High => "reasoning_effort=high",
                _ => "不发送 effort，使用模型默认值"
            };
            return $"已识别 OpenAI {resolvedModel} 推理型号：{effort}；当前官方资料没有明确列出该型号自定义 temperature/top_p 的支持范围，因此本应用省略这两个字段，已保存的采样值不生效；输出预算按 {(protocol == ProviderProtocol.OpenAIResponses ? "max_output_tokens" : "max_completion_tokens")} 发送。";
        }

        if (capabilities.EvidenceId.StartsWith("aliyun-qwen", StringComparison.Ordinal))
        {
            var effort = capabilities.ReasoningEffortValues.Count == 0
                ? "不发送 reasoning_effort，使用模型默认推理级别"
                : $"reasoning_effort={inferenceLevel switch
                {
                    InferenceLevel.Low => "low",
                    InferenceLevel.Medium => "medium",
                    InferenceLevel.High => "xhigh",
                    _ => "服务默认值"
                }}；不发送 thinking_budget";
            var outputTokenField = capabilities.UsesMaxCompletionTokens ? "max_completion_tokens" : "max_tokens";
            var structuredOutput = capabilities.StructuredOutput switch
            {
                ProviderStructuredOutputSupport.JsonSchema => "支持原生 JSON Schema，应用继续进行本地校验",
                ProviderStructuredOutputSupport.JsonObjectOnly => "结构化输出使用 JSON Mode，Schema 由应用本地校验",
                _ => "原生结构化输出未核验，使用提示词 Schema 与应用本地校验"
            };
            return $"已核验阿里云百炼 {resolvedModel}：{effort}；temperature 限制在 [0,2)，top_p 限制在 (0,1]；输出长度字段使用 {outputTokenField}；{structuredOutput}";
        }

        if (capabilities.EvidenceId.StartsWith("volcengine-doubao-seed-2-0-lite-260428", StringComparison.Ordinal))
        {
            var effort = inferenceLevel switch
            {
                InferenceLevel.Low => "low",
                InferenceLevel.Medium => "medium",
                InferenceLevel.High => "high",
                _ => "服务默认推理级别"
            };
            return $"已核验火山方舟 {resolvedModel}：推理强度映射为 reasoning_effort={effort}；temperature/top_p 按设置发送。";
        }

        if (capabilities.EvidenceId.StartsWith("volcengine-doubao-seed-2-0-260215-fixed", StringComparison.Ordinal))
            return $"已核验火山方舟 {resolvedModel}：服务固定 temperature=1.0、top_p=0.95，用户采样设置不生效；请求省略这两个字段。该型号已标记即将下线。";

        if (capabilities.EvidenceId.StartsWith("moonshot-kimi-k3", StringComparison.Ordinal))
        {
            var effort = inferenceLevel switch
            {
                InferenceLevel.Low => "low",
                InferenceLevel.Medium => "high",
                InferenceLevel.High => "max",
                _ => "服务默认推理级别（max）"
            };
            var contextWindow = capabilities.ContextWindowTokens is { } k3Context
                ? $"上下文窗口 {k3Context.ToString("N0", CultureInfo.InvariantCulture)} tokens；"
                : string.Empty;
            return $"已核验 Kimi K3 {resolvedModel}：{contextWindow}temperature=1.0 / top_p=0.95 固定；推理强度映射为 reasoning_effort={effort}，输出上限仍使用应用的 max_tokens 设置。";
        }

        if (capabilities.EvidenceId.StartsWith("moonshot-kimi-k2-current", StringComparison.Ordinal))
        {
            var contextWindow = capabilities.ContextWindowTokens is { } k2Context
                ? $"上下文窗口 {k2Context.ToString("N0", CultureInfo.InvariantCulture)} tokens；"
                : string.Empty;
            if (capabilities.SupportsThinkingToggle)
            {
                var temperature = inferenceLevel == InferenceLevel.Low ? "0.6（关闭思考）" : "1.0（启用思考）";
                var thinkingMode = inferenceLevel == InferenceLevel.Low
                    ? "Low 档发送 thinking.type=disabled，关闭思考以减少推理开销"
                    : $"{inferenceLevel} 档保留 K2.6 默认启用思考，不发送 thinking 参数";
                return $"已核验 Kimi {resolvedModel}：{contextWindow}服务固定 temperature={temperature}、top_p=0.95；请求省略固定采样字段；{thinkingMode}，输出上限与超时仍按应用档位设置。";
            }

            var thinking = resolvedModel.StartsWith("kimi-k2.6", StringComparison.OrdinalIgnoreCase)
                ? "K2.6 默认启用思考模式"
                : "K2.7 Code 默认始终启用思考模式";
            return $"已核验 Kimi {resolvedModel}：{contextWindow}temperature=1.0 / top_p=0.95 固定；{thinking}，本应用不发送 thinking 或 reasoning_effort，档位仅影响 max_tokens / 超时设置。";
        }

        if (capabilities.EvidenceId.StartsWith("zhipu-glm-5.3", StringComparison.Ordinal))
        {
            var effort = inferenceLevel switch
            {
                InferenceLevel.Low => "low",
                InferenceLevel.Medium => "high",
                InferenceLevel.High => "max",
                _ => "服务默认值 max"
            };
            return $"已核验智谱 {resolvedModel}：reasoning_effort={effort}；temperature/top_p 按配置发送（官方建议只调一项）；结构化输出使用 json_object，Schema 由应用侧校验。";
        }

        if (capabilities.EvidenceId.StartsWith("zhipu-glm-5.2", StringComparison.Ordinal))
        {
            var effort = inferenceLevel switch
            {
                InferenceLevel.Low => "none（关闭思考）",
                InferenceLevel.Medium => "low（服务映射为 high）",
                InferenceLevel.High => "max",
                _ => "服务默认值 max"
            };
            return $"已核验智谱 {resolvedModel}：reasoning_effort={effort}；temperature/top_p 按配置发送（官方建议只调一项）；结构化输出使用 json_object，Schema 由应用侧校验。";
        }

        if (capabilities.EvidenceId.StartsWith("xfyun-spark-openai-compatible", StringComparison.Ordinal))
            return $"已核验讯飞星火 {resolvedModel}：temperature 限制在 0–2，top_p 限制在 (0,1]；max_tokens 上限为 {capabilities.MaximumOutputTokens}；结构化输出使用 json_object，Schema 由应用侧校验；未配置文档未列出的推理强度字段。";

        if (capabilities.EvidenceId.StartsWith("groq-openai-compatible-reasoning-strict-schema", StringComparison.Ordinal))
        {
            var effort = inferenceLevel switch
            {
                InferenceLevel.Low => "low",
                InferenceLevel.Medium => "medium",
                InferenceLevel.High => "high",
                _ => "服务默认值"
            };
            var suppression = capabilities.ReasoningOutputPolicy == ProviderReasoningOutputPolicy.HideWithReasoningFormat
                ? "reasoning_format=hidden，不返回推理内容"
                : "include_reasoning=false，不返回推理内容";
            return $"已核验 Groq {resolvedModel}：reasoning_effort={effort}；{suppression}；支持严格 JSON Schema；max_completion_tokens 按设置发送；temperature/top_p 按设置发送（官方建议只调整一项）。";
        }

        if (capabilities.EvidenceId.StartsWith("groq-openai-compatible-json-mode", StringComparison.Ordinal))
            return $"已核验 Groq {resolvedModel}：结构化输出使用 json_object，Schema 由应用侧校验；max_completion_tokens 按设置发送；temperature/top_p 按设置发送（官方建议只调整一项）。";

        if (capabilities.EvidenceId.StartsWith("xai-grok-", StringComparison.Ordinal))
        {
            var effort = inferenceLevel switch
            {
                InferenceLevel.Low => "low",
                InferenceLevel.Medium => "medium",
                InferenceLevel.High => "high",
                _ => "服务默认值"
            };
            var outputLimit = protocol == ProviderProtocol.OpenAIResponses ? "max_output_tokens" : "max_completion_tokens";
            var protocolLabel = protocol == ProviderProtocol.OpenAIResponses ? "Responses API" : "Chat Completions";
            var effortParameter = protocol == ProviderProtocol.OpenAIResponses ? "reasoning.effort" : "reasoning_effort";
            var persistence = protocol == ProviderProtocol.OpenAIResponses
                ? " Responses 请求设置 store=false，不启用 API 的响应持久化检索。"
                : string.Empty;
            return $"已核验 xAI {resolvedModel}（{protocolLabel}）：{effortParameter}={effort}；temperature/top_p 按设置发送；输出上限按 {outputLimit} 发送；支持原生 JSON Schema，应用仍会本地校验。{persistence}";
        }

        var reasoningDescription = inferenceLevel == InferenceLevel.Custom
            ? "自定义参数模式下不发送推理级别字段。"
            : "当前推理强度按 low / medium / high 映射";
        if (capabilities.EvidenceId.StartsWith("openai-gpt-6-", StringComparison.Ordinal) ||
            capabilities.EvidenceId.StartsWith("openai-gpt-6-astra", StringComparison.Ordinal))
        {
            var toolCalling = capabilities.ToolCallingRequiresNoReasoningEffort
                ? "Chat Completions 仅在 reasoning_effort=none 时支持工具调用。"
                : protocol == ProviderProtocol.OpenAIResponses
                    ? "Responses API 支持工具调用。"
                    : "Chat Completions 不支持工具调用；工具调用需使用 Responses API。";
            var outputField = protocol == ProviderProtocol.OpenAIResponses ? "max_output_tokens" : "max_completion_tokens";
            return $"已核验 OpenAI {resolvedModel}：上下文上限 1,050,000 tokens，最大输出 128,000 tokens；支持流式输出；{toolCalling}temperature / top_p 使用模型默认值；{(inferenceLevel == InferenceLevel.Custom ? "自定义档使用模型默认推理强度。" : $"推理强度按 low / medium / high 映射为 {(protocol == ProviderProtocol.OpenAIResponses ? "reasoning.effort" : "reasoning_effort")}={inferenceLevel.ToString().ToLowerInvariant()}。")}{outputField} 为该协议的输出上限字段。";
        }

        if (capabilities.EvidenceId.StartsWith("openai-gpt-4o-chat-responses", StringComparison.Ordinal))
        {
            var outputField = protocol == ProviderProtocol.OpenAIResponses ? "max_output_tokens" : "max_completion_tokens";
            return $"已核验 OpenAI {resolvedModel}：上下文上限 128,000 tokens；支持流式输出与工具调用；temperature 范围 0–2，top_p 范围 0–1（官方建议只调整一项）；输出上限按 {outputField} 发送并限制为 16,384；支持原生 JSON Schema，应用仍会本地校验。";
        }

        if (capabilities.EvidenceId.StartsWith("openai-responses-gpt-4.1-mini", StringComparison.Ordinal))
            return $"已核验 {resolvedModel}（Responses API）：temperature / top_p 按设置发送；输出上限按 max_output_tokens 发送。";

        if (capabilities.EvidenceId.StartsWith("anthropic-messages-post-opus", StringComparison.Ordinal))
            return $"已核验 {resolvedModel}：temperature / top_p 使用模型默认值；{reasoningDescription}{(inferenceLevel == InferenceLevel.Custom ? string.Empty : "为 output_config.effort。")}";

        if (capabilities.EvidenceId.StartsWith("google-gemini-3-generate-content", StringComparison.Ordinal))
            return $"已核验 {resolvedModel}（Gemini 3 GenerateContent）：temperature / top_p 使用模型默认值；{(inferenceLevel == InferenceLevel.Custom ? "自定义参数模式下不发送 thinkingLevel，使用模型默认推理级别。" : $"当前推理强度按 low / medium / high 映射为 generationConfig.thinkingConfig.thinkingLevel（{inferenceLevel.ToString().ToLowerInvariant()}）。")}";

        if (capabilities.EvidenceId.StartsWith("mistral-chat-reasoning", StringComparison.Ordinal))
        {
            var effort = inferenceLevel == InferenceLevel.Custom
                ? "使用服务默认推理级别"
                : $"reasoning_effort={inferenceLevel.ToString().ToLowerInvariant()}";
            return $"已核验 Mistral {resolvedModel}：{effort}；严格 JSON Schema 输出由 Mistral API 约束；temperature/top_p 按设置发送（官方建议只调整一项）。高推理级别会增加推理 token。";
        }

        if (capabilities.EvidenceId.StartsWith("mistral-small-4-structured-output", StringComparison.Ordinal))
            return $"已核验 Mistral {resolvedModel}：上下文上限 256,000 tokens；支持工具调用和严格 JSON Schema 输出；推理强度使用模型默认值；temperature/top_p 按设置发送；最大输出限制未核验。";

        if (capabilities.EvidenceId.StartsWith("mistral-large-latest-json-mode", StringComparison.Ordinal))
            return $"已核验 Mistral {resolvedModel}：结构化输出使用 JSON Mode，Schema 由应用本地校验；max_tokens 按设置发送；temperature/top_p 按设置发送（官方建议只调整一项，未核验该型号专属数值范围）；不发送未核验的 reasoning_effort。";

        if (capabilities.EvidenceId.StartsWith("mistral-ministral-3-8b-structured-output", StringComparison.Ordinal))
            return $"已核验 Mistral {resolvedModel}：支持严格 JSON Schema 输出，应用继续本地校验；max_tokens 与 temperature/top_p 按设置发送；不发送未核验的 reasoning_effort。";

        if (capabilities.EvidenceId.StartsWith("siliconflow-v4-glm52-thinking-json-mode", StringComparison.Ordinal))
        {
            var effort = inferenceLevel switch
            {
                InferenceLevel.High => "显式发送 reasoning_effort=high",
                InferenceLevel.Custom => "使用平台默认 high",
                _ => "平台将 low/medium 折算为 high；请求省略该字段并使用默认 high"
            };
            return $"已核验硅基流动 {resolvedModel}：开启 enable_thinking；{effort}；当前界面档位不能选择低于 high 或 max 的推理强度，且没有 xhigh 档位映射到 max；结构化输出使用 JSON Mode，Schema 由应用侧校验；max_tokens 只限制最终答案长度，不设置 thinking_budget。temperature/top_p 按设置发送（官方建议只调整一项）。";
        }

        if (capabilities.EvidenceId.StartsWith("siliconflow-deepseek-v3-json-mode", StringComparison.Ordinal))
            return $"已核验硅基流动 {resolvedModel}：结构化输出使用 JSON Mode；Schema 放入 system 指令并由应用侧校验，不依赖未核实的严格 JSON Schema 支持。temperature/top_p 按设置发送（官方建议只调整一项）。";

        if (capabilities.EvidenceId.StartsWith("siliconflow-qwen2.5-7b-json-mode", StringComparison.Ordinal))
            return $"已核验硅基流动 {resolvedModel}：temperature 限制在 0–2，top_p 不高于 1；结构化输出使用 JSON Mode，Schema 由应用侧校验，不声明严格 JSON Schema 能力。";

        if (capabilities.EvidenceId.StartsWith("yi-official-chat-api", StringComparison.Ordinal))
            return $"已核验零一万物 {resolvedModel}：temperature 按 0–2、top_p 按 0–1 范围发送，输出上限使用 max_tokens；推理强度与 JSON Schema 能力未核验。";

        if (capabilities.EvidenceId.StartsWith("baichuan-chat-api", StringComparison.Ordinal))
            return $"已核验百川 {resolvedModel}：temperature 限制在 0–1，top_p 限制在 [0,1)（配置为 1 时发送最接近但小于 1 的值），max_tokens 上限为 2048；不支持的推理等级不发送。";

        if (capabilities.EvidenceId.StartsWith("stepfun-3.7-flash-model", StringComparison.Ordinal))
            return $"已核验阶跃星辰 {resolvedModel}：上下文上限 256,000 tokens；支持流式输出与工具调用；推理强度仅映射官方支持档位（{string.Join("/", capabilities.ReasoningEffortValues.OrderBy(value => value, StringComparer.Ordinal))}）；temperature 按 0–2、top_p 按设置发送（官方未注明 top_p 范围）；结构化输出使用提示词 Schema 并由应用本地校验；输出上限未按该型号核实。";

        if (capabilities.EvidenceId.StartsWith("google-gemini-3.5-flash-generate-content", StringComparison.Ordinal))
            return $"已核验 Google Gemini {resolvedModel}（GenerateContent）：上下文上限 1,048,576 tokens、最大输出 65,536 tokens；支持流式响应、Function Calling 与原生 JSON Schema；推理档位映射 low/medium/high。当前应用仍使用非流式生成路径，尚未执行模型工具调用。";

        if (capabilities.EvidenceId.StartsWith("stepfun-chat-api", StringComparison.Ordinal))
        {
            var reasoning = capabilities.ReasoningEffortValues.Count == 0
                ? "推理强度未核验"
                : $"仅发送该型号支持的推理档位（{string.Join("/", capabilities.ReasoningEffortValues.OrderBy(value => value, StringComparer.Ordinal))}）";
            var structured = capabilities.StructuredOutput == ProviderStructuredOutputSupport.JsonSchema
                ? "支持原生 JSON Schema 输出并由应用继续校验"
                : "原生结构化输出能力未按该型号核实，使用提示词 Schema 与应用本地校验";
            var output = capabilities.MaximumOutputTokens is { } maximumOutputTokens
                ? $"，服务端 max_tokens 上限 64K（应用当前设置上限为 32K）"
                : string.Empty;
            return $"已核验阶跃星辰 {resolvedModel}：temperature 按 0–2、top_p 按设置发送（官方未注明 top_p 范围）；{reasoning}；{structured}{output}。";
        }

        if (capabilities.EvidenceId.StartsWith("minimax-openai-chat", StringComparison.Ordinal))
        {
            var outputField = capabilities.UsesMaxCompletionTokens ? "max_completion_tokens（当前 M3 推荐字段）" : "max_tokens";
            var m3Reasoning = string.Equals(resolvedModel, "MiniMax-M3", StringComparison.OrdinalIgnoreCase)
                ? "thinking 按官方模型默认开启，reasoning_split=true 将思考内容置于 reasoning_content，仅读取最终 content"
                : "模型 content 可能包含 <think> 推理段，应用剥离该段后再交付成稿；推理强度不做未经支持的参数调节";
            return $"已核验 MiniMax {resolvedModel}：temperature 限制在 0–2，top_p 限制在 0–1；输出长度字段使用 {outputField}；{m3Reasoning}。当前 API 文档未声明 JSON Schema 原生支持，使用提示词 Schema 与本地契约校验。";
        }

        if (protocol == ProviderProtocol.OpenAIResponses)
            return $"Responses API：{resolvedModel} 的参数能力尚未核验；temperature / top_p 与 reasoning.effort 不发送，使用服务默认值；max_output_tokens 按设置发送。";

        return $"{resolvedModel} 的参数能力尚未核验；当前沿用兼容参数规则，若服务端不支持这些参数，可能拒绝请求。";
    }

    private static bool IsExactModelOrSnapshot(string model, params string[] supportedModelIds)
    {
        foreach (var supportedModelId in supportedModelIds)
        {
            if (string.Equals(model, supportedModelId, StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith(supportedModelId + "-", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsExactModelId(string model, params string[] supportedModelIds) =>
        supportedModelIds.Any(supportedModelId =>
            string.Equals(model, supportedModelId, StringComparison.OrdinalIgnoreCase));
}
