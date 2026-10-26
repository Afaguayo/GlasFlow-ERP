using System.Text.Json;

namespace GlasFlow.Desktop.Services;

public static class Settings
{
    public const string DefaultApiUrl = "http://localhost:5080";

    /// <summary>Env var GLASFLOW_API_URL, else appsettings.json next to the exe, else the default.</summary>
    public static string ResolveApiUrl()
    {
        var env = Environment.GetEnvironmentVariable("GLASFLOW_API_URL");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.TryGetProperty("ApiUrl", out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString()!.Trim();
            }
        }
        catch { /* unreadable settings: fall back to the default */ }
        return DefaultApiUrl;
    }
}
