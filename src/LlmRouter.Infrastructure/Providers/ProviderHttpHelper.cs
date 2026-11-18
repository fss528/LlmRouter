using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmRouter.Core.Models;
using Microsoft.Extensions.Configuration;

namespace LlmRouter.Infrastructure.Providers;

internal static class ProviderHttpHelper
{
    private const int MaxErrorBodyPreviewLength = 512;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static string? ResolveApiKey(string? configuredOptionKey, IConfiguration? configuration, string configPath, string envVarName)
    {
        string? envValue = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return envValue;
        }

        if (!string.IsNullOrWhiteSpace(configuredOptionKey))
        {
            return configuredOptionKey;
        }

        string? configValue = configuration?[configPath];
        return string.IsNullOrWhiteSpace(configValue) ? null : configValue;
    }

    internal static string GetRequiredApiKey(string? configuredOptionKey, IConfiguration? configuration, string configPath, string envVarName, string providerName)
    {
        string? apiKey = ResolveApiKey(configuredOptionKey, configuration, configPath, envVarName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Missing {providerName} API key. Set {envVarName} or {configPath}.");
        }

        return apiKey;
    }

    internal static (string? SystemPrompt, IReadOnlyList<Message> ConversationMessages) SplitSystemMessages(IReadOnlyList<Message> messages)
    {
        List<string> systemParts = [];
        List<Message> conversation = new(messages.Count);

        foreach (Message message in messages)
        {
            if (message.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                systemParts.Add(message.Content);
            }
            else
            {
                conversation.Add(message);
            }
        }

        string? systemPrompt = systemParts.Count > 0 ? string.Join("\n\n", systemParts) : null;
        return (systemPrompt, conversation);
    }

    internal static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, string providerName, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string preview = string.Empty;
        try
        {
            string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                preview = raw.Length <= MaxErrorBodyPreviewLength
                    ? raw
                    : string.Concat(raw.AsSpan(0, MaxErrorBodyPreviewLength), "...");
            }
        }
        catch
        {
            // Ignore body read failures when building error context.
        }

        string message = string.IsNullOrWhiteSpace(preview)
            ? $"{providerName} returned HTTP {(int)response.StatusCode} ({response.StatusCode})."
            : $"{providerName} returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {preview}";

        HttpRequestException exception = new(message, null, response.StatusCode);

        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
        {
            exception.Data["RetryAfter"] = delta;
        }
        else if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            TimeSpan computed = date - DateTimeOffset.UtcNow;
            if (computed > TimeSpan.Zero)
            {
                exception.Data["RetryAfter"] = computed;
            }
        }

        throw exception;
    }

    internal static async IAsyncEnumerable<(string? EventName, string Data)> ReadSseEventsAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using StreamReader reader = new(stream);
        string? currentEvent = null;
        List<string> dataLines = [];

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                if (dataLines.Count > 0)
                {
                    yield return (currentEvent, string.Join("\n", dataLines));
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return (currentEvent, string.Join("\n", dataLines));
                    dataLines.Clear();
                }

                currentEvent = null;
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                currentEvent = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                dataLines.Add(line[5..].TrimStart());
            }
        }
    }
}
