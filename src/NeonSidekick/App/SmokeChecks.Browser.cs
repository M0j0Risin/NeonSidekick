namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>browser:webview2</c> (2026-10-13, the bot checks): the Browser window's WebView2 in the published binary — the slots the video
    /// window never binds (navigation completed with its status, the address, DevTools' cookies with the DevTools window off, PrintToPdf
    /// and its BOOL handler, a download cancelled) and the browsing profile's navigation filter, over a hidden window and a temp user data
    /// folder (<see cref="Viewer.WebViewHost.ProbeBrowser"/>). No network and nothing shown. Skipped off Windows and where no WebView2
    /// Runtime is installed; a missing loader fails.
    /// </summary>
    public static SmokeCheck ProbeBrowserWebView2()
    {
        const string name = "browser:webview2";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            var (ok, detail) = Viewer.WebViewHost.ProbeBrowser(TimeSpan.FromSeconds(30));
            return new SmokeCheck(name, ok, detail);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
