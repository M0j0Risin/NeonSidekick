// SPDX-License-Identifier: AGPL-3.0-or-later

namespace SearXNG.Net.Locales;

/// <summary>Picks the engine's locale that best fits a search's: a port of <c>get_engine_locale</c> in <c>searx/locales.py</c>.</summary>
internal static class EngineLocale
{
    /// <summary>Finds the best fit for <paramref name="locale"/> in what the engine supports, or returns <paramref name="fallback"/>.</summary>
    public static string? Resolve(string locale, IReadOnlyDictionary<string, string> engineLocales, string? fallback)
    {
        var tag = NormalizeCase(locale);
        if (engineLocales.TryGetValue(tag, out var exact))
        {
            return exact;
        }

        // A tag Babel can't resolve counts as its bare language; a language it doesn't know gets nothing.
        if (!LocaleData.Resolved.TryGetValue(tag, out var resolved) && !LocaleData.Resolved.TryGetValue(tag.Split('-')[0], out resolved))
        {
            return fallback;
        }

        var (language, script, territory) = Split(resolved);
        var languageTag = script is null ? language : language + "_" + script;
        if (engineLocales.TryGetValue(languageTag, out var byLanguageTag))
        {
            return byLanguageTag;
        }

        // First rule: a territory's official languages take priority over the language asked for.
        if (territory is not null && LocaleData.OfficialLanguages.TryGetValue(territory, out var officials))
        {
            foreach (var official in officials)
            {
                if (engineLocales.TryGetValue(official + "-" + territory, out var byTerritory))
                {
                    return byTerritory;
                }
            }
        }

        // Second rule: other territories where the language is official, starting with its own country (de-DE, en-US).
        if (LocaleData.LanguageTerritories.TryGetValue(languageTag, out var territories))
        {
            var home = language == "en" ? "US" : language.ToUpperInvariant();
            if (Array.IndexOf(territories, home) >= 0 && engineLocales.TryGetValue(language + "-" + home, out var byHome))
            {
                return byHome;
            }

            foreach (var other in territories)
            {
                if (engineLocales.TryGetValue(language + "-" + other, out var byOther))
                {
                    return byOther;
                }
            }
        }

        return fallback;
    }

    /// <summary>
    /// Writes a tag the way the tables do, as Babel reads it: <c>en</c>, <c>Latn</c>, <c>US</c>. SearXNG looks for a market before
    /// Babel gets to read the tag, so there <c>en-in</c> and <c>en-IN</c> can differ. Here they don't.
    /// </summary>
    private static string NormalizeCase(string locale)
    {
        var parts = locale.Split('-');
        parts[0] = parts[0].ToLowerInvariant();
        for (int i = 1; i < parts.Length; i++)
        {
            parts[i] = parts[i].Length switch
            {
                2 => parts[i].ToUpperInvariant(),
                4 when parts[i].All(char.IsAsciiLetter) => char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant(),
                _ => parts[i],
            };
        }

        return string.Join('-', parts);
    }

    private static (string Language, string? Script, string? Territory) Split(string resolved)
    {
        var parts = resolved.Split('-');
        return parts.Length switch
        {
            1 => (parts[0], null, null),
            2 when parts[1].Length == 4 => (parts[0], parts[1], null),
            2 => (parts[0], null, parts[1]),
            _ => (parts[0], parts[1], parts[2]),
        };
    }
}
