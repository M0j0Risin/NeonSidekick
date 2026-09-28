using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.Settings;

/// <summary>
/// The three secrets a <c>profile.json</c> keeps — <see cref="AppSettingsData.LlmApiKey"/>, <see cref="AppSettingsData.ClaudeApiKey"/>
/// and <see cref="AppSettingsData.HomeAssistantToken"/> — encrypted with DPAPI for this Windows user on this machine
/// (<see cref="WindowsCredentials.Protect"/>: <c>dpapi:</c> and the blob). The Claude key was first (2026-09-27), the Home
/// Assistant key next (2026-09-28); the LLM API key joined them the same day (the user's call: "since we've done it with all
/// the rest"), and with it <see cref="ProtectAtRest"/>, which encrypts a plain value of any of the three when the profile
/// loads (the user's call: all three, so a hand edit or a file from before is protected without retyping). A plain value
/// still reads — a variable, a hand edit, a machine where DPAPI failed — and the values are never logged.
/// </summary>
public static class SettingsSecrets
{
    private const string Category = "Settings";

    /// <summary>
    /// The secret, readable: null for none, a plain value as it is, a <c>dpapi:</c> one decrypted. Null too when the stored
    /// value cannot be read — another Windows user's or machine's profile — which the callers read as "not set".
    /// </summary>
    public static string? Reveal(string? stored)
    {
        string value = stored?.Trim() ?? "";
        if (value.Length == 0)
        {
            return null;
        }

        if (!WindowsCredentials.IsProtected(value))
        {
            return value;
        }

        var result = WindowsCredentials.Unprotect(value);
        return string.IsNullOrWhiteSpace(result.Value) ? null : result.Value;
    }

    /// <summary>
    /// The secret as the settings file keeps it: <paramref name="plain"/> encrypted (<see cref="WindowsCredentials.Protect"/>);
    /// empty for empty, an already encrypted value as it is. Where DPAPI is unavailable the plain value is kept and
    /// <paramref name="error"/> says why, so the caller can warn.
    /// </summary>
    public static string Protect(string plain, out string? error)
    {
        ArgumentNullException.ThrowIfNull(plain);
        error = null;
        string trimmed = plain.Trim();
        if (trimmed.Length == 0 || WindowsCredentials.IsProtected(trimmed))
        {
            return trimmed;
        }

        var result = WindowsCredentials.Protect(trimmed);
        if (result.Value is { } value)
        {
            return value;
        }

        error = result.Error;
        return trimmed;
    }

    /// <summary>
    /// Encrypts, in place, whichever of the three secrets is stored plain; true when any changed, so the loader writes the
    /// file back. The LLM key's placeholder <see cref="Llm.LlmEndpoint.DefaultApiKey"/> stays as it is: it is no secret, and
    /// "not set" is told by comparing with it. Where DPAPI fails the values stay plain and one warning says so.
    /// </summary>
    public static bool ProtectAtRest(AppSettingsData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        string? error = null;
        bool changed = false;
        if (NeedsProtecting(data.LlmApiKey) && data.LlmApiKey.Trim() != Llm.LlmEndpoint.DefaultApiKey)
        {
            changed |= Swap(data.LlmApiKey, v => data.LlmApiKey = v, ref error);
        }

        if (NeedsProtecting(data.ClaudeApiKey))
        {
            changed |= Swap(data.ClaudeApiKey, v => data.ClaudeApiKey = v, ref error);
        }

        if (NeedsProtecting(data.HomeAssistantToken))
        {
            changed |= Swap(data.HomeAssistantToken, v => data.HomeAssistantToken = v, ref error);
        }

        if (error is not null)
        {
            DiagnosticLog.Warn(Category, $"A key in the profile stays unencrypted: {error}.");
        }

        return changed;
    }

    private static bool NeedsProtecting(string? stored) => !string.IsNullOrWhiteSpace(stored) && !WindowsCredentials.IsProtected(stored.Trim());

    private static bool Swap(string plain, Action<string> set, ref string? error)
    {
        string stored = Protect(plain, out string? failed);
        if (failed is not null)
        {
            error ??= failed;
            return false;
        }

        set(stored);
        return true;
    }
}
