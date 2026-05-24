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
        Dictionary<string, string[]> errors = new(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(Model))
        {
            errors[nameof(Model)] = ["Model is required."];
        }

        if (Messages is null || Messages.Count == 0)
        {
            errors[nameof(Messages)] = ["At least one message is required."];
            return errors;
        }

        if (Temperature is < 0 or > 2)
        {
            errors[nameof(Temperature)] = ["Temperature must be between 0 and 2."];
        }

        for (int index = 0; index < Messages.Count; index++)
        {
            MessageDto message = Messages[index];
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
}

/// <summary>
/// HTTP request message payload.
/// </summary>
public sealed record MessageDto(string Role, string Content);
