using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>What a navigation decision answers WebKit (<c>WKNavigationActionPolicy</c> / <c>WKNavigationResponsePolicy</c>: the values agree).</summary>
internal enum MacNavigationPolicy
{
    Cancel = 0,
    Allow = 1,
    Download = 2,
}

/// <summary>What the Browser window's web view tells its window (<see cref="MacBrowserKit"/>). Main thread; nothing here may throw into WebKit.</summary>
internal interface IBrowserViewSink
{
    /// <summary>A navigation about to start: <paramref name="mainFrame"/> false for a frame inside the page; <paramref name="download"/> for a link with <c>download</c>.</summary>
    MacNavigationPolicy DecideAction(string? url, bool mainFrame, bool download);

    /// <summary>A navigation's answer: whether WebKit can show it, and whether it says to save it (<c>Content-Disposition: attachment</c>).</summary>
    MacNavigationPolicy DecideResponse(string? url, bool mainFrame, int status, string? mime, bool canShow, bool attachment);

    void Started();

    void Finished();

    /// <summary>A navigation failed (<paramref name="provisional"/>: before anything was shown), with the error's domain, code and words.</summary>
    void Failed(bool provisional, string? domain, long code, string message, string? failingUrl);

    /// <summary>One of the view's observed properties changed (<see cref="MacBrowserKit.Observed"/>).</summary>
    void Changed(string key);

    /// <summary>A new window the page asked for: never made; the address handed here (navigated in place).</summary>
    void NewWindow(string? url);

    /// <summary>The page's web content process ended (a crash, or macOS took it back).</summary>
    void ContentEnded();

    /// <summary>The pointer is over a link (its address) or left one (empty): the status bubble.</summary>
    void Hovered(string href);

    /// <summary>A navigation became a download: the window keeps it (and sets itself as its delegate through the kit).</summary>
    void DownloadBegan(nint download);

    /// <summary>Where a download goes, a full path; null cancels it.</summary>
    string? DownloadDestination(nint download, string? suggested);

    /// <summary>A download ended: finished, or failed with WebKit's words.</summary>
    void DownloadEnded(nint download, bool ok, string? error);

    void Alert(string message, string? origin, HeldHandler done);

    void Confirm(string message, string? origin, HeldHandler done);

    void Prompt(string message, string? defaultText, string? origin, HeldHandler done);

    void OpenPanel(bool multiple, bool directories, HeldHandler done);

    /// <summary>An HTTP Basic or Digest sign-in: <paramref name="done"/> answers with a disposition and a credential (<see cref="MacBrowserKit.AnswerSignIn"/>).</summary>
    void SignIn(string host, string? realm, long failures, HeldHandler done);

    /// <summary>The page called <c>window.print()</c>: <paramref name="done"/> (null for the older form) is answered when the print sheet ends.</summary>
    void PrintRequested(HeldHandler? done);

    /// <summary>A print run by <see cref="MacBrowserKit.Print"/> ended: its token, and whether it ran.</summary>
    void PrintEnded(long token, bool ok);
}

