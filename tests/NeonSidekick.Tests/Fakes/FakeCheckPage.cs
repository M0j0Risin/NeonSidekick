using NeonSidekick.Viewer;
using NeonSidekick.Web;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A Browser window as the shared bot check watch sees it (<see cref="IBrowserCheckPage"/>, 2026-10-20, the code review's G8): every script,
/// print and cookie read held until the test answers it, so a test can let an answer arrive late — after the check began to finish, after it
/// ended — as WebKit's and WebView2's callbacks do. The window's end is its <c>Finish</c>'s: once per check, as both windows'.
/// </summary>
internal sealed class FakeCheckPage(BrowserCheck? holds = null) : IBrowserCheckPage
{
    /// <summary>The check the window is on (null: any check is still its own).</summary>
    public BrowserCheck? Holding { get; set; } = holds;

    public string? Source { get; set; } = "https://example.com/";

    /// <summary>False: no script can be sent (no view), and Evaluate says so.</summary>
    public bool CanEvaluate { get; set; } = true;

    /// <summary>False: no print can start.</summary>
    public bool CanPrint { get; set; } = true;

    /// <summary>The scripts sent, each with its answer still to give.</summary>
    public List<(string Expression, Action<string?> Answer)> Evaluations { get; } = [];

    public List<(string Path, Action<bool> Written)> Prints { get; } = [];

    public List<(IReadOnlyList<string> Hosts, Action<IReadOnlyList<BrowserCookie>> Answer)> CookieReads { get; } = [];

    /// <summary>Every end the window carried out (once per check), with whether it closed.</summary>
    public List<(BotCheckResult Result, bool Close)> Finishes { get; } = [];

    public BrowserIdentity Identity { get; } = BrowserIdentity.Safari("26.6");

    public bool Holds(BrowserCheck check) => Holding is null || Holding == check;

    public bool Evaluate(string expression, Action<string?> answer)
    {
        if (!CanEvaluate)
        {
            return false;
        }

        Evaluations.Add((expression, answer));
        return true;
    }

    public bool PrintToPdf(string path, Action<bool> written)
    {
        if (!CanPrint)
        {
            return false;
        }

        Prints.Add((path, written));
        return true;
    }

    public bool ReadCookies(IReadOnlyList<string> hosts, Action<IReadOnlyList<BrowserCookie>> answer)
    {
        CookieReads.Add((hosts, answer));
        return true;
    }

    /// <summary>The window's say on a look's document: by default its own status, and no error page.</summary>
    public Func<BrowserLook, (int Status, bool ErrorPage)> Describing { get; set; } = look => (look.Status, false);

    public (int Status, bool ErrorPage) Describe(BrowserLook look) => Describing(look);

    public void Finish(BrowserCheck check, BotCheckResult result, bool close)
    {
        if (check.TryEnd())
        {
            Finishes.Add((result, close));
        }
    }
}
