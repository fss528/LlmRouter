using System.Text.Json.Serialization;
using LlmRouter.Core.Models;

namespace LlmRouter.Api.Dtos;

/// <summary>
/// HTTP request payload for chat completions.
/// </summary>
public sealed record ChatRequestDto(
    string Model,
    IReadOnlyList<MessageDto> Messages,
    int? MaxTokens = null,
    double? Temperature = null,
    RoutingStrategy? Strategy = null)
{
    /// <summary>
    /// Converts the transport DTO to the provider-neutral domain model.
    /// </summary>
    public ChatRequest ToDomain()
    {
        return new ChatRequest(
            Model,
            Messages.Select(message => new Message(message.Role, message.Content)).ToList(),
            MaxTokens,
            Temperature,
            Strategy ?? RoutingStrategy.LeastLatency);
    }

    /// <summary>
    /// Validates the request shape before calling the domain service.
    /// </summary>
    public IDictionary<string, string[]> Validate()
    {
        return ChatDtoValidator.Validate(Model, Messages, MaxTokens, Temperature);
    }
}

/// <summary>
/// OpenAI-compatible chat completion request payload.
/// </summary>
public sealed record OpenAiChatRequestDto(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<MessageDto> Messages,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("stream")] bool Stream = false)
{
    /// <summary>Converts an OpenAI-compatible payload to the router domain request.</summary>
    public ChatRequest ToDomain(RoutingStrategy strategy)
    {
        return new ChatRequest(
            Model,
            Messages.Select(message => new Message(message.Role, message.Content)).ToList(),
            MaxTokens,
            Temperature,
            strategy);
    }

    /// <summary>Validates request shape.</summary>
    public IDictionary<string, string[]> Validate()
    {
        return ChatDtoValidator.Validate(Model, Messages, MaxTokens, Temperature);
    }
}

/// <summary>
/// HTTP request message payload.
/// </summary>
public sealed record MessageDto(string Role, string Content);

/// <summary>
/// Discovery response returned by GET /v1/models.
/// </summary>
public sealed record ModelsCatalogResponse(
    IReadOnlyList<ModelAliasInfo> Aliases,
    IReadOnlyList<ProviderModelsInfo> Providers);

/// <summary>
/// Concrete models and known pricing exposed by a registered provider.
/// </summary>
public sealed record ProviderModelsInfo(
    string Provider,
    IReadOnlyList<ModelPricingEntry> Models);

/// <summary>
/// Single model entry with optional pricing metadata.
/// </summary>
public sealed record ModelPricingEntry(
    string Model,
    ModelPricing? Pricing);

internal static class ChatDtoValidator
{
    internal static IDictionary<string, string[]> Validate(
        string? model,
        IReadOnlyList<MessageDto>? messages,
        int? maxTokens,
        double? temperature)
    {
        Dictionary<string, string[]> errors = new(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(model))
        {
            errors["Model"] = ["Model is required."];
        }

        if (MaxTokensInvalid(maxTokens))
        {
            errors["MaxTokens"] = ["MaxTokens must be greater than 0."];
        }

        if (temperature is < 0 or > 2)
        {
            errors["Temperature"] = ["Temperature must be between 0 and 2."];
        }

        if (messages is null || messages.Count == 0)
        {
            errors["Messages"] = ["At least one message is required."];
            return errors;
        }

        for (int index = 0; index < messages.Count; index++)
        {
            MessageDto message = messages[index];
            if (string.IsNullOrWhiteSpace(message.Role))
            {
                errors[$"Messages[{index}].Role"] = ["Role is required."];
            }

            if (string.IsNullOrWhiteSpace(message.Content))
            {
                errors[$"Messages[{index}].Content"] = ["Content is required."];
            }
        }

        return errors;
    }

    private static bool MaxTokensInvalid(int? maxTokens) => maxTokens is <= 0;
}
