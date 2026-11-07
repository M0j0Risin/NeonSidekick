// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;

namespace SearXNG.Net.Net;

/// <summary>Reading the engines' JSON.</summary>
internal static class EngineJson
{
    /// <summary>A string field, or null when it's missing, not a string, or holds half a surrogate pair, which <c>GetString</c> refuses.</summary>
    public static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
