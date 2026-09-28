using NeonSidekick.HomeAssistant;

namespace NeonSidekick.Tests;

/// <summary>
/// Resolves a live Home Assistant once per assembly (2026-09-28), the way <see cref="LiveTtsServer"/> does for Kokoro: the
/// server and a long-lived access token from the two test variables, probed with <c>GET /api/</c>. Only ever read from —
/// the live tests never call a service.
/// </summary>
internal static class LiveHomeAssistant
{
    /// <summary>Test-specific, so pointing the app somewhere does not silently point the suite there too.</summary>
    public const string UrlVariable = "NEONSIDEKICK_TEST_HA_URL";

    public const string TokenVariable = "NEONSIDEKICK_TEST_HA_TOKEN";

    public static readonly Uri? BaseUrl;
    public static readonly string Token = "";
    public static readonly string Unavailable;

    static LiveHomeAssistant()
    {
        string? raw = Environment.GetEnvironmentVariable(UrlVariable);
        string? token = Environment.GetEnvironmentVariable(TokenVariable);
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(token))
        {
            Unavailable = $"Set {UrlVariable} and {TokenVariable} to run the live Home Assistant tests.";
            return;
        }

        try
        {
            var url = new Uri(raw.Trim(), UriKind.Absolute);
            using var client = new HaClient(url, token);
            var ping = client.PingAsync(TimeSpan.FromSeconds(5), CancellationToken.None).GetAwaiter().GetResult();
            if (!ping.Ok)
            {
                Unavailable = $"No Home Assistant at {url}: {ping.Error}";
                return;
            }

            BaseUrl = url;
            Token = token.Trim();
            Unavailable = "";
        }
        catch (Exception ex)
        {
            Unavailable = "Live Home Assistant probe failed: " + ex.Message;
        }
    }
}

/// <summary>Skips unless a Home Assistant answers with the test token. A local gate, never a CI safety net.</summary>
public sealed class LiveHomeAssistantFactAttribute : FactAttribute
{
    public LiveHomeAssistantFactAttribute()
    {
        if (LiveHomeAssistant.BaseUrl is null)
        {
            Skip = LiveHomeAssistant.Unavailable;
        }
    }
}
