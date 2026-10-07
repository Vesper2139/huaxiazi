using System.Collections.Generic;
using System.Linq;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ProviderPlatformTests
{
    [Fact]
    public void Capabilities_ExperimentalOllamaQwen3ProfileDoesNotDeclareProductReasoningControl()
    {
        var profile = new ProviderProfile
        {
            Type = ProviderType.Local,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "http://127.0.0.1:11434/v1",
            Model = "qwen3:4b"
        };

        var capabilities = ProviderCapabilityResolver.Resolve(profile, profile.Model);

        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.DoesNotContain("Ollama 0.33.3", ProviderCapabilityResolver.Describe(profile, profile.Model));

        profile.InferenceLevel = InferenceLevel.Low;
        Assert.DoesNotContain("reasoning_effort=none", ProviderCapabilityResolver.Describe(profile, profile.Model));
    }

    [Theory]
    [InlineData(ProviderType.Local, "http://127.0.0.1:11435/v1", "qwen3:4b")]
    [InlineData(ProviderType.Local, "http://127.0.0.1:11434/v1", "qwen3:4b-custom")]
    [InlineData(ProviderType.Cloud, "http://127.0.0.1:11434/v1", "qwen3:4b")]
    public void Capabilities_OllamaQwen3FourBThinkingControlDoesNotLeakToUnverifiedProfiles(
        ProviderType type, string apiBase, string model)
    {
        var profile = new ProviderProfile
        {
            Type = type,
            Platform = ProviderPlatform.Ollama,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = apiBase,
            Model = model
        };

        var capabilities = ProviderCapabilityResolver.Resolve(profile, profile.Model);

        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.NotEqual("ollama-qwen3-4b-thinking-control-2026-10-07", capabilities.EvidenceId);
    }

    [Theory]
    [InlineData(ProviderPlatform.OpenAI, ProviderProtocol.OpenAICompatible, "gpt-4o-mini")]
    [InlineData(ProviderPlatform.OpenAI, ProviderProtocol.OpenAICompatible, "gpt-6-astra")]
    [InlineData(ProviderPlatform.OpenAI, ProviderProtocol.OpenAIResponses, "gpt-6.1-sol")]
    [InlineData(ProviderPlatform.Anthropic, ProviderProtocol.AnthropicMessages, "claude-sonnet-4-5")]
    [InlineData(ProviderPlatform.Gemini, ProviderProtocol.GeminiGenerateContent, "gemini-3.8-flash")]
    [InlineData(ProviderPlatform.Gemini, ProviderProtocol.GeminiGenerateContent, "gemini-2.5-flash")]
    public void Capabilities_KnownModelsDeclareNativeJsonSchema(ProviderPlatform platform, ProviderProtocol protocol, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(platform, protocol, model);

        Assert.Equal(ProviderStructuredOutputSupport.JsonSchema, capabilities.StructuredOutput);
    }

    [Theory]
    [InlineData(ProviderProtocol.OpenAICompatible, "gpt-4o")]
    [InlineData(ProviderProtocol.OpenAICompatible, "gpt-4o-mini")]
    [InlineData(ProviderProtocol.OpenAIResponses, "gpt-4o")]
    [InlineData(ProviderProtocol.OpenAIResponses, "gpt-4o-mini")]
    public void Capabilities_OpenAiGpt4oModelsMapSamplingTokenFieldAndOutputLimit(ProviderProtocol protocol, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(ProviderPlatform.OpenAI, protocol, model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.Equal(protocol == ProviderProtocol.OpenAICompatible, capabilities.UsesMaxCompletionTokens);
        Assert.Equal(16_384, capabilities.MaximumOutputTokens);
        Assert.Equal(ProviderStructuredOutputSupport.JsonSchema, capabilities.StructuredOutput);
        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.Equal("openai-gpt-4o-chat-responses-2026-10-07", capabilities.EvidenceId);
        var summary = ProviderCapabilityResolver.Describe(
            ProviderPlatform.OpenAI, protocol, model, InferenceLevel.Medium);
        Assert.Contains(protocol == ProviderProtocol.OpenAIResponses ? "max_output_tokens" : "max_completion_tokens", summary);
        Assert.Contains("16,384", summary);
    }

    [Theory]
    [InlineData(ProviderProtocol.OpenAICompatible, "gpt-4o")]
    [InlineData(ProviderProtocol.OpenAICompatible, "gpt-4o-mini")]
    [InlineData(ProviderProtocol.OpenAIResponses, "gpt-4o")]
    [InlineData(ProviderProtocol.OpenAIResponses, "gpt-4o-mini")]
    public void Capabilities_OpenAiGpt4oModelsDeclareDocumentedStreamingToolsAndContext(
        ProviderProtocol protocol, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(ProviderPlatform.OpenAI, protocol, model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Streaming);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.ToolCalling);
        Assert.Equal(128_000, capabilities.ContextWindowTokens);
        Assert.Equal(ProviderCapabilitySupport.Unknown, ProviderCapabilityResolver.Resolve(
            ProviderPlatform.OpenAI, protocol, model + "-custom").Streaming);
    }

    [Theory]
    [InlineData(ProviderPlatform.OpenRouter, ProviderProtocol.OpenAICompatible, "openai/gpt-4o-mini")]
    [InlineData(ProviderPlatform.CustomOpenAICompatible, ProviderProtocol.OpenAICompatible, "vendor/model")]
    [InlineData(ProviderPlatform.Anthropic, ProviderProtocol.AnthropicMessages, "claude-opus-4-1")]
    [InlineData(ProviderPlatform.Gemini, ProviderProtocol.GeminiGenerateContent, "gemini-1.5-flash")]
    public void Capabilities_UnverifiedModelsRemainUnknownForNativeJsonSchema(ProviderPlatform platform, ProviderProtocol protocol, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(platform, protocol, model);

        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
    }

    [Fact]
    public void Capabilities_Gemini35FlashMapsExactGenerateContentLimitsAndFeatures()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Gemini, ProviderProtocol.GeminiGenerateContent, "gemini-3.5-flash");
        var unverifiedVariant = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Gemini, ProviderProtocol.GeminiGenerateContent, "gemini-3.5-flash-custom");

        Assert.Equal("google-gemini-3.5-flash-generate-content-2026-10-07", capabilities.EvidenceId);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Streaming);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.ToolCalling);
        Assert.Equal(1_048_576, capabilities.ContextWindowTokens);
        Assert.Equal(65_536, capabilities.MaximumOutputTokens);
        Assert.Equal(ProviderStructuredOutputSupport.JsonSchema, capabilities.StructuredOutput);
        Assert.Equal(ProviderCapabilitySupport.Unknown, unverifiedVariant.Streaming);
        Assert.Equal(ProviderCapabilitySupport.Unknown, unverifiedVariant.ToolCalling);
        Assert.Null(unverifiedVariant.ContextWindowTokens);
        Assert.Null(unverifiedVariant.MaximumOutputTokens);
    }

    [Fact]
    public void Describe_Gemini35FlashExplainsGenerateContentFeaturesAndLimits()
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Gemini,
            ProviderProtocol.GeminiGenerateContent,
            "gemini-3.5-flash",
            InferenceLevel.Medium);

        Assert.Contains("1,048,576", description);
        Assert.Contains("65,536", description);
        Assert.Contains("流式", description);
        Assert.Contains("Function Calling", description);
    }

    [Theory]
    [InlineData("qwen3.8-max", "max_completion_tokens", "原生 JSON Schema")]
    [InlineData("qwen-plus", "max_tokens", "JSON Mode")]
    public void Capabilities_QwenSummaryExplainsOutputAndStructureContract(
        string model,
        string outputTokenField,
        string structureDescription)
    {
        var summary = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Qwen,
            ProviderProtocol.OpenAICompatible,
            model,
            InferenceLevel.Medium);

        Assert.Contains(outputTokenField, summary);
        Assert.Contains(structureDescription, summary);
        Assert.Contains("temperature", summary);
        Assert.Contains("top_p", summary);
    }

    [Fact]
    public void Capabilities_MistralLargeLatestDeclaresOnlyDocumentedJsonMode()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Mistral,
            ProviderProtocol.OpenAICompatible,
            "mistral-large-latest");
        var summary = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Mistral,
            ProviderProtocol.OpenAICompatible,
            "mistral-large-latest",
            InferenceLevel.Medium);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.False(capabilities.UsesMaxCompletionTokens);
        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.Equal(ProviderStructuredOutputSupport.JsonObjectOnly, capabilities.StructuredOutput);
        Assert.Contains("JSON Mode", summary);
        Assert.Contains("本地校验", summary);
    }

    [Fact]
    public void Capabilities_Ministral3_8B_DeclaresDocumentedStructuredOutput()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Mistral,
            ProviderProtocol.OpenAICompatible,
            "ministral-8b-2512");
        var summary = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Mistral,
            ProviderProtocol.OpenAICompatible,
            "ministral-8b-2512",
            InferenceLevel.Medium);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.False(capabilities.UsesMaxCompletionTokens);
        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.Equal(ProviderStructuredOutputSupport.JsonSchema, capabilities.StructuredOutput);
        Assert.Contains("严格 JSON Schema", summary);
    }

    [Fact]
    public void Capabilities_YiLightningMapsOnlyDocumentedSamplingAndLeavesSchemaUnknown()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Yi,
            ProviderProtocol.OpenAICompatible,
            "yi-lightning");

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.False(capabilities.UsesMaxCompletionTokens);
        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
        Assert.Equal("yi-official-chat-api-2026-10-07", capabilities.EvidenceId);
        Assert.Contains("temperature 按 0–2", ProviderCapabilityResolver.Describe(
            ProviderPlatform.Yi, ProviderProtocol.OpenAICompatible, "yi-lightning", InferenceLevel.Medium));
        Assert.Contains("JSON Schema", ProviderCapabilityResolver.Describe(
            ProviderPlatform.Yi, ProviderProtocol.OpenAICompatible, "yi-lightning", InferenceLevel.Medium));
    }

    [Fact]
    public void Capabilities_YiLargeMapsDocumentedSamplingWithoutInferringSchemaOrOtherProviders()
    {
        var yiLarge = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Yi, ProviderProtocol.OpenAICompatible, "yi-large");
        var yiLargeUnknownSnapshot = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Yi, ProviderProtocol.OpenAICompatible, "yi-large-preview");
        var proxy = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.OpenRouter, ProviderProtocol.OpenAICompatible, "yi-lightning");

        Assert.Equal(ProviderCapabilitySupport.Supported, yiLarge.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, yiLarge.TopP);
        Assert.False(yiLarge.UsesMaxCompletionTokens);
        Assert.Empty(yiLarge.ReasoningEffortValues);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, yiLarge.StructuredOutput);
        Assert.Equal("yi-official-chat-api-2026-10-07", yiLarge.EvidenceId);
        Assert.Equal("unknown", yiLargeUnknownSnapshot.EvidenceId);
        Assert.Contains("temperature 按 0–2", ProviderCapabilityResolver.Describe(
            ProviderPlatform.Yi, ProviderProtocol.OpenAICompatible, "yi-large", InferenceLevel.Medium));
        Assert.Contains("JSON Schema", ProviderCapabilityResolver.Describe(
            ProviderPlatform.Yi, ProviderProtocol.OpenAICompatible, "yi-large", InferenceLevel.Medium));
        Assert.Equal("unknown", proxy.EvidenceId);
    }

    [Theory]
    [InlineData("deepseek-flash")]
    [InlineData("deepseek-v4-pro")]
    [InlineData("deepseek-v4-flash")]
    [InlineData("deepseek-v4-flash-vision-exp")]
    public void Capabilities_DeepSeekDocumentedChatAliasesUseJsonModeAndThinkingControls(string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.DeepSeek, ProviderProtocol.OpenAICompatible, model);

        Assert.Equal("deepseek-chat-api-2026-10-07", capabilities.EvidenceId);
        Assert.Equal(ProviderStructuredOutputSupport.JsonObjectOnly, capabilities.StructuredOutput);
        Assert.Equal(new[] { "high", "low", "max" }, capabilities.ReasoningEffortValues.OrderBy(value => value).ToArray());
    }

    [Fact]
    public void Capabilities_UnknownDeepSeekV4SuffixDoesNotInheritThinkingOrJsonMode()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.DeepSeek, ProviderProtocol.OpenAICompatible, "deepseek-v4-experimental");

        Assert.Equal("unknown", capabilities.EvidenceId);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
        Assert.False(ProviderCapabilityResolver.UsesDeepSeekV4ThinkingControls(
            ProviderPlatform.DeepSeek, ProviderProtocol.OpenAICompatible, "deepseek-v4-experimental"));
        Assert.Equal("unknown", ProviderCapabilityResolver.Resolve(
            ProviderPlatform.OpenRouter, ProviderProtocol.OpenAICompatible, "deepseek-flash").EvidenceId);
    }

    [Theory]
    [InlineData(InferenceLevel.Low, "low 映射为 low")]
    [InlineData(InferenceLevel.Medium, "medium 映射为 high")]
    [InlineData(InferenceLevel.High, "high 映射为 high")]
    [InlineData(InferenceLevel.Custom, "自定义推理档位使用服务默认值")]
    public void Describe_DeepSeekV4InferenceLevelMatchesActualRequestMapping(
        InferenceLevel level, string expectedMapping)
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.DeepSeek,
            ProviderProtocol.OpenAICompatible,
            "deepseek-flash",
            level);

        Assert.Contains(expectedMapping, description);
    }

    [Fact]
    public void Catalog_DeepSeekNamesCurrentFlashAndCompatibilityRoutingWithoutChangingModelIds()
    {
        var preset = ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek);
        var models = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(preset.Models);

        Assert.Equal("deepseek-flash", preset.DefaultModel);
        Assert.Contains(models, model => model.ModelId == "deepseek-flash" && model.DisplayName.Contains("V4.1 Flash", StringComparison.Ordinal));
        Assert.Contains(models, model => model.ModelId == "deepseek-v4-pro" &&
            model.DisplayName.Contains("兼容别名", StringComparison.Ordinal) &&
            model.DisplayName.Contains("V4.1 Flash", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Baichuan4-Turbo")]
    [InlineData("Baichuan4-Air")]
    [InlineData("Baichuan4")]
    [InlineData("Baichuan3-Turbo")]
    [InlineData("Baichuan3-Turbo-128k")]
    public void Capabilities_BaichuanJsonModelsDeclareOnlyDocumentedOutputMode(string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Baichuan, ProviderProtocol.OpenAICompatible, model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.Equal(ProviderStructuredOutputSupport.JsonObjectOnly, capabilities.StructuredOutput);
        Assert.False(capabilities.UsesMaxCompletionTokens);
        Assert.Equal(1.0, capabilities.MaximumTemperature);
        Assert.Equal(1.0, capabilities.MaximumTopPExclusive);
        Assert.Equal(2048, capabilities.MaximumOutputTokens);
        Assert.StartsWith("baichuan-chat-api-", capabilities.EvidenceId, StringComparison.Ordinal);
    }

    [Fact]
    public void Capabilities_BaichuanDescriptionExplainsNumericLimitsAndSchemaFallback()
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Baichuan, ProviderProtocol.OpenAICompatible,
            "Baichuan4-Turbo", InferenceLevel.Medium);

        Assert.Contains("temperature", description);
        Assert.Contains("0–1", description);
        Assert.Contains("top_p", description);
        Assert.Contains("[0,1)", description);
        Assert.Contains("max_tokens", description);
        Assert.Contains("2048", description);
        Assert.Contains("JSON Mode", description);
        Assert.Contains("本地校验", description);
    }

    [Fact]
    public void Capabilities_Baichuan2HasDocumentedSamplingLimitsButNoListedJsonMode()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Baichuan, ProviderProtocol.OpenAICompatible, "Baichuan2-Turbo");

        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal("baichuan-chat-api-2026-10-07", capabilities.EvidenceId);
    }

    [Fact]
    public void Capabilities_BaichuanVariantsDoNotInheritDocumentedModelMapping()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Baichuan, ProviderProtocol.OpenAICompatible, "Baichuan4-Turbo-custom");

        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
        Assert.Equal("unknown", capabilities.EvidenceId);
    }

    [Fact]
    public void Catalog_BaichuanOffersDocumentedModelsWithoutChangingDefault()
    {
        var baichuan = ProviderPlatformCatalog.Get(ProviderPlatform.Baichuan);
        var modelIds = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(baichuan.Models)
            .Select(model => model.ModelId)
            .ToArray();

        Assert.Equal("Baichuan4-Turbo", baichuan.DefaultModel);
        Assert.Contains("Baichuan4-Air", modelIds);
        Assert.Contains("Baichuan4", modelIds);
        Assert.Contains("Baichuan3-Turbo-128k", modelIds);
        Assert.Contains("Baichuan2-Turbo", modelIds);
    }

    [Fact]
    public void Capabilities_Step5PreviewMapsDocumentedSamplingReasoningAndNativeSchema()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.StepFun, ProviderProtocol.OpenAICompatible, "step-5-preview");

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.Equal(2.0, capabilities.MaximumTemperature);
        Assert.Equal(65536, capabilities.MaximumOutputTokens);
        Assert.Equal(ProviderStructuredOutputSupport.JsonSchema, capabilities.StructuredOutput);
        Assert.Contains("low", capabilities.ReasoningEffortValues);
        Assert.Contains("medium", capabilities.ReasoningEffortValues);
        Assert.Contains("high", capabilities.ReasoningEffortValues);
        Assert.Equal("stepfun-chat-api-2026-10-07", capabilities.EvidenceId);
    }

    [Fact]
    public void Capabilities_Step35Flash2603MapsOnlyDocumentedLowAndHighReasoning()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.StepFun, ProviderProtocol.OpenAICompatible, "step-3.5-flash-2603");

        Assert.Equal(new[] { "high", "low" }, capabilities.ReasoningEffortValues.OrderBy(value => value).ToArray());
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
    }

    [Fact]
    public void Capabilities_Step37FlashMapsThreeReasoningLevelsButKeepsSchemaUnknown()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.StepFun, ProviderProtocol.OpenAICompatible, "step-3.7-flash");

        Assert.Equal(new[] { "high", "low", "medium" }, capabilities.ReasoningEffortValues.OrderBy(value => value).ToArray());
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
        Assert.Null(capabilities.MaximumOutputTokens);
    }

    [Fact]
    public void Capabilities_Step37FlashMapsDocumentedStreamingToolCallingAndContextWithoutGuessingOutputLimit()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.StepFun, ProviderProtocol.OpenAICompatible, "step-3.7-flash");
        var unverifiedVariant = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.StepFun, ProviderProtocol.OpenAICompatible, "step-3.7-flash-custom");

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Streaming);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.ToolCalling);
        Assert.Equal(256_000, capabilities.ContextWindowTokens);
        Assert.Null(capabilities.MaximumOutputTokens);
        Assert.Equal(ProviderCapabilitySupport.Unknown, unverifiedVariant.Streaming);
        Assert.Equal(ProviderCapabilitySupport.Unknown, unverifiedVariant.ToolCalling);
        Assert.Null(unverifiedVariant.ContextWindowTokens);
    }

    [Fact]
    public void Describe_Step37FlashExplainsDocumentedStreamingToolsAndContext()
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.StepFun,
            ProviderProtocol.OpenAICompatible,
            "step-3.7-flash",
            InferenceLevel.Medium);

        Assert.Contains("256,000", description, StringComparison.Ordinal);
        Assert.Contains("流式输出", description, StringComparison.Ordinal);
        Assert.Contains("工具调用", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Capabilities_Step35FlashKeepsUnverifiedReasoningAndSchemaUnknown()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.StepFun, ProviderProtocol.OpenAICompatible, "step-3.5-flash");

        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
    }

    [Fact]
    public void Capabilities_StepFunEvidenceDoesNotApplyToVariantsOrOtherProviders()
    {
        var variant = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.StepFun, ProviderProtocol.OpenAICompatible, "step-5-preview-custom");
        var proxy = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.OpenRouter, ProviderProtocol.OpenAICompatible, "step-5-preview");

        Assert.Equal("unknown", variant.EvidenceId);
        Assert.Equal("unknown", proxy.EvidenceId);
    }

    [Theory]
    [InlineData("MiniMax-M3", true)]
    [InlineData("MiniMax-M2.7", false)]
    [InlineData("MiniMax-M2.7-highspeed", false)]
    public void Capabilities_MiniMaxCurrentModelsMapSamplingAndModelSpecificTokenField(string model, bool usesMaxCompletionTokens)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.MiniMax, ProviderProtocol.OpenAICompatible, model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.Equal(2.0, capabilities.MaximumTemperature);
        Assert.Equal(usesMaxCompletionTokens, capabilities.UsesMaxCompletionTokens);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.Equal("minimax-openai-chat-2026-10-07", capabilities.EvidenceId);
    }

    [Theory]
    [InlineData("mimo-v2.5-pro")]
    [InlineData("mimo-v2.5")]
    public void Capabilities_MiMoV25ModelsOmitUnsupportedSamplingAndUseCompletionBudget(string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.MiMo, ProviderProtocol.OpenAICompatible, model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.True(capabilities.UsesMaxCompletionTokens);
        Assert.False(capabilities.OmitSamplingParameters);
        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.Equal(ProviderStructuredOutputSupport.JsonObjectOnly, capabilities.StructuredOutput);
        Assert.Equal(1.5, capabilities.MaximumTemperature);
        Assert.Equal(0.01, capabilities.MinimumTopP);
        Assert.Equal("xiaomi-mimo-v2.5-openai-chat-2026-10-07", capabilities.EvidenceId);
    }

    [Theory]
    [InlineData("mimo-v2.6-pro")]
    [InlineData("mimo-v2.6-flash")]
    [InlineData("mimo-v2.6-pro-ultraspeed")]
    public void Capabilities_MiMoV26MapsDocumentedSamplingJsonModeAndCompletionBudget(string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.MiMo, ProviderProtocol.OpenAICompatible, model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.Equal(1.5, capabilities.MaximumTemperature);
        Assert.Equal(0.01, capabilities.MinimumTopP);
        Assert.True(capabilities.UsesMaxCompletionTokens);
        Assert.False(capabilities.OmitSamplingParameters);
        Assert.Equal(ProviderStructuredOutputSupport.JsonObjectOnly, capabilities.StructuredOutput);
        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.Equal("xiaomi-mimo-v2.6-openai-chat-2026-10-07", capabilities.EvidenceId);
    }

    [Fact]
    public void Catalog_MiMoDefaultsToV26ProAndKeepsV25AsDeprecatedChoices()
    {
        var mimo = ProviderPlatformCatalog.Get(ProviderPlatform.MiMo);
        var models = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(mimo.Models);

        Assert.Equal("mimo-v2.6-pro", mimo.DefaultModel);
        Assert.Contains(models, model => model.ModelId == "mimo-v2.6-pro");
        Assert.Contains(models, model => model.ModelId == "mimo-v2.6-flash");
        Assert.Contains(models, model => model.ModelId == "mimo-v2.6-pro-ultraspeed");
        Assert.Contains(models, model => model.ModelId == "mimo-v2.5-pro" && model.DisplayName.Contains("退役"));
        Assert.Contains(models, model => model.ModelId == "mimo-v2.5" && model.DisplayName.Contains("退役"));
        Assert.Equal("2026-10-07", mimo.VerifiedOn);
    }

    [Theory]
    [InlineData(ProviderPlatform.MiMo, ProviderProtocol.OpenAICompatible, "mimo-v2.5-pro-preview")]
    [InlineData(ProviderPlatform.MiMo, ProviderProtocol.OpenAICompatible, "mimo-v2-flash")]
    [InlineData(ProviderPlatform.MiMo, ProviderProtocol.OpenAICompatible, "mimo-v2.6-pro-preview")]
    [InlineData(ProviderPlatform.OpenRouter, ProviderProtocol.OpenAICompatible, "mimo-v2.5-pro")]
    public void Capabilities_MiMoEvidenceDoesNotApplyToAliasesOrOtherProviders(
        ProviderPlatform platform, ProviderProtocol protocol, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(platform, protocol, model);

        Assert.Equal("unknown", capabilities.EvidenceId);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
    }

    [Theory]
    [InlineData(InferenceLevel.Low, "Low 档关闭 thinking")]
    [InlineData(InferenceLevel.High, "thinking 模式下 temperature/top_p 不生效")]
    public void Capabilities_MiMoDescriptionExplainsEffectiveThinkingAndTokenBudget(InferenceLevel level, string expected)
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.MiMo, ProviderProtocol.OpenAICompatible, "mimo-v2.5-pro", level);

        Assert.Contains(expected, description);
        Assert.Contains("max_completion_tokens", description);
        Assert.Contains("包含 thinking 与最终答案 token", description);
        Assert.Contains("JSON Mode", description);
        Assert.Contains("本地校验", description);
    }

    [Theory]
    [InlineData("grok-4.7", ProviderProtocol.OpenAICompatible, true, false, true, "xai-grok-4.7-2026-10-07")]
    [InlineData("grok-4.7", ProviderProtocol.OpenAIResponses, false, false, true, "xai-grok-4.7-2026-10-07")]
    [InlineData("grok-4.3", ProviderProtocol.OpenAICompatible, true, true, false, "xai-grok-4.3-2026-10-07")]
    [InlineData("grok-4.3-latest", ProviderProtocol.OpenAIResponses, false, true, false, "xai-grok-4.3-2026-10-07")]
    [InlineData("grok-4.3", ProviderProtocol.OpenAIResponses, false, true, false, "xai-grok-4.3-2026-10-07")]
    public void Capabilities_GrokCurrentModelsMapDocumentedProtocolContract(
        string model, ProviderProtocol protocol, bool usesMaxCompletionTokens,
        bool supportsNoReasoning, bool supportsXhigh, string evidenceId)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Grok, protocol, model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.Equal(2.0, capabilities.MaximumTemperature);
        Assert.Equal(usesMaxCompletionTokens, capabilities.UsesMaxCompletionTokens);
        Assert.Equal(ProviderStructuredOutputSupport.JsonSchema, capabilities.StructuredOutput);
        Assert.Contains("low", capabilities.ReasoningEffortValues);
        Assert.Contains("medium", capabilities.ReasoningEffortValues);
        Assert.Contains("high", capabilities.ReasoningEffortValues);
        Assert.Equal(supportsNoReasoning, capabilities.ReasoningEffortValues.Contains("none"));
        Assert.Equal(supportsXhigh, capabilities.ReasoningEffortValues.Contains("xhigh"));
        Assert.Equal(evidenceId, capabilities.EvidenceId);
    }

    [Fact]
    public void Capabilities_GrokDescriptionExplainsEffectiveChatSettings()
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Grok, ProviderProtocol.OpenAICompatible, "grok-4.3", InferenceLevel.Medium);

        Assert.Contains("xAI", description);
        Assert.Contains("reasoning_effort=medium", description);
        Assert.Contains("max_completion_tokens", description);
        Assert.Contains("JSON Schema", description);
        Assert.Contains("本地校验", description);
        Assert.Contains("top_p", description);
    }

    [Theory]
    [InlineData(ProviderPlatform.Grok, ProviderProtocol.OpenAICompatible, "grok-4.7-custom")]
    [InlineData(ProviderPlatform.Grok, ProviderProtocol.OpenAICompatible, "grok-2-latest")]
    [InlineData(ProviderPlatform.Grok, ProviderProtocol.OpenAIResponses, "grok-2-latest")]
    [InlineData(ProviderPlatform.OpenRouter, ProviderProtocol.OpenAICompatible, "x-ai/grok-4.7")]
    public void Capabilities_GrokEvidenceDoesNotLeakToVariantsOrOtherProtocols(
        ProviderPlatform platform, ProviderProtocol protocol, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(platform, protocol, model);

        Assert.Equal("unknown", capabilities.EvidenceId);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
    }

    [Fact]
    public void Catalog_GrokOffersCurrentModelsAndKeepsLegacyIds()
    {
        var grok = ProviderPlatformCatalog.Get(ProviderPlatform.Grok);
        var modelIds = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(grok.Models)
            .Select(model => model.ModelId)
            .ToArray();

        Assert.Equal("grok-4.3", grok.DefaultModel);
        Assert.Equal(ProviderProtocol.OpenAIResponses, grok.Protocol);
        Assert.Equal("https://api.x.ai/v1", grok.ApiBase);
        Assert.Equal("2026-10-07", grok.VerifiedOn);
        Assert.Contains("grok-4.3", modelIds);
        Assert.Contains("grok-4.7", modelIds);
        Assert.Contains("grok-2-latest", modelIds);
        Assert.Contains("grok-beta", modelIds);
    }

    [Fact]
    public void ConfigNormalization_PreservesExistingGrokChatProtocolAfterPresetUpgrade()
    {
        var existingProfile = new ProviderProfile
        {
            Id = "existing-grok",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.Grok,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.x.ai/v1",
            Model = "grok-2-latest"
        };
        var settings = new AppSettings
        {
            ProviderProfiles = [existingProfile],
            ActiveProviderProfileId = existingProfile.Id
        };

        settings.NormalizeProviderProfiles();

        Assert.Same(existingProfile, settings.ProviderProfiles[0]);
        Assert.Equal(ProviderProtocol.OpenAICompatible, existingProfile.Protocol);
        Assert.Equal("grok-2-latest", existingProfile.Model);
    }

    [Theory]
    [InlineData(ProviderPlatform.MiniMax, "MiniMax-M3-custom")]
    [InlineData(ProviderPlatform.OpenRouter, "MiniMax-M3")]
    public void Capabilities_MiniMaxEvidenceDoesNotApplyToUnverifiedModelsOrOtherPlatforms(ProviderPlatform platform, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(platform, ProviderProtocol.OpenAICompatible, model);

        Assert.Equal("unknown", capabilities.EvidenceId);
    }

    [Fact]
    public void Catalog_MiniMaxOffersCurrentlyServedModelsAndCurrentApiBase()
    {
        var miniMax = ProviderPlatformCatalog.Get(ProviderPlatform.MiniMax);
        var modelIds = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(miniMax.Models)
            .Select(model => model.ModelId)
            .ToArray();

        Assert.Equal("MiniMax-M3", miniMax.DefaultModel);
        Assert.Equal("https://api.minimax.cn/v1", miniMax.ApiBase);
        Assert.Contains("MiniMax-M2.7", modelIds);
        Assert.Contains("MiniMax-M2.7-highspeed", modelIds);
        Assert.Contains("MiniMax-Text-01", modelIds);
        Assert.Contains("abab6.5s-chat", modelIds);
    }

    [Fact]
    public void Catalog_AnthropicUsesActiveSonnetDefaultAndKeepsRetirementVisible()
    {
        var anthropic = ProviderPlatformCatalog.Get(ProviderPlatform.Anthropic);
        var modelIds = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(anthropic.Models)
            .Select(model => model.ModelId)
            .ToArray();

        Assert.Equal("claude-sonnet-5-5", anthropic.DefaultModel);
        Assert.Equal("2026-10-07", anthropic.VerifiedOn);
        Assert.Contains("claude-sonnet-5-5", modelIds);
        Assert.Contains("claude-opus-5-5", modelIds);
        Assert.Contains("claude-haiku-4-5", modelIds);
        Assert.Contains("claude-sonnet-4-5", modelIds);
        Assert.DoesNotContain("claude-opus-4-1", modelIds);
        Assert.Contains(anthropic.Models!, model => model.ModelId == "claude-sonnet-4-5" &&
            model.DisplayName.Contains("2026-11-30", StringComparison.Ordinal));
    }

    [Fact]
    public void Catalog_TogetherUsesDocumentedApiHostAndOffersCurrentServerlessModels()
    {
        var together = ProviderPlatformCatalog.Get(ProviderPlatform.Together);
        var models = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(together.Models);
        var modelIds = models.Select(model => model.ModelId).ToArray();

        Assert.Equal("https://api.together.ai/v1", together.ApiBase);
        Assert.Equal("meta-llama/Llama-3.3-70B-Instruct-Turbo", together.DefaultModel);
        Assert.Equal("2026-10-07", together.VerifiedOn);
        Assert.Contains("thinkingmachines/Inkling", modelIds);
        Assert.Contains("Qwen/Qwen3.5-9B", modelIds);
        Assert.Contains("moonshotai/Kimi-K3", modelIds);
        Assert.Contains("zai-org/GLM-5.3-Flash", modelIds);
        Assert.Contains("zai-org/GLM-5.3", modelIds);
        Assert.Contains("zai-org/GLM-5.2", modelIds);
        Assert.Contains("deepseek-ai/DeepSeek-V4-Flash-0731", modelIds);
        Assert.Contains("deepseek-ai/DeepSeek-V4-Pro-0813", modelIds);
        Assert.Contains("deepseek-ai/DeepSeek-V4.1-Flash", modelIds);
        Assert.Contains("MiniMaxAI/MiniMax-M3", modelIds);
        Assert.Contains("openai/gpt-oss-120b", modelIds);
        Assert.Contains("Qwen2.5 72B（历史型号）", models.Single(model => model.ModelId == "Qwen/Qwen2.5-72B-Instruct").DisplayName);
    }

    [Theory]
    [InlineData("meta-llama/Llama-3.3-70B-Instruct-Turbo")]
    [InlineData("Qwen/Qwen3.5-9B")]
    [InlineData("moonshotai/Kimi-K3")]
    [InlineData("zai-org/GLM-5.3-Flash")]
    [InlineData("zai-org/GLM-5.3")]
    [InlineData("zai-org/GLM-5.2")]
    [InlineData("deepseek-ai/DeepSeek-V4-Flash-0731")]
    [InlineData("deepseek-ai/DeepSeek-V4-Pro-0813")]
    [InlineData("deepseek-ai/DeepSeek-V4.1-Flash")]
    [InlineData("MiniMaxAI/MiniMax-M3")]
    [InlineData("openai/gpt-oss-120b")]
    [InlineData("thinkingmachines/Inkling")]
    public void Capabilities_TogetherCatalogModelsMapDocumentedSamplingAndStructuredOutput(string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Together,
            ProviderProtocol.OpenAICompatible,
            model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.False(capabilities.UsesMaxCompletionTokens);
        Assert.Equal(1.0, capabilities.MaximumTemperature);
        Assert.Null(capabilities.MaximumTopPExclusive);
        Assert.Equal(ProviderStructuredOutputSupport.JsonSchema, capabilities.StructuredOutput);
        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.StartsWith("together-serverless-chat-", capabilities.EvidenceId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProviderPlatform.Together, ProviderProtocol.OpenAICompatible, "Qwen/Qwen2.5-72B-Instruct")]
    [InlineData(ProviderPlatform.Together, ProviderProtocol.OpenAICompatible, "Qwen/Qwen3.8-2.4T-A95B")]
    [InlineData(ProviderPlatform.Together, ProviderProtocol.OpenAICompatible, "Qwen/Qwen3.8-Flash")]
    [InlineData(ProviderPlatform.Together, ProviderProtocol.AnthropicMessages, "Qwen/Qwen3.5-9B")]
    [InlineData(ProviderPlatform.CustomOpenAICompatible, ProviderProtocol.OpenAICompatible, "Qwen/Qwen3.5-9B")]
    public void Capabilities_TogetherStructuredOutputEvidenceDoesNotLeakToUnverifiedModels(
        ProviderPlatform platform, ProviderProtocol protocol, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(platform, protocol, model);

        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
        Assert.Equal("unknown", capabilities.EvidenceId);
    }

    [Fact]
    public void Capabilities_TogetherDescriptionExplainsSamplingLimitsAndSchemaStatus()
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Together,
            ProviderProtocol.OpenAICompatible,
            "Qwen/Qwen3.5-9B",
            InferenceLevel.Medium);

        Assert.Contains("temperature 限制在 0–1", description);
        Assert.Contains("top_p 按设置发送", description);
        Assert.Contains("max_tokens", description);
        Assert.Contains("原生 JSON Schema", description);
        Assert.DoesNotContain("参数能力尚未核验", description);
    }

    [Fact]
    public void ConfigNormalization_PreservesExistingTogetherEndpointAndModel()
    {
        var profile = new ProviderProfile
        {
            Id = "together-legacy-profile",
            Name = "Together Legacy",
            Platform = ProviderPlatform.Together,
            Protocol = ProviderProtocol.OpenAICompatible,
            Type = ProviderType.Cloud,
            ApiBase = "https://api.together.xyz/v1",
            Model = "Qwen/Qwen2.5-72B-Instruct",
            SecretId = "provider-together-legacy-profile"
        };
        var settings = new AppSettings { ProviderProfiles = [profile], ActiveProviderProfileId = profile.Id };

        settings.NormalizeProviderProfiles();

        Assert.Equal("https://api.together.xyz/v1", settings.ProviderProfiles[0].ApiBase);
        Assert.Equal("Qwen/Qwen2.5-72B-Instruct", settings.ProviderProfiles[0].Model);
        Assert.Equal("provider-together-legacy-profile", settings.ProviderProfiles[0].SecretId);
        Assert.Equal(profile.Id, settings.ActiveProviderProfileId);
    }

    [Fact]
    public void ConfigNormalization_DoesNotMigrateExistingAnthropicModelChoice()
    {
        var existingProfile = new ProviderProfile
        {
            Id = "existing-anthropic",
            Type = ProviderType.Cloud,
            Platform = ProviderPlatform.Anthropic,
            Protocol = ProviderProtocol.AnthropicMessages,
            ApiBase = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5"
        };
        var settings = new AppSettings
        {
            ProviderProfiles = [existingProfile],
            ActiveProviderProfileId = existingProfile.Id
        };

        settings.NormalizeProviderProfiles();

        Assert.Equal("claude-sonnet-4-5", settings.ProviderProfiles[0].Model);
        Assert.Equal(ProviderProtocol.AnthropicMessages, settings.ProviderProfiles[0].Protocol);
    }

    [Fact]
    public void Catalog_StepFunOffersCurrentOfficialModelsAndUsesCurrentDocumentedModelForNewProfiles()
    {
        var stepFun = ProviderPlatformCatalog.Get(ProviderPlatform.StepFun);
        var modelIds = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(stepFun.Models)
            .Select(model => model.ModelId)
            .ToArray();

        Assert.Equal("step-3.7-flash", stepFun.DefaultModel);
        Assert.Contains("step-5-preview", modelIds);
        Assert.Contains("step-3.7-flash", modelIds);
        Assert.Contains("step-3.5-flash", modelIds);
        Assert.Contains("step-3.5-flash-2603", modelIds);
        Assert.Contains("step-2-16k", modelIds);

        var savedProfile = new ProviderProfile
        {
            Id = "saved-stepfun-profile",
            Platform = ProviderPlatform.StepFun,
            Protocol = ProviderProtocol.OpenAICompatible,
            Type = ProviderType.Cloud,
            ApiBase = "https://api.stepfun.com/v1",
            Model = "step-2-16k"
        };
        var settings = new AppSettings
        {
            ProviderProfiles = [savedProfile],
            ActiveProviderProfileId = savedProfile.Id
        };

        settings.NormalizeProviderProfiles();

        Assert.Equal("step-2-16k", settings.ProviderProfiles[0].Model);
        Assert.Equal(savedProfile.Id, settings.ActiveProviderProfileId);
    }

    [Theory]
    [InlineData(ProviderPlatform.OpenAI, "https://api.openai.com/v1", "gpt-4o-mini", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Anthropic, "https://api.anthropic.com", "claude-sonnet-5-5", ProviderProtocol.AnthropicMessages)]
    [InlineData(ProviderPlatform.Gemini, "https://generativelanguage.googleapis.com/v1beta", "gemini-3.5-flash", ProviderProtocol.GeminiGenerateContent)]
    [InlineData(ProviderPlatform.DeepSeek, "https://api.deepseek.com/v1", "deepseek-flash", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Qwen, "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Doubao, "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-2-0-lite-260428", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Kimi, "https://api.moonshot.cn/v1", "kimi-k2.6", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Zhipu, "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.SiliconFlow, "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.StepFun, "https://api.stepfun.com/v1", "step-3.7-flash", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.MiniMax, "https://api.minimax.cn/v1", "MiniMax-M3", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.OpenRouter, "https://openrouter.ai/api/v1", "deepseek/deepseek-chat", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Grok, "https://api.x.ai/v1", "grok-4.3", ProviderProtocol.OpenAIResponses)]
    [InlineData(ProviderPlatform.Mistral, "https://api.mistral.ai/v1", "mistral-small-latest", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Groq, "https://api.groq.com/openai/v1", "openai/gpt-oss-120b", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Baichuan, "https://api.baichuan-ai.com/v1", "Baichuan4-Turbo", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Spark, "https://spark-api-open.xf-yun.com/v1", "4.0Ultra", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Yi, "https://api.lingyiwanwu.com/v1", "yi-large", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Together, "https://api.together.ai/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.Ollama, "http://localhost:11434/v1", "qwen3", ProviderProtocol.OpenAICompatible)]
    [InlineData(ProviderPlatform.LmStudio, "http://localhost:1234/v1", "local-model", ProviderProtocol.OpenAICompatible)]
    public void ApplyPreset_UsesPlatformOfficialDefaults(ProviderPlatform platform, string apiBase, string model, ProviderProtocol protocol)
    {
        var profile = new ProviderProfile();

        ProviderPlatformCatalog.ApplyPreset(profile, platform);

        Assert.Equal(platform, profile.Platform);
        Assert.Equal(apiBase, profile.ApiBase);
        Assert.Equal(model, profile.Model);
        Assert.Equal(protocol, profile.Protocol);
    }

    [Fact]
    public void Catalog_ExposesFriendlyPlatformNamesInsteadOfCloudLocalProtocol()
    {
        var names = ProviderPlatformCatalog.Options.Select(option => option.DisplayName).ToArray();

        Assert.Contains("OpenAI", names);
        Assert.Contains("Claude (Anthropic)", names);
        Assert.Contains("Gemini (Google)", names);
        Assert.Contains("DeepSeek", names);
        Assert.Contains("通义千问（阿里云百炼）", names);
        Assert.Contains("豆包（火山方舟）", names);
        Assert.Contains("Kimi（月之暗面）", names);
        Assert.Contains("智谱 GLM", names);
        Assert.Contains("硅基流动", names);
        Assert.Contains("阶跃星辰", names);
        Assert.Contains("MiniMax", names);
        Assert.Contains("OpenRouter", names);
        Assert.Contains("xAI Grok", names);
        Assert.Contains("Mistral", names);
        Assert.Contains("Groq", names);
        Assert.Contains("百川智能", names);
        Assert.Contains("讯飞星火", names);
        Assert.Contains("零一万物 Yi", names);
        Assert.Contains("Together", names);
        Assert.Contains("Ollama（本地）", names);
        Assert.Contains("LM Studio（本地）", names);
        Assert.Contains("自定义 OpenAI 兼容接口", names);
    }

    [Fact]
    public void Catalog_ExposesModelListsContainingDefaultModelPerProvider()
    {
        foreach (var option in ProviderPlatformCatalog.Options)
        {
            if (string.IsNullOrWhiteSpace(option.DefaultModel)) continue; // 自定义接口无预设模型
            var models = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(option.Models);
            Assert.NotEmpty(models);
            Assert.Contains(models, model => model.ModelId == option.DefaultModel);
        }
    }

    [Fact]
    public void Catalog_KimiContainsCurrentModelsAndExcludesRetiredModels()
    {
        var kimi = ProviderPlatformCatalog.Options.Single(option => option.Platform == ProviderPlatform.Kimi);
        var modelIds = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(kimi.Models)
            .Select(model => model.ModelId)
            .ToArray();

        Assert.Equal("kimi-k2.6", kimi.DefaultModel);
        Assert.Contains("kimi-k3", modelIds);
        Assert.Contains("kimi-k2.6", modelIds);
        Assert.Contains("kimi-k2.7-code", modelIds);
        Assert.Contains("kimi-k2.7-code-highspeed", modelIds);
        Assert.DoesNotContain("kimi-k2-0905-preview", modelIds);
        Assert.DoesNotContain(modelIds, modelId => modelId.StartsWith("moonshot-v1-", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("kimi-k3", 1_000_000)]
    [InlineData("kimi-k2.6", 256_000)]
    [InlineData("kimi-k2.7-code", 256_000)]
    [InlineData("kimi-k2.7-code-highspeed", 256_000)]
    public void Capabilities_KimiCurrentModelsExposeDocumentedContextWindow(string model, int expectedContext)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Kimi,
            ProviderProtocol.OpenAICompatible,
            model);

        Assert.Equal(expectedContext, capabilities.ContextWindowTokens);
        var summary = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Kimi,
            ProviderProtocol.OpenAICompatible,
            model,
            InferenceLevel.Medium);
        Assert.Contains($"上下文窗口 {expectedContext:N0} tokens", summary);
    }

    [Theory]
    [InlineData("kimi-k2.6-custom")]
    [InlineData("kimi-k2.7-code-custom")]
    [InlineData("kimi-k2.7-code-highspeed-custom")]
    [InlineData("kimi-k3-custom")]
    public void Capabilities_KimiUnverifiedVariantDoesNotInheritModelCapabilitiesOrDescription(string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Kimi,
            ProviderProtocol.OpenAICompatible,
            model);
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Kimi,
            ProviderProtocol.OpenAICompatible,
            model,
            InferenceLevel.Low);

        Assert.Equal("unknown", capabilities.EvidenceId);
        Assert.Null(capabilities.ContextWindowTokens);
        Assert.False(capabilities.SupportsThinkingToggle);
        Assert.Contains("参数能力尚未核验", description);
        Assert.DoesNotContain("K2.6 默认", description);
        Assert.DoesNotContain("Kimi K3", description);
    }

    [Theory]
    [InlineData(InferenceLevel.Low, "Low 档发送 thinking.type=disabled", "temperature=0.6")]
    [InlineData(InferenceLevel.Medium, "Medium 档保留 K2.6 默认启用思考", "temperature=1.0")]
    [InlineData(InferenceLevel.High, "High 档保留 K2.6 默认启用思考", "temperature=1.0")]
    [InlineData(InferenceLevel.Custom, "Custom 档保留 K2.6 默认启用思考", "temperature=1.0")]
    public void Describe_KimiK26ExplainsThinkingModeForSelectedInferenceLevel(
        InferenceLevel level, string expected, string expectedTemperature)
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Kimi,
            ProviderProtocol.OpenAICompatible,
            "kimi-k2.6",
            level);

        Assert.Contains(expected, description);
        Assert.Contains(expectedTemperature, description);
        Assert.Contains("top_p=0.95", description);
        Assert.Contains("上下文窗口 256,000 tokens", description);
    }

    [Theory]
    [InlineData("mistral-small-latest")]
    [InlineData("mistral-small-2603")]
    [InlineData("mistral-medium-3-5")]
    public void Capabilities_MistralCurrentModelsDeclareDocumentedStructuredOutput(string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Mistral,
            ProviderProtocol.OpenAICompatible,
            model);

        Assert.Equal(ProviderStructuredOutputSupport.JsonSchema, capabilities.StructuredOutput);
        Assert.Contains("mistral-", capabilities.EvidenceId);
    }

    [Fact]
    public void Capabilities_MistralSmall4MapsExactContextAndFunctionCallingWithoutAssumingStreaming()
    {
        var small4 = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Mistral,
            ProviderProtocol.OpenAICompatible,
            "mistral-small-2603");
        var unverifiedVariant = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Mistral,
            ProviderProtocol.OpenAICompatible,
            "mistral-small-2603-custom");
        var rollingAlias = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Mistral,
            ProviderProtocol.OpenAICompatible,
            "mistral-small-latest");

        Assert.Equal(256_000, small4.ContextWindowTokens);
        Assert.Equal(ProviderCapabilitySupport.Supported, small4.ToolCalling);
        Assert.Equal(ProviderCapabilitySupport.Unknown, small4.Streaming);
        Assert.Null(small4.MaximumOutputTokens);
        Assert.Null(unverifiedVariant.ContextWindowTokens);
        Assert.Equal(ProviderCapabilitySupport.Unknown, unverifiedVariant.ToolCalling);
        Assert.Null(rollingAlias.ContextWindowTokens);
    }

    [Fact]
    public void Describe_MistralSmall4ExplainsExactContextAndFunctionCalling()
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.Mistral,
            ProviderProtocol.OpenAICompatible,
            "mistral-small-2603",
            InferenceLevel.Medium);

        Assert.Contains("256,000", description, StringComparison.Ordinal);
        Assert.Contains("工具调用", description, StringComparison.Ordinal);
        Assert.Contains("最大输出限制未核验", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pro/deepseek-ai/DeepSeek-V4")]
    [InlineData("deepseek-ai/DeepSeek-V4-Flash")]
    [InlineData("Pro/zai-org/GLM-5.2")]
    public void Capabilities_SiliconFlowReasoningModelsUseJsonModeAndOnlyDocumentedEfforts(string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.SiliconFlow,
            ProviderProtocol.OpenAICompatible,
            model);

        Assert.Equal(ProviderStructuredOutputSupport.JsonObjectOnly, capabilities.StructuredOutput);
        Assert.Contains("high", capabilities.ReasoningEffortValues);
        Assert.Contains("max", capabilities.ReasoningEffortValues);
        Assert.DoesNotContain("low", capabilities.ReasoningEffortValues);
        Assert.DoesNotContain("medium", capabilities.ReasoningEffortValues);
    }

    [Fact]
    public void Capabilities_SiliconFlowUnknownModelVariantDoesNotInheritReasoningControls()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.SiliconFlow,
            ProviderProtocol.OpenAICompatible,
            "Pro/zai-org/GLM-5.2-custom");

        Assert.Equal("unknown", capabilities.EvidenceId);
        Assert.Empty(capabilities.ReasoningEffortValues);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
    }

    [Fact]
    public void Capabilities_SiliconFlowDescriptionDisclosesEffectiveDefaultAndTokenBudgetSemantics()
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.SiliconFlow,
            ProviderProtocol.OpenAICompatible,
            "deepseek-ai/DeepSeek-V4-Flash",
            InferenceLevel.Low);

        Assert.Contains("enable_thinking", description);
        Assert.Contains("默认 high", description);
        Assert.Contains("JSON Mode", description);
        Assert.Contains("thinking_budget", description);
        Assert.Contains("max_tokens 只限制最终答案长度", description);
    }

    [Fact]
    public void Catalog_SparkListsCurrentHttpModelsAndDefaultsToExplicitUltraId()
    {
        var spark = ProviderPlatformCatalog.Options.Single(option => option.Platform == ProviderPlatform.Spark);
        var models = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(spark.Models);
        var modelIds = models.Select(model => model.ModelId).ToArray();

        Assert.Equal("4.0Ultra", spark.DefaultModel);
        Assert.Contains("max-32k", modelIds);
        Assert.Contains("generalv3", modelIds);
        Assert.Contains("generalv3.5", modelIds);
        Assert.Contains("pro-128k", modelIds);
        Assert.Contains("lite", modelIds);
        Assert.DoesNotContain("general", modelIds);
        Assert.Contains(models, model => model.ModelId == "generalv3.5" && model.DisplayName.Contains("Ultra", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("4.0Ultra", 32768)]
    [InlineData("max-32k", 32768)]
    [InlineData("generalv3.5", 8192)]
    [InlineData("generalv3", 8192)]
    [InlineData("pro-128k", 32768)]
    [InlineData("lite", 4096)]
    public void Capabilities_SparkCurrentHttpModelsMapOfficialSamplingAndOutputLimits(string model, int outputLimit)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Spark, ProviderProtocol.OpenAICompatible, model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.Equal(2.0, capabilities.MaximumTemperature);
        Assert.Equal(0.0, capabilities.MinimumTopPExclusive);
        Assert.Equal(outputLimit, capabilities.MaximumOutputTokens);
        Assert.Equal(ProviderStructuredOutputSupport.JsonObjectOnly, capabilities.StructuredOutput);
        Assert.Empty(capabilities.ReasoningEffortValues);
    }

    [Fact]
    public void Capabilities_SparkUnlistedModelSuffixDoesNotInheritVersionCapabilities()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.Spark, ProviderProtocol.OpenAICompatible, "generalv3.5-custom");

        Assert.Equal("unknown", capabilities.EvidenceId);
        Assert.Null(capabilities.MaximumOutputTokens);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, capabilities.StructuredOutput);
    }

    [Fact]
    public void NormalizeProviderProfiles_DoesNotRewriteExistingSparkModel()
    {
        var profile = new ProviderProfile
        {
            Id = "spark-existing",
            Name = "Existing Spark",
            Platform = ProviderPlatform.Spark,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://spark-api-open.xf-yun.com/v1",
            Model = "generalv3.5"
        };
        var settings = new AppSettings { ProviderProfiles = [profile], ActiveProviderProfileId = profile.Id };

        settings.NormalizeProviderProfiles();

        Assert.Equal("generalv3.5", Assert.Single(settings.ProviderProfiles).Model);
        Assert.Equal("spark-existing", settings.ActiveProviderProfileId);
    }

    [Fact]
    public void Catalog_GroqDefaultsToDocumentedReplacementAndKeepsRetiredModelsVisible()
    {
        var groq = ProviderPlatformCatalog.Options.Single(option => option.Platform == ProviderPlatform.Groq);
        var models = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(groq.Models);
        var modelIds = models.Select(model => model.ModelId).ToArray();

        Assert.Equal("openai/gpt-oss-120b", groq.DefaultModel);
        Assert.Contains("openai/gpt-oss-20b", modelIds);
        Assert.Contains("openai/gpt-oss-120b", modelIds);
        Assert.Contains("qwen/qwen3.8-27b", modelIds);
        Assert.Contains(models, model => model.ModelId == "llama-3.3-70b-versatile" && model.DisplayName.Contains("退役", StringComparison.Ordinal));
        Assert.Contains(models, model => model.ModelId == "llama-3.1-8b-instant" && model.DisplayName.Contains("退役", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeProviderProfiles_DoesNotRewriteExistingRetiredGroqModel()
    {
        var profile = new ProviderProfile
        {
            Id = "groq-existing",
            Name = "Existing Groq",
            Platform = ProviderPlatform.Groq,
            Protocol = ProviderProtocol.OpenAICompatible,
            ApiBase = "https://api.groq.com/openai/v1",
            Model = "llama-3.3-70b-versatile"
        };
        var settings = new AppSettings { ProviderProfiles = [profile], ActiveProviderProfileId = profile.Id };

        settings.NormalizeProviderProfiles();

        Assert.Equal("llama-3.3-70b-versatile", Assert.Single(settings.ProviderProfiles).Model);
        Assert.Equal("groq-existing", settings.ActiveProviderProfileId);
    }

    [Fact]
    public void Catalog_ZhipuContainsCurrentTextModelsAndPreservesFreeDefault()
    {
        var zhipu = ProviderPlatformCatalog.Options.Single(option => option.Platform == ProviderPlatform.Zhipu);
        var modelIds = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(zhipu.Models)
            .Select(model => model.ModelId)
            .ToArray();

        Assert.Equal("glm-4-flash", zhipu.DefaultModel);
        Assert.Contains("glm-5.3", modelIds);
        Assert.Contains("glm-5.2", modelIds);
    }

    [Fact]
    public void Catalog_QwenIncludesCurrentOfficialReasoningModelsWithoutChangingDefault()
    {
        var qwen = ProviderPlatformCatalog.Get(ProviderPlatform.Qwen);
        var models = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(qwen.Models);

        Assert.Equal("qwen-plus", qwen.DefaultModel);
        Assert.Contains(models, model => model.ModelId == "qwen3.8-max");
        Assert.Contains(models, model => model.ModelId == "qwen3.8-flash");
        Assert.Contains(models, model => model.ModelId == "qwen3.8-27b");
    }

    [Fact]
    public void Catalog_GroupsCommonLocalMoreAndCustomPlatforms()
    {
        Assert.Equal(ProviderPresetTier.Common, ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek).Tier);
        Assert.Equal(ProviderPresetTier.Common, ProviderPlatformCatalog.Get(ProviderPlatform.OpenAI).Tier);
        Assert.Equal(ProviderPresetTier.Local, ProviderPlatformCatalog.Get(ProviderPlatform.Ollama).Tier);
        Assert.Equal(ProviderPresetTier.Custom, ProviderPlatformCatalog.Get(ProviderPlatform.CustomOpenAICompatible).Tier);
        Assert.Equal(ProviderPresetTier.More, ProviderPlatformCatalog.Get(ProviderPlatform.Groq).Tier);
    }

    [Fact]
    public void Catalog_CommonTierMatchesTheProductApprovedProviderSet()
    {
        var common = ProviderPlatformCatalog.Options
            .Where(option => option.Tier == ProviderPresetTier.Common)
            .Select(option => option.Platform)
            .ToHashSet();

        Assert.Equal(
            new HashSet<ProviderPlatform>
            {
                ProviderPlatform.DeepSeek,
                ProviderPlatform.Qwen,
                ProviderPlatform.Doubao,
                ProviderPlatform.Kimi,
                ProviderPlatform.Zhipu,
                ProviderPlatform.SiliconFlow,
                ProviderPlatform.OpenAI,
                ProviderPlatform.Anthropic,
                ProviderPlatform.Gemini,
                ProviderPlatform.OpenRouter
            },
            common);
    }

    [Fact]
    public void Catalog_DeclaresSecretRequirementAndVerificationDate()
    {
        var cloud = ProviderPlatformCatalog.Get(ProviderPlatform.DeepSeek);
        var local = ProviderPlatformCatalog.Get(ProviderPlatform.Ollama);

        Assert.True(cloud.RequiresApiKey);
        Assert.False(local.RequiresApiKey);
        Assert.Equal("2026-10-07", cloud.VerifiedOn);
    }

    [Theory]
    [InlineData("gpt-6-astra", ProviderProtocol.OpenAICompatible, false)]
    [InlineData("gpt-6.1-sol", ProviderProtocol.OpenAICompatible, false)]
    [InlineData("gpt-6-sol", ProviderProtocol.OpenAICompatible, true)]
    [InlineData("gpt-6-luna", ProviderProtocol.OpenAIResponses, true)]
    public void Resolve_OpenAiGpt6Models_UsesDocumentedEffortAndSamplingCapabilities(
        string model, ProviderProtocol protocol, bool supportsNone)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(ProviderPlatform.OpenAI, protocol, model);

        Assert.Equal(ProviderCapabilitySupport.Unsupported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Unsupported, capabilities.TopP);
        Assert.Equal(protocol == ProviderProtocol.OpenAICompatible, capabilities.UsesMaxCompletionTokens);
        Assert.Contains("low", capabilities.ReasoningEffortValues);
        Assert.Contains("medium", capabilities.ReasoningEffortValues);
        Assert.Contains("high", capabilities.ReasoningEffortValues);
        Assert.Equal(supportsNone, capabilities.ReasoningEffortValues.Contains("none"));
    }

    [Theory]
    [InlineData("gpt-6-astra", ProviderProtocol.OpenAICompatible, "Unsupported", false)]
    [InlineData("gpt-6-astra", ProviderProtocol.OpenAIResponses, "Supported", false)]
    [InlineData("gpt-6.1-sol", ProviderProtocol.OpenAICompatible, "Unsupported", false)]
    [InlineData("gpt-6.1-sol", ProviderProtocol.OpenAIResponses, "Supported", false)]
    [InlineData("gpt-6-sol", ProviderProtocol.OpenAICompatible, "Supported", true)]
    [InlineData("gpt-6-sol", ProviderProtocol.OpenAIResponses, "Supported", false)]
    [InlineData("gpt-6-luna", ProviderProtocol.OpenAICompatible, "Supported", true)]
    [InlineData("gpt-6-luna", ProviderProtocol.OpenAIResponses, "Supported", false)]
    public void Resolve_OpenAiGpt6Models_MapsDocumentedContextOutputStreamingAndToolSupport(
        string model, ProviderProtocol protocol, string expectedToolCalling, bool requiresNoReasoning)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(ProviderPlatform.OpenAI, protocol, model);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Streaming);
        Assert.Equal(expectedToolCalling, capabilities.ToolCalling.ToString());
        Assert.Equal(requiresNoReasoning, capabilities.ToolCallingRequiresNoReasoningEffort);
        Assert.Equal(1_050_000, capabilities.ContextWindowTokens);
        Assert.Equal(128_000, capabilities.MaximumOutputTokens);
    }

    [Theory]
    [InlineData("gpt-6-astra", ProviderProtocol.OpenAICompatible, "Chat Completions 不支持工具调用")]
    [InlineData("gpt-6.1-sol", ProviderProtocol.OpenAICompatible, "Chat Completions 不支持工具调用")]
    [InlineData("gpt-6-sol", ProviderProtocol.OpenAICompatible, "reasoning_effort=none")]
    [InlineData("gpt-6-luna", ProviderProtocol.OpenAICompatible, "reasoning_effort=none")]
    [InlineData("gpt-6-sol", ProviderProtocol.OpenAIResponses, "Responses API 支持工具调用")]
    public void Describe_OpenAiGpt6Models_ExplainsProtocolSpecificToolCallingRequirements(
        string model, ProviderProtocol protocol, string expectedRequirement)
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.OpenAI, protocol, model, InferenceLevel.Medium);

        Assert.Contains(expectedRequirement, description, StringComparison.Ordinal);
        Assert.Contains("1,050,000", description, StringComparison.Ordinal);
        Assert.Contains("128,000", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProviderPlatform.OpenRouter, ProviderProtocol.OpenAICompatible, "openai/gpt-6-astra")]
    [InlineData(ProviderPlatform.OpenAI, ProviderProtocol.OpenAICompatible, "gpt-6-astra-custom")]
    [InlineData(ProviderPlatform.OpenAI, ProviderProtocol.OpenAICompatible, "gpt-6.1-sol-2026-10-01")]
    public void Resolve_OpenAiGpt6Capabilities_DoNotLeakToProxyOrUnverifiedVariants(
        ProviderPlatform platform, ProviderProtocol protocol, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(platform, protocol, model);

        Assert.Equal("unknown", capabilities.EvidenceId);
        Assert.Equal(ProviderCapabilitySupport.Unknown, capabilities.Streaming);
        Assert.Equal(ProviderCapabilitySupport.Unknown, capabilities.ToolCalling);
        Assert.Null(capabilities.ContextWindowTokens);
        Assert.Null(capabilities.MaximumOutputTokens);
        Assert.False(capabilities.ToolCallingRequiresNoReasoningEffort);
    }

    [Theory]
    [InlineData("o3", ProviderProtocol.OpenAICompatible)]
    [InlineData("o3-2025-04-16", ProviderProtocol.OpenAICompatible)]
    [InlineData("o4-mini", ProviderProtocol.OpenAICompatible)]
    [InlineData("o4-mini-2025-04-16", ProviderProtocol.OpenAIResponses)]
    public void Resolve_OpenAiOSeriesExactModels_UsesDocumentedCapabilities(string model, ProviderProtocol protocol)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(ProviderPlatform.OpenAI, protocol, model);

        Assert.Equal(ProviderCapabilitySupport.Unknown, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Unknown, capabilities.TopP);
        Assert.True(capabilities.OmitSamplingParameters);
        Assert.Equal(protocol == ProviderProtocol.OpenAICompatible, capabilities.UsesMaxCompletionTokens);
        Assert.Equal(new[] { "high", "low", "medium" }, capabilities.ReasoningEffortValues.OrderBy(value => value));
        Assert.Equal(protocol == ProviderProtocol.OpenAICompatible, capabilities.UsesDeveloperRole);
        Assert.Equal(ProviderStructuredOutputSupport.JsonSchema, capabilities.StructuredOutput);
        Assert.StartsWith("openai-o-series-", capabilities.EvidenceId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProviderPlatform.OpenRouter, ProviderProtocol.OpenAICompatible, "openai/o3")]
    [InlineData(ProviderPlatform.OpenAI, ProviderProtocol.OpenAICompatible, "o3-custom")]
    [InlineData(ProviderPlatform.OpenAI, ProviderProtocol.OpenAICompatible, "o3-2025-04-17")]
    [InlineData(ProviderPlatform.OpenAI, ProviderProtocol.OpenAIResponses, "o4-mini-custom")]
    public void Resolve_OpenAiOSeriesCapabilities_DoNotLeakToProxyOrUnverifiedVariants(
        ProviderPlatform platform, ProviderProtocol protocol, string model)
    {
        var capabilities = ProviderCapabilityResolver.Resolve(platform, protocol, model);

        Assert.Equal("unknown", capabilities.EvidenceId);
        Assert.False(capabilities.UsesDeveloperRole);
    }

    [Fact]
    public void Describe_OpenAiOSeries_ExplainsSamplingFieldsAreOmittedByApplicationPolicy()
    {
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.OpenAI,
            ProviderProtocol.OpenAICompatible,
            "o3",
            InferenceLevel.Medium);

        Assert.Contains("官方资料没有明确列出", description, StringComparison.Ordinal);
        Assert.Contains("temperature/top_p", description, StringComparison.Ordinal);
        Assert.Contains("省略", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Capabilities_SiliconFlowDeepSeekV3UsesJsonModeWithoutAssumingStrictSchema()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.SiliconFlow,
            ProviderProtocol.OpenAICompatible,
            "deepseek-ai/DeepSeek-V3");

        Assert.Equal(ProviderStructuredOutputSupport.JsonObjectOnly, capabilities.StructuredOutput);
    }

    [Fact]
    public void Capabilities_SiliconFlowQwen25UsesDocumentedSamplingAndJsonModeWithoutLeakingToOtherModels()
    {
        var capabilities = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.SiliconFlow,
            ProviderProtocol.OpenAICompatible,
            "Qwen/Qwen2.5-7B-Instruct");
        var unverified = ProviderCapabilityResolver.Resolve(
            ProviderPlatform.SiliconFlow,
            ProviderProtocol.OpenAICompatible,
            "Qwen/Qwen3.8-Max");
        var description = ProviderCapabilityResolver.Describe(
            ProviderPlatform.SiliconFlow,
            ProviderProtocol.OpenAICompatible,
            "Qwen/Qwen2.5-7B-Instruct",
            InferenceLevel.Medium);

        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.Temperature);
        Assert.Equal(ProviderCapabilitySupport.Supported, capabilities.TopP);
        Assert.Equal(2.0, capabilities.MaximumTemperature);
        Assert.Equal(ProviderStructuredOutputSupport.JsonObjectOnly, capabilities.StructuredOutput);
        Assert.Contains("JSON Mode", description);
        Assert.Contains("不声明严格 JSON Schema", description);
        Assert.Equal("unknown", unverified.EvidenceId);
        Assert.Equal(ProviderStructuredOutputSupport.Unknown, unverified.StructuredOutput);
    }

    [Fact]
    public void Catalog_OpenAiIncludesCurrentGpt6ModelsWithoutChangingDefault()
    {
        var openAi = ProviderPlatformCatalog.Get(ProviderPlatform.OpenAI);
        var modelIds = Assert.IsAssignableFrom<IReadOnlyList<ModelDefinition>>(openAi.Models)
            .Select(model => model.ModelId)
            .ToHashSet();

        Assert.Equal("gpt-4o-mini", openAi.DefaultModel);
        Assert.Contains("gpt-6-astra", modelIds);
        Assert.Contains("gpt-6.1-sol", modelIds);
        Assert.Contains("gpt-6-sol", modelIds);
        Assert.Contains("gpt-6-luna", modelIds);
    }

    [Fact]
    public void Catalog_UnknownPersistedPlatformFallsBackToCustomInsteadOfCrashing()
    {
        var option = ProviderPlatformCatalog.Get((ProviderPlatform)999);

        Assert.Equal(ProviderPlatform.CustomOpenAICompatible, option.Platform);
    }

    [Fact]
    public void NormalizeProviderProfiles_DoesNotReclassifyAnExplicitPlatformFromItsEndpoint()
    {
        var profile = new ProviderProfile
        {
            Id = "company-proxy",
            Name = "公司代理",
            Platform = ProviderPlatform.OpenAI,
            ApiBase = "https://proxy.example.com/v1",
            Model = "company-model"
        };
        var settings = new AppSettings
        {
            ProviderProfiles = [profile],
            ActiveProviderProfileId = profile.Id
        };

        settings.NormalizeProviderProfiles();

        Assert.Equal(ProviderPlatform.OpenAI, profile.Platform);
    }

    [Fact]
    public void InferLegacyPlatform_RecognizesNewChineseVendors()
    {
        Assert.Equal(ProviderPlatform.Kimi, InferPlatform("https://api.moonshot.cn/v1"));
        Assert.Equal(ProviderPlatform.Zhipu, InferPlatform("https://open.bigmodel.cn/api/paas/v4"));
        Assert.Equal(ProviderPlatform.SiliconFlow, InferPlatform("https://api.siliconflow.cn/v1"));
        Assert.Equal(ProviderPlatform.StepFun, InferPlatform("https://api.stepfun.com/v1"));
        Assert.Equal(ProviderPlatform.MiniMax, InferPlatform("https://api.minimax.cn/v1"));
        Assert.Equal(ProviderPlatform.MiniMax, InferPlatform("https://api.minimax.chat/v1"));
        Assert.Equal(ProviderPlatform.OpenRouter, InferPlatform("https://openrouter.ai/api/v1"));
        Assert.Equal(ProviderPlatform.Grok, InferPlatform("https://api.x.ai/v1"));
        Assert.Equal(ProviderPlatform.Mistral, InferPlatform("https://api.mistral.ai/v1"));
        Assert.Equal(ProviderPlatform.Groq, InferPlatform("https://api.groq.com/openai/v1"));
        Assert.Equal(ProviderPlatform.Baichuan, InferPlatform("https://api.baichuan-ai.com/v1"));
        Assert.Equal(ProviderPlatform.Spark, InferPlatform("https://spark-api-open.xf-yun.com/v1"));
        Assert.Equal(ProviderPlatform.Yi, InferPlatform("https://api.lingyiwanwu.com/v1"));
        Assert.Equal(ProviderPlatform.Together, InferPlatform("https://api.together.xyz/v1"));
        Assert.Equal(ProviderPlatform.Together, InferPlatform("https://api.together.ai/v1"));
    }

    private static ProviderPlatform InferPlatform(string apiBase)
    {
        var profile = new ProviderProfile { ApiBase = apiBase };
        ProviderPlatformCatalog.InferLegacyPlatform(profile);
        return profile.Platform;
    }
}
