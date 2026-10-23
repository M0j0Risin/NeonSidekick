using System.Globalization;
using System.Text.Json;

namespace NeonSidekick.Network;

/// <summary>
/// A port list as the model and <c>/net</c> write it (2026-10-23, the admin review's U18: <c>/net</c>, in the domain layer, parsed it through the
/// tool layer's <c>NetworkTool.Ports</c>): numbers 1–65535, a JSON array or a string split on commas, spaces or semicolons, each once, at most
/// <see cref="NetworkChecks.MaxPortsPerCall"/>; refused at the first element past that many (K7: a 65,535-element array was parsed and deduplicated
/// in quadratic time before the cap). Pure.
/// </summary>
public static class NetworkPorts
{
    public static (IReadOnlyList<int> Ports, string? Error) Parse(object? sent)
    {
        var ports = new List<int>();
        string raw = sent switch
        {
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.GetRawText(),
            null => "",
            _ => sent.ToString() ?? "",
        };
        IEnumerable<string> parts = sent switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()),
            IEnumerable<object?> list when sent is not string => list.Select(o => o?.ToString() ?? ""),
            _ => raw.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries),
        };
        int seen = 0;
        foreach (string part in parts)
        {
            if (++seen > NetworkChecks.MaxPortsPerCall)
            {
                return ([], NetworkText.BadPorts(raw.Length > 200 ? raw[..200] : raw));
            }

            if (!int.TryParse(part.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
            {
                return ([], NetworkText.BadPorts(raw.Length > 200 ? raw[..200] : raw));
            }

            if (!ports.Contains(port))
            {
                ports.Add(port);
            }
        }

        return ports.Count == 0 ? ([], NetworkText.BadPorts(raw)) : (ports, null);
    }
}
