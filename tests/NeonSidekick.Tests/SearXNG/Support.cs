// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;

namespace SearXNG.Net.Tests;

internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "searxng", name), Encoding.UTF8);
}
