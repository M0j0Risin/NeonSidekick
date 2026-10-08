using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>What a web view's delegate hears (<see cref="MacWebKit"/>): the page's messages, its navigations and its new windows. Main thread.</summary>
internal interface IWebPageSink
{
    /// <summary>A script message, already checked to be the page's own (<see cref="WebKitPage.IsOurFrame"/>).</summary>
    void PageMessage(string text);

    /// <summary>Whether a navigation goes ahead: <paramref name="mainFrame"/> is the page's own frame, false for a frame inside it.</summary>
    bool AllowNavigation(string? url, bool mainFrame);

    /// <summary>A new window the page asked for (a link with a target, <c>window.open</c>): never made; the address handed here.</summary>
    void NewWindow(string? url);

    /// <summary>The page's web content process ended (a crash, or macOS took it back): the view is blank from here.</summary>
    void ContentEnded();
}

/// <summary>
/// WebKit for the video window on a Mac (2026-10-07, Stage 2: YouTube playback on macOS), the native part: the framework loaded,
/// <c>NeonSidekickWebDelegate</c> (an NSObject made at runtime, the windows round's shape, declaring <c>WKScriptMessageHandler</c>,
/// <c>WKNavigationDelegate</c> and <c>WKUIDelegate</c>), and a <c>WKWebView</c> made over it with the window's settings: no user action
/// needed to play media (Windows' <c>--autoplay-policy=no-user-gesture-required</c>), the page's <c>chrome.webview</c> bridge
/// (<see cref="WebKitPage.Bridge"/>) at document start in the main frame, the message handler, and a data store — the home's
/// identified one (<c>dataStoreForIdentifier:</c>, macOS 14) or, for the smoke, a non-persistent one, so a probe never writes into
/// the user's. The page is loaded with <c>loadHTMLString:baseURL:</c> and <see cref="VideoPage.Url"/> as its base (the origin,
/// <see cref="WebKitPage"/>). The navigation decision is a block WebKit hands in: it is called, never built, through its invoke
/// pointer. Element full screen is on (2026-10-07, the user's pick after the live run: off, YouTube still drew its full-screen button
/// and a click went nowhere): the button takes the video into WebKit's own full-screen window, a Space as in Safari, and Esc brings it
/// back; ⌃⌘F stays the window's own full screen in place. Main thread only; excluded from coverage with the AppKit layer, proven by the smoke's <c>video:webkit</c> and the
/// live run.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacWebKit
{
    public const string WebKitPath = "/System/Library/Frameworks/WebKit.framework/WebKit";

    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    // WKUserScriptInjectionTimeAtDocumentStart; WKNavigationActionPolicyCancel / Allow; WKAudiovisualMediaTypeNone.
    private const long AtDocumentStart = 0;
    private const long PolicyCancel = 0;
    private const long PolicyAllow = 1;
    private const ulong MediaTypeNone = 0;

    // NSViewWidthSizable | NSViewHeightSizable.
    private const ulong ViewSizable = 2 | 16;

    private static readonly Dictionary<nint, IWebPageSink> s_sinks = [];
    private static nint s_delegateClass;
    private static bool s_loaded;

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendInitScript(nint receiver, nint selector, nint source, long injectionTime, byte mainFrameOnly);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendInitWebView(nint receiver, nint selector, CGRect frame, nint configuration);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBoolWith(nint receiver, nint selector, nint argument);

    /// <summary>WebKit loaded once; its classes are found by name from then on.</summary>
    public static void Load()
    {
        if (!s_loaded)
        {
            NativeLibrary.Load(WebKitPath);
            s_loaded = true;
        }
    }

    /// <summary>A new delegate for <paramref name="sink"/> (retained: <see cref="Release"/> lets it go).</summary>
    public static nint NewDelegate(IWebPageSink sink)
    {
        nint instance = Send(Send(DelegateClass, Sel("alloc")), Sel("init"));
        s_sinks[instance] = sink;
        return instance;
    }

    /// <summary>
    /// A web view <paramref name="width"/> × <paramref name="height"/> that grows with its parent, its delegate <paramref name="webDelegate"/>,
    /// its store the one named <paramref name="storeId"/> (<see cref="WebKitPage.StoreId"/>) or, when null, a non-persistent one. Retained.
    /// </summary>
    public static nint CreateWebView(double width, double height, nint webDelegate, string? storeId)
    {
        Load();
        nint configuration = Send(Send(Class("WKWebViewConfiguration"), Sel("alloc")), Sel("init"));
        try
        {
            SendVoidULong(configuration, Sel("setMediaTypesRequiringUserActionForPlayback:"), MediaTypeNone);
            SendVoid(configuration, Sel("setWebsiteDataStore:"), Store(storeId));
            SendVoidBool(Send(configuration, Sel("preferences")), Sel("setElementFullscreenEnabled:"), 1);   // macOS 12.3

            nint content = Send(configuration, Sel("userContentController"));
            nint script = SendInitScript(Send(Class("WKUserScript"), Sel("alloc")), Sel("initWithSource:injectionTime:forMainFrameOnly:"), NSString(WebKitPage.Bridge), AtDocumentStart, 1);
            SendVoid(content, Sel("addUserScript:"), script);
            SendVoid(script, Sel("release"));
            SendVoid(content, Sel("addScriptMessageHandler:name:"), webDelegate, NSString(WebKitPage.HandlerName));   // the controller retains the delegate

            nint web = SendInitWebView(Send(Class("WKWebView"), Sel("alloc")), Sel("initWithFrame:configuration:"), new CGRect(0, 0, width, height), configuration);
            if (web == 0)
            {
                SendVoid(content, Sel("removeScriptMessageHandlerForName:"), NSString(WebKitPage.HandlerName));
                throw new InvalidOperationException("WKWebView could not be made");
            }

            SendVoidULong(web, Sel("setAutoresizingMask:"), ViewSizable);
            SendVoid(web, Sel("setNavigationDelegate:"), webDelegate);
            SendVoid(web, Sel("setUIDelegate:"), webDelegate);
            return web;
        }
        finally
        {
            SendVoid(configuration, Sel("release"));   // the view keeps its own copy
        }
    }

    /// <summary><paramref name="html"/> loaded with the page's https base, so it has that origin and its embeds a Referer.</summary>
    public static void LoadPage(nint web, string html)
    {
        nint baseUrl = Send(Class("NSURL"), Sel("URLWithString:"), NSString(VideoPage.Url));
        Send(web, Sel("loadHTMLString:baseURL:"), NSString(html), baseUrl);
    }

    /// <summary>A script run in the page, its answer not wanted (no completion block).</summary>
    public static void Run(nint web, string script) => SendVoid(web, Sel("evaluateJavaScript:completionHandler:"), NSString(script), 0);

    /// <summary>
    /// The view stopped and let go, with its delegate: full screen and picture in picture left (measured: closed while in WebKit's
    /// full-screen window, the Space ends and that window hides, with nothing left behind), the media paused, the loading stopped, the delegates and the message handler
    /// taken off (the controller holds the delegate strongly), the view out of its window and released. After this no sound is left.
    /// </summary>
    public static void Release(nint web, nint webDelegate)
    {
        if (web != 0)
        {
            SendVoid(web, Sel("closeAllMediaPresentationsWithCompletionHandler:"), 0);   // macOS 12
            SendVoid(web, Sel("pauseAllMediaPlaybackWithCompletionHandler:"), 0);
            SendVoid(web, Sel("stopLoading"));
            SendVoid(web, Sel("setNavigationDelegate:"), 0);
            SendVoid(web, Sel("setUIDelegate:"), 0);
            nint content = Send(Send(web, Sel("configuration")), Sel("userContentController"));
            SendVoid(content, Sel("removeScriptMessageHandlerForName:"), NSString(WebKitPage.HandlerName));
            SendVoid(content, Sel("removeAllUserScripts"));
            SendVoid(web, Sel("removeFromSuperview"));
            SendVoid(web, Sel("release"));
        }

        if (webDelegate != 0)
        {
            s_sinks.Remove(webDelegate);
            SendVoid(webDelegate, Sel("release"));
        }
    }

    /// <summary>An http or https address opened in the user's default browser (NSWorkspace; no process of the app's own). True when macOS took it.</summary>
    public static bool OpenInBrowser(string url)
    {
        nint pool = objc_autoreleasePoolPush();
        try
        {
            NativeLibrary.Load(AppKitPath);
            nint address = Send(Class("NSURL"), Sel("URLWithString:"), NSString(url));
            return address != 0 && SendBoolWith(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("openURL:"), address) != 0;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    // The home's own store, or a store that keeps nothing (the smoke's).
    private static nint Store(string? storeId)
    {
        nint store = Class("WKWebsiteDataStore");
        if (storeId is null)
        {
            return Send(store, Sel("nonPersistentDataStore"));
        }

        nint uuid = Send(Send(Class("NSUUID"), Sel("alloc")), Sel("initWithUUIDString:"), NSString(storeId));
        if (uuid == 0)
        {
            throw new InvalidOperationException("not a UUID: " + storeId);
        }

        nint identified = Send(store, Sel("dataStoreForIdentifier:"), uuid);
        SendVoid(uuid, Sel("release"));
        return identified;
    }

    private static nint DelegateClass
    {
        get
        {
            if (s_delegateClass != 0)
            {
                return s_delegateClass;
            }

            Load();
            nint cls = objc_allocateClassPair(Class("NSObject"), "NeonSidekickWebDelegate", 0);
            class_addMethod(cls, Sel("userContentController:didReceiveScriptMessage:"), (nint)(delegate* unmanaged<nint, nint, nint, nint, void>)&DidReceive, "v@:@@");
            class_addMethod(cls, Sel("webView:decidePolicyForNavigationAction:decisionHandler:"), (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DecidePolicy, "v@:@@@?");
            class_addMethod(cls, Sel("webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:"), (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, nint, nint>)&CreateWebView, "@@:@@@@");
            class_addMethod(cls, Sel("webViewWebContentProcessDidTerminate:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&ContentTerminated, "v@:@");
            foreach (string protocol in (string[])["WKScriptMessageHandler", "WKNavigationDelegate", "WKUIDelegate"])
            {
                nint declared = objc_getProtocol(protocol);
                if (declared != 0)
                {
                    class_addProtocol(cls, declared);   // declared, as WebKit's setters expect of their delegates
                }
            }

            objc_registerClassPair(cls);
            s_delegateClass = cls;
            return cls;
        }
    }

    // A script message: the page's own frame only, a string only (the page posts JSON text).
    [UnmanagedCallersOnly]
    private static void DidReceive(nint self, nint selector, nint controller, nint message)
    {
        try
        {
            if (!s_sinks.TryGetValue(self, out var sink))
            {
                return;
            }

            nint frame = Send(message, Sel("frameInfo"));
            nint origin = frame == 0 ? 0 : Send(frame, Sel("securityOrigin"));
            bool main = frame != 0 && SendBool(frame, Sel("isMainFrame")) != 0;
            string? protocol = origin == 0 ? null : FromNSString(Send(origin, Sel("protocol")));
            string? host = origin == 0 ? null : FromNSString(Send(origin, Sel("host")));
            if (!WebKitPage.IsOurFrame(main, protocol, host))
            {
                DiagnosticLog.Debug("Video", $"A script message from {protocol}://{host} (main frame: {main}) was dropped: not the page's own.");
                return;
            }

            nint body = Send(message, Sel("body"));
            if (body == 0 || SendBoolWith(body, Sel("isKindOfClass:"), Class("NSString")) == 0)
            {
                return;
            }

            sink.PageMessage(FromNSString(body) ?? string.Empty);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "A video page message failed.", ex);
        }
    }

    // A navigation: the sink decides, and WebKit's block is called with the answer — always, an exception included (a decision
    // never made leaves WebKit waiting and logs a warning).
    [UnmanagedCallersOnly]
    private static void DecidePolicy(nint self, nint selector, nint webView, nint action, nint decisionHandler)
    {
        long policy = PolicyCancel;
        try
        {
            nint target = Send(action, Sel("targetFrame"));
            bool main = target != 0 && SendBool(target, Sel("isMainFrame")) != 0;
            string? url = UrlOf(action);
            policy = target == 0 || (s_sinks.TryGetValue(self, out var sink) && sink.AllowNavigation(url, main)) ? PolicyAllow : PolicyCancel;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "A video page navigation's decision failed.", ex);
        }
        finally
        {
            CallDecision(decisionHandler, policy);
        }
    }

    // A new window the page asked for: never made (nil); the sink gets the address.
    [UnmanagedCallersOnly]
    private static nint CreateWebView(nint self, nint selector, nint webView, nint configuration, nint action, nint features)
    {
        try
        {
            if (s_sinks.TryGetValue(self, out var sink))
            {
                sink.NewWindow(UrlOf(action));
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "A video page's new window failed.", ex);
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    private static void ContentTerminated(nint self, nint selector, nint webView)
    {
        try
        {
            if (s_sinks.TryGetValue(self, out var sink))
            {
                sink.ContentEnded();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Video", "The video page's end failed.", ex);
        }
    }

    private static string? UrlOf(nint action)
    {
        nint request = action == 0 ? 0 : Send(action, Sel("request"));
        nint url = request == 0 ? 0 : Send(request, Sel("URL"));
        return url == 0 ? null : FromNSString(Send(url, Sel("absoluteString")));
    }

    // A block's layout: isa, flags and reserved (two ints), then its function; the function takes the block first.
    private static void CallDecision(nint block, long policy)
    {
        if (block == 0)
        {
            return;
        }

        var invoke = (delegate* unmanaged<nint, long, void>)*(nint*)(block + 16);
        invoke(block, policy);
    }
}