/// <summary>
/// WebKit for the Mac's Browser window (2026-10-18, the Mac Browser plan), the native part beside <see cref="MacWebKit"/> (the video page's):
/// <c>NeonSidekickBrowserDelegate</c>, an NSObject made at runtime declaring <c>WKNavigationDelegate</c>, <c>WKUIDelegate</c>,
/// <c>WKDownloadDelegate</c> and <c>WKScriptMessageHandler</c>, with KVO on the view's <see cref="Observed"/> properties; the view's
/// configuration (the profile's data store, element full screen, windows a script opens allowed — they are navigated in place — Safari's
/// autoplay rule, magnification and swipes, Safari's user agent); and the window's async calls, each answered through a block on the main
/// queue (<see cref="MacBlocks"/>): reads in the app's own content world, the store's cookies, a snapshot as JPEG, find, a print, the
/// store's clears. Every method the runtime calls catches all and answers WebKit's handlers on every path. Main thread only; excluded from
/// coverage with the AppKit layer, proven by the smoke's <c>browser:webkit</c> and the live runs.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacBrowserKit
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>The app's content world's name: the watch's reads, the status bubble and the full-screen exit run there, never in the page's.</summary>
    public const string WorldName = "NeonSidekick";

    /// <summary>The status bubble's message handler, added for the app's world alone.</summary>
    public const string StatusHandler = "neonStatus";

    /// <summary>The view's properties the window observes.</summary>
    public static readonly string[] Observed = ["URL", "title", "canGoBack", "canGoForward", "loading", "estimatedProgress", "fullscreenState"];

    // WKUserScriptInjectionTimeAtDocumentEnd; NSViewWidthSizable | NSViewHeightSizable; NSKeyValueObservingOptionNew.
    private const long AtDocumentEnd = 1;
    private const ulong ViewSizable = 2 | 16;
    private const nint ObserveNew = 1;

    /// <summary>NSURLSessionAuthChallengeDisposition: use the credential, the system's default handling, cancel.</summary>
    public const long UseCredential = 0;
    public const long DefaultHandling = 1;
    public const long CancelChallenge = 2;

    // The status bubble (pass 14): links hovered reported from the app's world, each change once.
    private const string StatusScript =
        "(() => { let last = ''; const post = h => { if (h !== last) { last = h; window.webkit.messageHandlers." + StatusHandler + ".postMessage(h); } };" +
        " document.addEventListener('mouseover', e => { const a = e.target && e.target.closest ? e.target.closest('a[href]') : null; post(a ? a.href : ''); }, true);" +
        " document.addEventListener('mouseleave', () => post(''), true); window.addEventListener('blur', () => post('')); })();";

    private static readonly Dictionary<nint, IBrowserViewSink> s_sinks = [];
    private static readonly Dictionary<nint, nint> s_downloads = [];   // a download → its window's delegate
    private static nint s_delegateClass;
    private static nint s_world;

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendInitWebView(nint receiver, nint selector, CGRect frame, nint configuration);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendInitScript(nint receiver, nint selector, nint source, long injectionTime, byte mainFrameOnly, nint world);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBoolWith(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendMouse(nint receiver, nint selector, ulong type, CGPoint location, ulong flags, double time, long windowNumber, nint context, long eventNumber, long clickCount, float pressure);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    internal static partial nint SendKey(nint receiver, nint selector, ulong type, CGPoint location, ulong flags, double time, long windowNumber, nint context, nint characters, nint charactersIgnoring, byte repeat, ushort keyCode);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendUseCredential(nint receiver, nint selector, nint user, nint password, ulong persistence);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendJpeg(nint receiver, nint selector, ulong type, nint properties);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendNumberDouble(nint receiver, nint selector, double value);

    /// <summary>The app's content world, made once and kept.</summary>
    public static nint World
    {
        get
        {
            if (s_world == 0)
            {
                MacWebKit.Load();
                s_world = Send(Send(Class("WKContentWorld"), Sel("worldWithName:"), NSString(WorldName)), Sel("retain"));
            }

            return s_world;
        }
    }

    /// <summary>A new delegate for <paramref name="sink"/> (retained: <see cref="Release"/> lets it go).</summary>
    public static nint NewDelegate(IBrowserViewSink sink)
    {
        nint instance = Send(Send(DelegateClass, Sel("alloc")), Sel("init"));
        s_sinks[instance] = sink;
        return instance;
    }

    /// <summary>
    /// The window's web view <paramref name="width"/> × <paramref name="height"/>, growing with its parent: its store the one named
    /// <paramref name="storeId"/> (or, null, one that keeps nothing: the smoke's), <paramref name="userAgent"/> as Safari's, the status script
    /// and its handler in the app's world, the delegates and the observers on. Retained.
    /// </summary>
    public static nint CreateWebView(double width, double height, nint webDelegate, string? storeId, string userAgent)
    {
        MacWebKit.Load();
        nint configuration = Send(Send(Class("WKWebViewConfiguration"), Sel("alloc")), Sel("init"));
        try
        {
            SendVoid(configuration, Sel("setWebsiteDataStore:"), MacWebKit.Store(storeId));
            nint preferences = Send(configuration, Sel("preferences"));
            SendVoidBool(preferences, Sel("setElementFullscreenEnabled:"), 1);   // macOS 12.3
            SendVoidBool(preferences, Sel("setJavaScriptCanOpenWindowsAutomatically:"), 1);   // pass 24: a pop-up after an await, navigated in place

            nint content = Send(configuration, Sel("userContentController"));
            nint script = SendInitScript(Send(Class("WKUserScript"), Sel("alloc")), Sel("initWithSource:injectionTime:forMainFrameOnly:inContentWorld:"), NSString(StatusScript), AtDocumentEnd, 1, World);
            SendVoid(content, Sel("addUserScript:"), script);
            SendVoid(script, Sel("release"));
            SendVoid(content, Sel("addScriptMessageHandler:contentWorld:name:"), webDelegate, World, NSString(StatusHandler));   // the controller retains the delegate

            nint web = SendInitWebView(Send(Class("WKWebView"), Sel("alloc")), Sel("initWithFrame:configuration:"), new CGRect(0, 0, width, height), configuration);
            if (web == 0)
            {
                SendVoid(content, Sel("removeScriptMessageHandlerForName:contentWorld:"), NSString(StatusHandler), World);
                throw new InvalidOperationException(BrowserText.MacFailed);
            }

            SendVoidULong(web, Sel("setAutoresizingMask:"), ViewSizable);
            SendVoid(web, Sel("setNavigationDelegate:"), webDelegate);
            SendVoid(web, Sel("setUIDelegate:"), webDelegate);
            SendVoidBool(web, Sel("setAllowsMagnification:"), 1);
            SendVoidBool(web, Sel("setAllowsBackForwardNavigationGestures:"), 1);
            SendVoid(web, Sel("setCustomUserAgent:"), NSString(userAgent));
            foreach (string key in Observed)
            {
                SendVoid(web, Sel("addObserver:forKeyPath:options:context:"), webDelegate, NSString(key), ObserveNew, 0);
            }

            return web;
        }
        finally
        {
            SendVoid(configuration, Sel("release"));   // the view keeps its own copy
        }
    }

    /// <summary>
    /// The view stopped and let go, with its delegate: the observers off first, full screen and media closed, the loading stopped, the
    /// delegates and the handler taken off, the view out of its window and released. The window answers its held handlers before this.
    /// </summary>
    public static void Release(nint web, nint webDelegate)
    {
        if (web != 0)
        {
            foreach (string key in Observed)
            {
                SendVoid(web, Sel("removeObserver:forKeyPath:"), webDelegate, NSString(key));
            }

            SendVoid(web, Sel("closeAllMediaPresentationsWithCompletionHandler:"), 0);
            SendVoid(web, Sel("pauseAllMediaPlaybackWithCompletionHandler:"), 0);
            SendVoid(web, Sel("stopLoading"));
            SendVoid(web, Sel("setNavigationDelegate:"), 0);
            SendVoid(web, Sel("setUIDelegate:"), 0);
            nint content = Send(Send(web, Sel("configuration")), Sel("userContentController"));
            SendVoid(content, Sel("removeScriptMessageHandlerForName:contentWorld:"), NSString(StatusHandler), World);
            SendVoid(content, Sel("removeAllUserScripts"));
            SendVoid(web, Sel("removeFromSuperview"));
            SendVoid(web, Sel("release"));
        }

        if (webDelegate != 0)
        {
            s_sinks.Remove(webDelegate);
            foreach (var entry in s_downloads.Where(e => e.Value == webDelegate).ToList())
            {
                SendVoid(entry.Key, Sel("setDelegate:"), 0);
                s_downloads.Remove(entry.Key);
            }

            SendVoid(webDelegate, Sel("release"));
        }
    }

    /// <summary>
    /// An http(s) or blank address loaded; false, and nothing loaded, when it is no URL WebKit takes (pass 27: never a <c>loadRequest:</c> with nil).
    /// </summary>
    public static bool Load(nint web, string url)
    {
        nint address = Send(Class("NSURL"), Sel("URLWithString:"), NSString(url));
        if (address == 0)
        {
            return false;
        }

        nint request = Send(Class("NSURLRequest"), Sel("requestWithURL:"), address);
        Send(web, Sel("loadRequest:"), request);
        return true;
    }

    /// <summary>A file loaded with read access to its own folder alone (a staged PDF); false when it is no file.</summary>
    public static bool LoadFile(nint web, string path)
    {
        if (!File.Exists(path) || Path.GetDirectoryName(path) is not { } folder)
        {
            return false;
        }

        nint file = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
        nint access = Send(Class("NSURL"), Sel("fileURLWithPath:isDirectory:"), NSString(folder), 1);
        if (file == 0 || access == 0)
        {
            return false;
        }

        Send(web, Sel("loadFileURL:allowingReadAccessToURL:"), file, access);
        return true;
    }

    /// <summary>A file's URL as WebKit spells it (<c>file:///…</c>), for the filter to admit exactly that one.</summary>
    public static string? FileUrl(string path)
    {
        nint file = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
        return file == 0 ? null : FromNSString(Send(file, Sel("absoluteString")));
    }

    /// <summary>A page of the window's own (the error page) at <paramref name="baseUrl"/>, so the address box keeps it and Reload retries.</summary>
    public static void LoadHtml(nint web, string html, string? baseUrl)
    {
        nint address = baseUrl is null ? 0 : Send(Class("NSURL"), Sel("URLWithString:"), NSString(baseUrl));
        Send(web, Sel("loadHTMLString:baseURL:"), NSString(html), address);
    }

    /// <summary>The view's current address, or null.</summary>
    public static string? Url(nint web) => UrlString(Send(web, Sel("URL")));

    /// <summary>The page's title, or null.</summary>
    public static string? Title(nint web) => FromNSString(Send(web, Sel("title")));

    public static bool CanGoBack(nint web) => SendBool(web, Sel("canGoBack")) != 0;

    public static bool CanGoForward(nint web) => SendBool(web, Sel("canGoForward")) != 0;

    public static bool Loading(nint web) => SendBool(web, Sel("isLoading")) != 0;

    public static double Progress(nint web) => SendDouble(web, Sel("estimatedProgress"));

    /// <summary><c>fullscreenState</c> (macOS 13): 0 not, 1 entering, 2 in, 3 exiting.</summary>
    public static long FullScreenState(nint web) => SendLong(web, Sel("fullscreenState"));

    /// <summary>
    /// <paramref name="expression"/> evaluated in the app's world, its value JSON-stringified (pass 17: an object would come back as an
    /// NSDictionary), and <paramref name="done"/> told the JSON or null (an error, nothing to evaluate in). Main queue.
    /// </summary>
    public static void Evaluate(nint web, string expression, Action<string?> done)
    {
        nint block = MacBlocks.Once((result, error, _) =>
        {
            string? json = error == 0 && result != 0 && SendBoolWith(result, Sel("isKindOfClass:"), Class("NSString")) != 0 ? FromNSString(result) : null;
            done(json);
        });
        SendVoid(web, Sel("evaluateJavaScript:inFrame:inContentWorld:completionHandler:"), NSString("JSON.stringify(" + expression + ")"), 0, World, block);
    }

    /// <summary>A script run in the app's world, its answer not wanted.</summary>
    public static void Run(nint web, string script) =>
        SendVoid(web, Sel("evaluateJavaScript:inFrame:inContentWorld:completionHandler:"), NSString(script), 0, World, 0);

    /// <summary>Every cookie the view's store holds, as <see cref="BrowserCookie"/>s, to <paramref name="done"/> on the main queue.</summary>
    public static void Cookies(nint web, Action<IReadOnlyList<BrowserCookie>> done) =>
        StoreCookies(Send(Send(web, Sel("configuration")), Sel("websiteDataStore")), done);

    /// <summary>Every cookie <paramref name="store"/> (a <c>WKWebsiteDataStore</c>) holds; it answers with no view open.</summary>
    public static void StoreCookies(nint store, Action<IReadOnlyList<BrowserCookie>> done)
    {
        nint block = MacBlocks.Once((array, _, _) =>
        {
            var cookies = new List<BrowserCookie>();
            nuint count = array == 0 ? 0 : (nuint)SendULong(array, Sel("count"));
            for (nuint i = 0; i < count; i++)
            {
                nint c = SendIndex(array, Sel("objectAtIndex:"), i);
                nint expires = Send(c, Sel("expiresDate"));
                if (MacBrowserRules.Cookie(FromNSString(Send(c, Sel("name"))), FromNSString(Send(c, Sel("value"))), FromNSString(Send(c, Sel("domain"))),
                    FromNSString(Send(c, Sel("path"))), expires == 0 ? null : SendDouble(expires, Sel("timeIntervalSince1970")),
                    SendBool(c, Sel("isSessionOnly")) != 0, SendBool(c, Sel("isSecure")) != 0, SendBool(c, Sel("isHTTPOnly")) != 0) is { } cookie)
                {
                    cookies.Add(cookie);
                }
            }

            done(cookies);
        });
        SendVoid(Send(store, Sel("httpCookieStore")), Sel("getAllCookies:"), block);
    }

    /// <summary>Everything <paramref name="store"/> keeps removed (<c>/browser clear all</c>); <paramref name="done"/> on the main queue.</summary>
    public static void RemoveAll(nint store, Action done)
    {
        nint types = Send(Class("WKWebsiteDataStore"), Sel("allWebsiteDataTypes"));
        nint past = Send(Class("NSDate"), Sel("distantPast"));
        nint block = MacBlocks.Once((_, _, _) => done());
        SendVoid(store, Sel("removeDataOfTypes:modifiedSince:completionHandler:"), types, past, block);
    }

    /// <summary>The store <paramref name="storeId"/> names deleted (macOS 14, a deleted profile's); <paramref name="done"/> told WebKit's words on failure.</summary>
    public static void RemoveStore(string storeId, Action<string?> done)
    {
        MacWebKit.Load();
        nint uuid = Send(Send(Class("NSUUID"), Sel("alloc")), Sel("initWithUUIDString:"), NSString(storeId));
        if (uuid == 0)
        {
            done("not a store id: " + storeId);
            return;
        }

        nint block = MacBlocks.Once((error, _, _) => done(error == 0 ? null : FromNSString(Send(error, Sel("localizedDescription")))));
        SendVoid(Class("WKWebsiteDataStore"), Sel("removeDataStoreForIdentifier:completionHandler:"), uuid, block);
        SendVoid(uuid, Sel("release"));
    }

    /// <summary>
    /// What the view shows as a JPEG at quality <paramref name="quality"/>, <paramref name="widthPoints"/> points wide (pixels = points × the
    /// backing scale), to <paramref name="done"/> with its pixel width; null for none.
    /// </summary>
    public static void Snapshot(nint web, double widthPoints, int quality, Action<byte[]?, int> done)
    {
        nint configuration = Send(Send(Class("WKSnapshotConfiguration"), Sel("alloc")), Sel("init"));
        SendVoid(configuration, Sel("setSnapshotWidth:"), SendNumberDouble(Class("NSNumber"), Sel("numberWithDouble:"), widthPoints));
        nint block = MacBlocks.Once((image, error, _) =>
        {
            if (image == 0)
            {
                done(null, 0);
                return;
            }

            nint cg = Send(image, Sel("CGImageForProposedRect:context:hints:"), 0, 0, 0);
            if (cg == 0)
            {
                done(null, 0);
                return;
            }

            nint rep = Send(Send(Class("NSBitmapImageRep"), Sel("alloc")), Sel("initWithCGImage:"), cg);
            try
            {
                nint properties = Send(Class("NSDictionary"), Sel("dictionaryWithObject:forKey:"), SendNumberDouble(Class("NSNumber"), Sel("numberWithDouble:"), quality / 100.0), NSString("NSImageCompressionFactor"));
                nint data = SendJpeg(rep, Sel("representationUsingType:properties:"), 3, properties);   // NSBitmapImageFileTypeJPEG
                done(data == 0 ? null : Bytes(data), (int)CGImageGetWidth(cg));
            }
            finally
            {
                SendVoid(rep, Sel("release"));
            }
        });
        SendVoid(web, Sel("takeSnapshotWithConfiguration:completionHandler:"), configuration, block);
        SendVoid(configuration, Sel("release"));
    }

    /// <summary>An NSData's bytes copied out.</summary>
    public static byte[] Bytes(nint data)
    {
        long length = (long)SendULong(data, Sel("length"));
        var bytes = new byte[length];
        if (length > 0)
        {
            Marshal.Copy(Send(data, Sel("bytes")), bytes, 0, (int)length);
        }

        return bytes;
    }

    /// <summary>The page searched for <paramref name="text"/> (macOS 11), backwards or not, wrapping; <paramref name="done"/> told whether it matched.</summary>
    public static void Find(nint web, string text, bool backwards, Action<bool> done)
    {
        nint configuration = Send(Send(Class("WKFindConfiguration"), Sel("alloc")), Sel("init"));
        SendVoidBool(configuration, Sel("setBackwards:"), (byte)(backwards ? 1 : 0));
        SendVoidBool(configuration, Sel("setWraps:"), 1);
        nint block = MacBlocks.Once((result, _, _) => done(result != 0 && SendBool(result, Sel("matchFound")) != 0));
        SendVoid(web, Sel("findString:withConfiguration:completionHandler:"), NSString(text), configuration, block);
        SendVoid(configuration, Sel("release"));
    }

    /// <summary>
    /// The page printed as a sheet on <paramref name="window"/>: to <paramref name="pdfPath"/> with no panel (a check's PDF; Phase 0: paged,
    /// a 14-page document in under a second), or, with none, through the print panel (⌘P, a page's own button). The sink's
    /// <see cref="IBrowserViewSink.PrintEnded"/> hears <paramref name="token"/>. False when no print operation could be made.
    /// </summary>
    public static bool Print(nint web, nint window, nint webDelegate, string? pdfPath, long token)
    {
        nint info;
        if (pdfPath is not null)
        {
            nint dictionary = Send(Class("NSMutableDictionary"), Sel("dictionary"));
            SendVoid(dictionary, Sel("setObject:forKey:"), NSString("NSPrintSaveJob"), NSString("NSJobDisposition"));
            SendVoid(dictionary, Sel("setObject:forKey:"), Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(pdfPath)), NSString("NSJobSavingURL"));
            info = Send(Send(Class("NSPrintInfo"), Sel("alloc")), Sel("initWithDictionary:"), dictionary);
        }
        else
        {
            info = Send(Send(Send(Class("NSPrintInfo"), Sel("sharedPrintInfo")), Sel("copy")), Sel("self"));
        }

        if (info == 0)
        {
            return false;
        }

        try
        {
            SendVoidLong(info, Sel("setHorizontalPagination:"), 0);   // automatic: the page laid out on sheets
            SendVoidLong(info, Sel("setVerticalPagination:"), 0);
            nint operation = Send(web, Sel("printOperationWithPrintInfo:"), info);
            if (operation == 0)
            {
                return false;
            }

            bool silent = pdfPath is not null;
            SendVoidBool(operation, Sel("setShowsPrintPanel:"), (byte)(silent ? 0 : 1));
            SendVoidBool(operation, Sel("setShowsProgressPanel:"), (byte)(silent ? 0 : 1));
            var paper = SendRect(Send(operation, Sel("view")), Sel("frame"));
            if (paper.Width < 1 || paper.Height < 1)
            {
                SendVoidRect(Send(operation, Sel("view")), Sel("setFrame:"), new CGRect(0, 0, 612, 792));   // Phase 0: a print view needs a frame
            }

            SendVoid(operation, Sel("runOperationModalForWindow:delegate:didRunSelector:contextInfo:"), window, webDelegate, Sel("printOperationDidRun:success:contextInfo:"), (nint)token);
            return true;
        }
        finally
        {
            SendVoid(info, Sel("release"));
        }
    }

    /// <summary>An HTTP sign-in answered: the user's name and password for this session only, or the challenge cancelled (null).</summary>
    public static void AnswerSignIn(HeldHandler done, string? user, string? password)
    {
        if (user is null)
        {
            done.Answer(b => MacBlocks.CallLongObject(b, CancelChallenge, 0));
            return;
        }

        nint credential = SendUseCredential(Class("NSURLCredential"), Sel("credentialWithUser:password:persistence:"), NSString(user), NSString(password ?? ""), 1);   // ForSession
        done.Answer(b => MacBlocks.CallLongObject(b, UseCredential, credential));
    }

    /// <summary>The download asked to stop (its delegate hears a failure, cancelled).</summary>
    public static void CancelDownload(nint download) => SendVoid(download, Sel("cancel:"), 0);

    /// <summary>A download's progress, 0 to 1.</summary>
    public static double DownloadFraction(nint download)
    {
        nint progress = Send(download, Sel("progress"));
        return progress == 0 ? 0 : Math.Clamp(SendDouble(progress, Sel("fractionCompleted")), 0, 1);
    }

    /// <summary>An address opened in the user's default browser, a file in its own program (NSWorkspace: no process of the app's own).</summary>
    public static bool OpenFile(string path)
    {
        nint url = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
        return url != 0 && SendBoolWith(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("openURL:"), url) != 0;
    }

    private static string? UrlString(nint url) => url == 0 ? null : FromNSString(Send(url, Sel("absoluteString")));

    private static nint DelegateClass
    {
        get
        {
            if (s_delegateClass != 0)
            {
                return s_delegateClass;
            }

            MacWebKit.Load();
            nint cls = objc_allocateClassPair(Class("NSObject"), "NeonSidekickBrowserDelegate", 0);
            void Add(string selector, nint implementation, string types)
            {
                if (!class_addMethod(cls, Sel(selector), implementation, types))
                {
                    DiagnosticLog.Warn("Browser", "The browser delegate could not take " + selector + ".");
                }
            }

            Add("webView:decidePolicyForNavigationAction:decisionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DecideAction, "v@:@@@?");
            Add("webView:decidePolicyForNavigationResponse:decisionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DecideResponse, "v@:@@@?");
            Add("webView:didStartProvisionalNavigation:", (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&Started, "v@:@@");
            Add("webView:didFinishNavigation:", (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&Finished, "v@:@@");
            Add("webView:didFailNavigation:withError:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&Failed, "v@:@@@");
            Add("webView:didFailProvisionalNavigation:withError:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&FailedProvisional, "v@:@@@");
            Add("webView:didReceiveAuthenticationChallenge:completionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&Challenge, "v@:@@@?");
            Add("webView:navigationResponse:didBecomeDownload:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&BecameDownload, "v@:@@@");
            Add("webView:navigationAction:didBecomeDownload:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&BecameDownload, "v@:@@@");
            Add("webViewWebContentProcessDidTerminate:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&ContentTerminated, "v@:@");
            Add("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)&CreateWebView, "@@:@@@@");
            Add("webView:runJavaScriptAlertPanelWithMessage:initiatedByFrame:completionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&AlertPanel, "v@:@@@@?");
            Add("webView:runJavaScriptConfirmPanelWithMessage:initiatedByFrame:completionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&ConfirmPanel, "v@:@@@@?");
            Add("webView:runJavaScriptTextInputPanelWithPrompt:defaultText:initiatedByFrame:completionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint, void>)&PromptPanel, "v@:@@@@@?");
            Add("webView:runOpenPanelWithParameters:initiatedByFrame:completionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&OpenPanel, "v@:@@@@?");
            Add("webViewDidClose:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&PageClosed, "v@:@");
            // A page's own print button (pass 26): methods WebKit may call, never ones the app calls. Phase 0 found the newer form called.
            Add("_webView:printFrame:", (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&PrintFrame, "v@:@@");
            Add("_webView:printFrame:pdfFirstPageSize:completionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, CGSize, nint, void>)&PrintFrameSized, "v@:@@{CGSize=dd}@?");
            Add("download:decideDestinationUsingResponse:suggestedFilename:completionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&DownloadDestination, "v@:@@@@?");
            Add("downloadDidFinish:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&DownloadFinished, "v@:@");
            Add("download:didFailWithError:resumeData:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DownloadFailed, "v@:@@@");
            Add("observeValueForKeyPath:ofObject:change:context:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, void>)&Observe, "v@:@@@^v");
            Add("userContentController:didReceiveScriptMessage:", (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&Message, "v@:@@");
            Add("printOperationDidRun:success:contextInfo:", (nint)(delegate* unmanaged<nint, nint, nint, byte, nint, void>)&PrintDidRun, "v@:@B^v");
            foreach (string protocol in (string[])["WKNavigationDelegate", "WKUIDelegate", "WKDownloadDelegate", "WKScriptMessageHandler"])
            {
                nint declared = objc_getProtocol(protocol);
                if (declared != 0)
                {
                    class_addProtocol(cls, declared);
                }
            }

            objc_registerClassPair(cls);
            s_delegateClass = cls;
            return cls;
        }
    }

    private static IBrowserViewSink? SinkOf(nint self) => s_sinks.GetValueOrDefault(self);

    private static string? UrlOfAction(nint action)
    {
        nint request = action == 0 ? 0 : Send(action, Sel("request"));
        return request == 0 ? null : UrlString(Send(request, Sel("URL")));
    }

    private static string? OriginOf(nint frame)
    {
        nint origin = frame == 0 ? 0 : Send(frame, Sel("securityOrigin"));
        if (origin == 0)
        {
            return null;
        }

        string? protocol = FromNSString(Send(origin, Sel("protocol")));
        string? host = FromNSString(Send(origin, Sel("host")));
        long port = SendLong(origin, Sel("port"));
        return string.IsNullOrEmpty(host) ? null : $"{protocol}://{host}{(port > 0 ? ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}";
    }

    [UnmanagedCallersOnly]
    private static void DecideAction(nint self, nint selector, nint webView, nint action, nint decisionHandler)
    {
        var policy = MacNavigationPolicy.Cancel;
        try
        {
            nint target = Send(action, Sel("targetFrame"));
            bool main = target != 0 && SendBool(target, Sel("isMainFrame")) != 0;
            bool download = SendBool(action, Sel("shouldPerformDownload")) != 0;
            // A new window's navigation (no target frame) goes ahead here; createWebView… then navigates the address in place.
            policy = target == 0 ? MacNavigationPolicy.Allow : SinkOf(self)?.DecideAction(UrlOfAction(action), main, download) ?? MacNavigationPolicy.Cancel;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "A navigation's decision failed.", ex);
            policy = MacNavigationPolicy.Cancel;
        }
        finally
        {
            MacBlocks.CallLong(decisionHandler, (long)policy);
        }
    }

    [UnmanagedCallersOnly]
    private static void DecideResponse(nint self, nint selector, nint webView, nint navigationResponse, nint decisionHandler)
    {
        var policy = MacNavigationPolicy.Cancel;
        try
        {
            bool main = SendBool(navigationResponse, Sel("isForMainFrame")) != 0;
            bool canShow = SendBool(navigationResponse, Sel("canShowMIMEType")) != 0;
            nint response = Send(navigationResponse, Sel("response"));
            string? mime = FromNSString(Send(response, Sel("MIMEType")));
            string? url = UrlString(Send(response, Sel("URL")));
            int status = 0;
            bool attachment = false;
            if (SendBoolWith(response, Sel("isKindOfClass:"), Class("NSHTTPURLResponse")) != 0)
            {
                status = (int)SendLong(response, Sel("statusCode"));
                string? disposition = FromNSString(Send(response, Sel("valueForHTTPHeaderField:"), NSString("Content-Disposition")));
                attachment = disposition?.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase) ?? false;
            }

            policy = SinkOf(self)?.DecideResponse(url, main, status, mime, canShow, attachment) ?? MacNavigationPolicy.Cancel;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "A response's decision failed.", ex);
            policy = MacNavigationPolicy.Cancel;
        }
        finally
        {
            MacBlocks.CallLong(decisionHandler, (long)policy);
        }
    }

    private static void Sink(nint self, Action<IBrowserViewSink> act, string what)
    {
        try
        {
            if (SinkOf(self) is { } sink)
            {
                act(sink);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", $"The Browser window's {what} failed.", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void Started(nint self, nint selector, nint webView, nint navigation) => Sink(self, static s => s.Started(), "navigation start");

    [UnmanagedCallersOnly]
    private static void Finished(nint self, nint selector, nint webView, nint navigation) => Sink(self, static s => s.Finished(), "navigation end");

    [UnmanagedCallersOnly]
    private static void Failed(nint self, nint selector, nint webView, nint navigation, nint error) => Fail(self, provisional: false, error);

    [UnmanagedCallersOnly]
    private static void FailedProvisional(nint self, nint selector, nint webView, nint navigation, nint error) => Fail(self, provisional: true, error);

    private static void Fail(nint self, bool provisional, nint error) => Sink(self, s =>
    {
        string? domain = FromNSString(Send(error, Sel("domain")));
        long code = SendLong(error, Sel("code"));
        string message = FromNSString(Send(error, Sel("localizedDescription"))) ?? "";
        nint info = Send(error, Sel("userInfo"));
        string? failing = info == 0 ? null : FromNSString(Send(info, Sel("objectForKey:"), NSString("NSErrorFailingURLStringKey")));
        s.Failed(provisional, domain, code, message, failing);
    }, "failed load");

    // HTTP Basic and Digest are the window's sheet; Negotiate, NTLM, client certificates and server trust the system's default
    // handling (the user's Kerberos ticket, the system's trust; a refused certificate is never offered a way past).
    [UnmanagedCallersOnly]
    private static void Challenge(nint self, nint selector, nint webView, nint challenge, nint completionHandler)
    {
        bool held = false;
        try
        {
            nint space = Send(challenge, Sel("protectionSpace"));
            string? method = FromNSString(Send(space, Sel("authenticationMethod")));
            if (MacBrowserRules.AsksForSignIn(method) && SinkOf(self) is { } sink)
            {
                string host = FromNSString(Send(space, Sel("host"))) ?? "";
                string? realm = FromNSString(Send(space, Sel("realm")));
                long failures = SendLong(challenge, Sel("previousFailureCount"));
                var done = new HeldHandler(completionHandler, static b => MacBlocks.CallLongObject(b, CancelChallenge, 0));
                held = true;
                sink.SignIn(host, realm, failures, done);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "A sign-in challenge failed.", ex);
        }
        finally
        {
            if (!held)
            {
                MacBlocks.CallLongObject(completionHandler, DefaultHandling, 0);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static void BecameDownload(nint self, nint selector, nint webView, nint origin, nint download)
    {
        try
        {
            SendVoid(download, Sel("setDelegate:"), self);
            s_downloads[download] = self;
            SinkOf(self)?.DownloadBegan(download);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "A download's start failed.", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void DownloadDestination(nint self, nint selector, nint download, nint response, nint suggested, nint completionHandler)
    {
        nint url = 0;
        try
        {
            if (SinkOf(self)?.DownloadDestination(download, FromNSString(suggested)) is { } path)
            {
                url = Send(Class("NSURL"), Sel("fileURLWithPath:"), NSString(path));
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "A download's destination failed.", ex);
            url = 0;
        }
        finally
        {
            MacBlocks.Call(completionHandler, url);   // nil cancels it
        }
    }

    [UnmanagedCallersOnly]
    private static void DownloadFinished(nint self, nint selector, nint download) => EndDownload(self, download, ok: true, null);

    [UnmanagedCallersOnly]
    private static void DownloadFailed(nint self, nint selector, nint download, nint error, nint resumeData) =>
        EndDownload(self, download, ok: false, error == 0 ? null : FromNSString(Send(error, Sel("localizedDescription"))));

    private static void EndDownload(nint self, nint download, bool ok, string? error)
    {
        s_downloads.Remove(download);
        Sink(self, s => s.DownloadEnded(download, ok, error), "download's end");
    }

    [UnmanagedCallersOnly]
    private static void ContentTerminated(nint self, nint selector, nint webView) => Sink(self, static s => s.ContentEnded(), "content end");

    // A new window the page asked for: never made (nil); the window navigates the address in place.
    [UnmanagedCallersOnly]
    private static nint CreateWebView(nint self, nint selector, nint webView, nint configuration, nint action, nint features)
    {
        try
        {
            SinkOf(self)?.NewWindow(UrlOfAction(action));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "A page's new window failed.", ex);
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static void AlertPanel(nint self, nint selector, nint webView, nint message, nint frame, nint completionHandler) =>
        Panel(self, completionHandler, static b => MacBlocks.Call(b), (s, done) => s.Alert(FromNSString(message) ?? "", OriginOf(frame), done));

    [UnmanagedCallersOnly]
    private static void ConfirmPanel(nint self, nint selector, nint webView, nint message, nint frame, nint completionHandler) =>
        Panel(self, completionHandler, static b => MacBlocks.CallBool(b, false), (s, done) => s.Confirm(FromNSString(message) ?? "", OriginOf(frame), done));

    [UnmanagedCallersOnly]
    private static void PromptPanel(nint self, nint selector, nint webView, nint prompt, nint defaultText, nint frame, nint completionHandler) =>
        Panel(self, completionHandler, static b => MacBlocks.Call(b, 0), (s, done) => s.Prompt(FromNSString(prompt) ?? "", FromNSString(defaultText), OriginOf(frame), done));

    [UnmanagedCallersOnly]
    private static void OpenPanel(nint self, nint selector, nint webView, nint parameters, nint frame, nint completionHandler) =>
        Panel(self, completionHandler, static b => MacBlocks.Call(b, 0), (s, done) => s.OpenPanel(
            SendBool(parameters, Sel("allowsMultipleSelection")) != 0, SendBool(parameters, Sel("allowsDirectories")) != 0, done));

    // A page's panel: the handler held for the sink's sheet, answered with its default if the sink is gone or fails before it holds it.
    private static void Panel(nint self, nint completionHandler, Action<nint> fallback, Action<IBrowserViewSink, HeldHandler> show)
    {
        var done = new HeldHandler(completionHandler, fallback);
        try
        {
            if (SinkOf(self) is { } sink)
            {
                show(sink, done);
                return;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "A page's dialog failed.", ex);
        }

        done.Fallback();
    }

    [UnmanagedCallersOnly]
    private static void PageClosed(nint self, nint selector, nint webView) =>
        DiagnosticLog.Debug("Browser", "A page called window.close(); the Browser window stays (WebView2 leaves it too).");

    [UnmanagedCallersOnly]
    private static void PrintFrame(nint self, nint selector, nint webView, nint frame) => Sink(self, static s => s.PrintRequested(null), "page's print");

    [UnmanagedCallersOnly]
    private static void PrintFrameSized(nint self, nint selector, nint webView, nint frame, CGSize firstPage, nint completionHandler) =>
        Panel(self, completionHandler, static b => MacBlocks.Call(b), static (s, done) => s.PrintRequested(done));

    [UnmanagedCallersOnly]
    private static void Observe(nint self, nint selector, nint keyPath, nint target, nint change, nint context)
    {
        try
        {
            if (FromNSString(keyPath) is { } key && SinkOf(self) is { } sink)
            {
                sink.Changed(key);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "A web view property's change failed.", ex);
        }
    }

    // The status bubble: the app's world alone has the handler, so the page cannot post to it; a string only.
    [UnmanagedCallersOnly]
    private static void Message(nint self, nint selector, nint controller, nint message)
    {
        try
        {
            nint body = Send(message, Sel("body"));
            if (body != 0 && SendBoolWith(body, Sel("isKindOfClass:"), Class("NSString")) != 0 && SinkOf(self) is { } sink)
            {
                sink.Hovered(FromNSString(body) ?? "");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Browser", "The status bubble's message failed.", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void PrintDidRun(nint self, nint selector, nint operation, byte success, nint context) =>
        Sink(self, s => s.PrintEnded(context, success != 0), "print's end");
}

/// <summary>A size in AppKit's coordinates (<c>CGSize</c>: two doubles, an HFA in d0–d1).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGSize
{
    public double Width;
    public double Height;
}
