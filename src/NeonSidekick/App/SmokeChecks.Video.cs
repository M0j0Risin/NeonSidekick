namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>video:webview2</c> (2026-10-05, the YouTube plan): the video window's WebView2 in the published binary — the loader
    /// beside the exe, the raw vtables and the app-made handler objects under NativeAOT, the virtual host's page, web messages
    /// both ways, a script's answer and the navigation filter, over a hidden window and a temp user data folder
    /// (<see cref="Viewer.WebViewHost.Probe"/>). No network and nothing shown. Skipped off Windows and where no WebView2 Runtime
    /// is installed (the app says so in a sentence, and the tools point at open_url); a missing loader fails.
    /// </summary>
    public static SmokeCheck ProbeVideoWebView2()
    {
        const string name = "video:webview2";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            var (ok, detail) = Viewer.WebViewHost.Probe(TimeSpan.FromSeconds(30));
            return new SmokeCheck(name, ok, detail);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
