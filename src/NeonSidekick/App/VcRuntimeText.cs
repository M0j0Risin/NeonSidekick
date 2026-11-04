namespace NeonSidekick.App;

/// <summary>The Visual C++ runtime's message (2026-11-04). No period or parentheses of its own: it goes inside the <c>STT: unavailable (...)</c> and <c>StartFailed</c> lines, and a period would end up in the link.</summary>
public static class VcRuntimeText
{
    public const string InstallerUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist";

    public static string Missing(string library) => $"Microsoft's Visual C++ runtime isn't installed, {library} is missing: {InstallerUrl}";
}
