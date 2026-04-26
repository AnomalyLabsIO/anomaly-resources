using System.Net.Http.Json;

namespace ManifestAnalyzer;

internal sealed class DiscordNotifier
{
    private readonly string _webhookUrl;
    private readonly HttpClient _http;

    public DiscordNotifier(string webhookUrl)
    {
        _webhookUrl = webhookUrl;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public static DiscordNotifier? FromEnv(string? envVarName)
    {
        if (string.IsNullOrWhiteSpace(envVarName))
            return null;

        var url = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return new DiscordNotifier(url);
    }

    public Task NotifySuccessAsync(string gameVersion, string manifestId, string branch, string? buildId, CancellationToken ct)
    {
        var content = $"🆕 SCP:SL build **{gameVersion}** detected on `{branch}` (manifest `{manifestId}`{(buildId != null ? $", build `{buildId}`" : string.Empty)}).";
        return PostAsync(content, ct);
    }

    public Task NotifyDiscoveredAsync(string manifestId, string branch, string? buildId, CancellationToken ct)
    {
        var content = $"🔍 New SCP:SL manifest discovered on `{branch}`: `{manifestId}`{(buildId != null ? $" (build `{buildId}`)" : string.Empty)}. Resolving game version...";
        return PostAsync(content, ct);
    }

    public Task NotifyUnresolvedAsync(string manifestId, string branch, string reason, CancellationToken ct)
    {
        var content = $"⚠️ SCP:SL manifest `{manifestId}` on `{branch}` could not be resolved: {reason}";
        return PostAsync(content, ct);
    }

    public Task NotifyAuthFailureAsync(string reason, CancellationToken ct)
    {
        var content = $"🔒 SCP:SL manifest tracker auth failed: {reason}\nRotate `STEAM_USERNAME` / `STEAM_PASSWORD` / `STEAM_SHARED_SECRET` secrets.";
        return PostAsync(content, ct);
    }

    public Task NotifyRunFailureAsync(string reason, CancellationToken ct)
    {
        var content = $"❌ SCP:SL manifest tracker run failed: {reason}";
        return PostAsync(content, ct);
    }

    private async Task PostAsync(string content, CancellationToken ct)
    {
        try
        {
            using var response = await _http
                .PostAsJsonAsync(_webhookUrl, new { content }, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Discord webhook returned {(int)response.StatusCode}: {response.ReasonPhrase}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Discord webhook post failed: {ex.Message}");
        }
    }
}
