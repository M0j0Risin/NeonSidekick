using NeonSidekick.Diagnostics;

namespace NeonSidekick.Llm;

/// <summary>
/// One short request to a server's native endpoint, the transport both connect-side probes share
/// (<see cref="ContextLengthProbe"/>, and <see cref="ServerSamplingProbe"/> since 2026-09-28): a per-call ceiling
/// linked to the caller's token, a POST when there is a body, the key as a Bearer header when there is one, and the
/// body's text on a 2xx. Anything else — a status, a timeout, a refused connection — is null and one Debug line.
/// A cancel by the caller is null too, never thrown.
/// </summary>
internal static class NativeRequest
{
    public static async Task<string?> TextAsync(HttpClient http, Uri url, string? apiKey, TimeSpan timeout, string category, CancellationToken cancellationToken, string? body = null)
    {
        try
        {
            // A per-call ceiling on a probe, not the turn budget (the linked-CTS rule is about the turn path).
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);

            using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
            if (body is not null)
            {
                request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DiagnosticLog.Debug(category, $"{url.AbsolutePath}: {(int)response.StatusCode}.");
                return null;
            }

            return await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Debug(category, $"{url.AbsolutePath}: {ex.Message}");
            return null;
        }
    }
}
