using System.Globalization;

namespace NeonSidekick.Monitors;

/// <summary>
/// The VESA Monitor Control Command Set as the monitors' input switch needs it (2026-10-23, moved over from the MonitorTrials
/// <c>DisplaySwitcherTest</c>): VCP code 0x60 (input source), its standard values' names (MCCS 2.2), the common four a monitor that lists
/// none is offered, a code typed as hex, and the 0x60 list read out of a capabilities string. The spike's XG27UQDMS lists
/// <c>60(0F 11 12)</c> beside four-digit values elsewhere (<c>72(5000 6400 …)</c>) and the LG <c>60(11 12 0F 10 )</c>; some monitors
/// leave the spaces out, so an unspaced list is read in pairs, and a four-digit value keeps its low byte (VCP 0x60's value is a byte:
/// <c>&amp; 0xFF</c>, as the current input's read). Pure.
/// </summary>
public static class Mccs
{
    /// <summary>VCP 0x60, the input source.</summary>
    public const byte InputSource = 0x60;

    /// <summary>The inputs offered when the monitor's capabilities list none: DisplayPort-1, DisplayPort-2, HDMI-1, HDMI-2 (the trial's fallback).</summary>
    public static readonly IReadOnlyList<byte> CommonInputs = [0x0F, 0x10, 0x11, 0x12];

    /// <summary>An input's standard MCCS name (<c>HDMI-1</c>), or null for a vendor's own code (USB-C is often 0x1B).</summary>
    public static string? StandardName(byte code) => code switch
    {
        0x01 => "VGA-1",
        0x02 => "VGA-2",
        0x03 => "DVI-1",
        0x04 => "DVI-2",
        0x05 => "Composite-1",
        0x06 => "Composite-2",
        0x07 => "S-Video-1",
        0x08 => "S-Video-2",
        0x09 => "Tuner-1",
        0x0A => "Tuner-2",
        0x0B => "Tuner-3",
        0x0C => "Component-1",
        0x0D => "Component-2",
        0x0E => "Component-3",
        0x0F => "DisplayPort-1",
        0x10 => "DisplayPort-2",
        0x11 => "HDMI-1",
        0x12 => "HDMI-2",
        _ => null,
    };

    /// <summary>A code as the app writes it: <c>0x1B</c>.</summary>
    public static string Hex(byte code) => "0x" + code.ToString("X2", CultureInfo.InvariantCulture);

    /// <summary>
    /// A typed code: <c>0x1B</c> or <c>1B</c> (any case, spaces trimmed), 0x01–0xFF; false for anything else, 0 among them (no input
    /// has it).
    /// </summary>
    public static bool TryParseCode(string? text, out byte code)
    {
        code = 0;
        string t = text?.Trim() ?? "";
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }

        if (t.Length is 0 or > 2 || !t.All(char.IsAsciiHexDigit)
            || !byte.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte value) || value == 0)
        {
            return false;
        }

        code = value;
        return true;
    }

    /// <summary>
    /// The inputs <paramref name="capabilities"/> lists for VCP 0x60, each once in its order, and whether it listed any; the common four
    /// (<see cref="CommonInputs"/>) when the string is null, has no <c>vcp(…)</c>, or no <c>60(…)</c> with a value in it.
    /// </summary>
    public static (IReadOnlyList<byte> Inputs, bool Reported) Inputs(string? capabilities)
    {
        var values = capabilities is null ? null : VcpValues(capabilities, InputSource);
        var inputs = values?.Where(v => v != 0).Distinct().ToList();
        return inputs is { Count: > 0 } ? (inputs, true) : (CommonInputs, false);
    }

    /// <summary>
    /// The values <paramref name="capabilities"/> lists for <paramref name="code"/> inside its <c>vcp(…)</c> group, or null when the code is
    /// not listed with values. Nested parentheses are skipped as a whole; codes are two hex digits, spaced or not.
    /// </summary>
    public static IReadOnlyList<byte>? VcpValues(string capabilities, byte code)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        int start = VcpStart(capabilities);
        if (start < 0)
        {
            return null;
        }

        var vcp = capabilities.AsSpan(start);
        int i = 0;
        while (i < vcp.Length && vcp[i] != ')')
        {
            if (!TryHexByte(vcp, i, out byte current))
            {
                i++;
                continue;
            }

            i += 2;
            while (i < vcp.Length && vcp[i] == ' ')
            {
                i++;
            }

            if (i < vcp.Length && vcp[i] == '(')
            {
                int close = Closing(vcp, i);
                if (close < 0)
                {
                    return null;
                }

                if (current == code)
                {
                    return Values(vcp[(i + 1)..close]);
                }

                i = close + 1;
            }
        }

        return null;
    }

    // The index just past "vcp(", the group itself (never "vcpname(", whose values are names).
    private static int VcpStart(string capabilities)
    {
        int from = 0;
        while (true)
        {
            int at = capabilities.IndexOf("vcp", from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return -1;
            }

            int j = at + 3;
            while (j < capabilities.Length && capabilities[j] == ' ')
            {
                j++;
            }

            if (j < capabilities.Length && capabilities[j] == '(')
            {
                return j + 1;
            }

            from = at + 3;
        }
    }

    // The ')' closing the '(' at open, nested groups counted; -1 when the string ends first.
    private static int Closing(ReadOnlySpan<char> text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    // A value list: spaced tokens keep each token's low byte (a four-digit value's last two digits); an unspaced run is read in pairs.
    private static List<byte> Values(ReadOnlySpan<char> text)
    {
        var values = new List<byte>();
        string list = text.ToString();
        if (list.Trim().Contains(' ', StringComparison.Ordinal))
        {
            foreach (string token in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length is 2 or 4 && token.All(char.IsAsciiHexDigit))
                {
                    values.Add(byte.Parse(token.AsSpan(token.Length - 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                }
                else if (token.Length > 4)
                {
                    values.AddRange(Pairs(token));
                }
            }

            return values;
        }

        values.AddRange(Pairs(list.Trim()));
        return values;
    }

    private static IEnumerable<byte> Pairs(string text)
    {
        for (int i = 0; i + 1 < text.Length; i += 2)
        {
            if (TryHexByte(text, i, out byte value))
            {
                yield return value;
            }
        }
    }

    private static bool TryHexByte(ReadOnlySpan<char> text, int index, out byte value)
    {
        value = 0;
        if (index + 1 >= text.Length || !char.IsAsciiHexDigit(text[index]) || !char.IsAsciiHexDigit(text[index + 1]))
        {
            return false;
        }

        value = byte.Parse(text.Slice(index, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return true;
    }
}
