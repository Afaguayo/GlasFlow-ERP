using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GlasFlow.Application.Services;
using Microsoft.Extensions.Options;

namespace GlasFlow.Infrastructure.Ai;

/// <summary>
/// AI configuration. The API key is read from configuration (env var Ai__ApiKey or GLASFLOW_AI_API_KEY);
/// it is never stored in appsettings or source control.
/// </summary>
public class AiOptions
{
    public const string Section = "Ai";
    /// <summary>none | anthropic | openai (any OpenAI-compatible chat-completions endpoint).</summary>
    public string Provider { get; set; } = "none";
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    /// <summary>Base URL for OpenAI-compatible providers, e.g. https://api.openai.com/v1 or a local gateway.</summary>
    public string? BaseUrl { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxTokens { get; set; } = 600;
}

public class AnthropicProvider(HttpClient http, IOptions<AiOptions> options) : IAiProvider
{
    private AiOptions O => options.Value;
    public string Name => $"anthropic:{Model}";
    public bool IsConfigured => O.Provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(O.ApiKey);
    private string Model => string.IsNullOrWhiteSpace(O.Model) ? "claude-sonnet-5-5" : O.Model;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, (O.BaseUrl ?? "https://api.anthropic.com").TrimEnd('/') + "/v1/messages");
        request.Headers.Add("x-api-key", O.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = JsonContent.Create(new
        {
            model = Model,
            max_tokens = O.MaxTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } }
        });
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
    }
}

public class OpenAiCompatibleProvider(HttpClient http, IOptions<AiOptions> options) : IAiProvider
{
    private AiOptions O => options.Value;
    public string Name => $"openai-compatible:{O.Model}";
    public bool IsConfigured => O.Provider.Equals("openai", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(O.ApiKey) && !string.IsNullOrWhiteSpace(O.Model);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, (O.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", O.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = O.Model,
            max_tokens = O.MaxTokens,
            messages = new[] { new { role = "system", content = systemPrompt }, new { role = "user", content = userPrompt } }
        });
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
