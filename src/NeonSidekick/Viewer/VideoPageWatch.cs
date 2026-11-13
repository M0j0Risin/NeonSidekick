namespace NeonSidekick.Viewer;

/// <summary>What the video window does with what waits for its page (<see cref="VideoPageWatch.Next"/>).</summary>
public enum VideoPageStep
{
    /// <summary>The page is still loading (or none was asked for): everything waits for its <c>page</c> message.</summary>
    Wait,

    /// <summary>The page can hear it: the waiting request and the commands go now.</summary>
    Send,

    /// <summary>The waiting request needs the other page, or the last page never came: navigate to the request's page first.</summary>
    Navigate,
}

/// <summary>
/// The video window's page bookkeeping (2026-11-13, the review's fix), pure so it can be tested without a window: which page the view
/// was last navigated to (the file page or the YouTube player, <see cref="VideoPage.NeedsSwitch"/>), whether it said it was ready, and
/// whether its wait ran out. Before it, a page that never posted its <c>page</c> message left every later play and command waiting for
/// good. Now the window arms a timer at each navigation; when it fires first, <see cref="TimedOut"/> marks the page lost, the window
/// drops what waited and fails the snapshot, and the next play navigates again instead of queuing behind a page that won't come. A
/// page that arrives late only marks itself ready: nothing waits to flush into it. The window's thread only.
/// </summary>
public sealed class VideoPageWatch
{
    /// <summary>A watch on the window's first navigation: to the file page when <paramref name="showingFile"/>, else the YouTube player.</summary>
    public VideoPageWatch(bool showingFile) => ShowingFile = showingFile;

    /// <summary>Whether the view was last navigated to the file page (else the YouTube player).</summary>
    public bool ShowingFile { get; private set; }

    /// <summary>Whether that page said it was ready.</summary>
    public bool Ready { get; private set; }

    /// <summary>Whether that page's wait ran out before it said so.</summary>
    public bool Expired { get; private set; }

    /// <summary>The view navigated to the file page (<paramref name="file"/>) or the YouTube player: its wait starts over.</summary>
    public void Navigated(bool file)
    {
        ShowingFile = file;
        Ready = false;
        Expired = false;
    }

    /// <summary>
    /// A page said it was ready: true when it is the kind the view was navigated to (it is ready now, even after its wait ran out),
    /// false for a late one from the page just left, whose flush would load into the wrong page.
    /// </summary>
    public bool Arrived(bool filePage)
    {
        if (filePage != ShowingFile)
        {
            return false;
        }

        Ready = true;
        Expired = false;
        return true;
    }

    /// <summary>The page's timer fired: true when the page still hadn't come (now <see cref="Expired"/>), false when it had.</summary>
    public bool TimedOut()
    {
        if (Ready)
        {
            return false;
        }

        Expired = true;
        return true;
    }

    /// <summary>
    /// What to do with <paramref name="pending"/> (null with only commands waiting): navigate when it needs the other page or the last
    /// page was lost, send when the page is ready, else wait.
    /// </summary>
    public VideoPageStep Next(VideoRequest? pending)
    {
        if (pending is not null && (Expired || (Ready && VideoPage.NeedsSwitch(ShowingFile, pending))))
        {
            return VideoPageStep.Navigate;
        }

        return Ready ? VideoPageStep.Send : VideoPageStep.Wait;
    }
}
