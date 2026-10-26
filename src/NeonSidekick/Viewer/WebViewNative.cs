using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static NeonSidekick.Viewer.ViewerNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The native calls behind the video window (2026-10-05, the YouTube plan): Microsoft Edge WebView2 through its loader,
/// <see cref="LoaderFileName"/>, the one file of the <c>Microsoft.Web.WebView2</c> package the build keeps (the csproj copies it
/// beside the exe; it joins <c>SmokeChecks.RequiredNativeLibraries</c>, since unlike the system libraries the other layers use a
/// missing copy is invisible to <c>dotnet build</c>). Microsoft's .NET wrapper was not taken: it is built on the runtime's
/// built-in COM interop, RCWs, which NativeAOT does not support. So every interface is called through its vtable with
/// unmanaged function pointers (the <c>CameraNative</c> and <c>PerfNative</c> shape), and the handlers WebView2 calls back
/// are objects of ours (<see cref="WebViewHandler"/>, the codebase's first app-made COM objects). WebView2Aot (a generated
/// AOT binding, MIT) was the fallback and was not needed: the Phase 0 spike ran these slots unchanged under JIT and NativeAOT.
///
/// <para>Slots from <c>WebView2.h</c>'s declaration order (SDK 1.0.4258.31; a published COM interface never changes, and a
/// newer member is a new interface, <c>ICoreWebView2_3</c> here): IUnknown 0–2; ICoreWebView2Environment 3
/// CreateCoreWebView2Controller; ICoreWebView2Controller 4 put_IsVisible, 6 put_Bounds, 19 add_AcceleratorKeyPressed,
/// 23 NotifyParentWindowPositionChanged, 24 Close, 25 get_CoreWebView2; ICoreWebView2 3 get_Settings, 5 Navigate,
/// 7 add_NavigationStarting, 29 ExecuteScript, 32 PostWebMessageAsJson, 34 add_WebMessageReceived, 44 add_NewWindowRequested,
/// 52 add_ContainsFullScreenElementChanged, 54 get_ContainsFullScreenElement; the accelerator key's arguments 3
/// get_KeyEventKind, 4 get_VirtualKey, 8 put_Handled; ICoreWebView2_3 71
/// SetVirtualHostNameToFolderMapping; ICoreWebView2Environment5 12 add_BrowserProcessExited; ICoreWebView2Settings 8 put_AreDefaultScriptDialogsEnabled, 10 put_IsStatusBarEnabled,
/// 12 put_AreDevToolsEnabled, 14 put_AreDefaultContextMenusEnabled, 16 put_AreHostObjectsAllowed, 18 put_IsZoomControlEnabled;
/// the navigation-starting arguments 3 get_Uri, 8 put_Cancel; the web message's 5 TryGetWebMessageAsString; the new window's
/// 3 get_Uri, 6 put_Handled.</para>
///
/// <para>The Browser window's (2026-10-13, the bot checks, checked against the same header): ICoreWebView2 4 get_Source,
/// 11 add_SourceChanged, 15 add_NavigationCompleted, 31 Reload, 36 CallDevToolsProtocolMethod, 40 GoBack, 46 add_DocumentTitleChanged,
/// 48 get_DocumentTitle; ICoreWebView2_4 75 add_DownloadStarting; ICoreWebView2_7 80 PrintToPdf; the navigation-completed arguments
/// 3 get_IsSuccess, and their second interface's 6 get_HttpStatusCode; the download-starting arguments 5 put_Cancel, 9 put_Handled.
/// Its navigation bar's (later the same day, the same header): ICoreWebView2 13 add_HistoryChanged, 38 get_CanGoBack,
/// 39 get_CanGoForward, 41 GoForward, 43 Stop; ICoreWebView2Controller 12 MoveFocus. Its privacy (later still): ICoreWebView2_13
/// 105 get_Profile; ICoreWebView2Profile2 10 ClearBrowsingData, 12 ClearBrowsingDataAll. Its bot check's navigations (2026-10-26, the
/// same header, 1.0.4258.31): the navigation-starting arguments 9 get_NavigationId, the navigation-completed arguments 5 get_NavigationId.</para>
///
/// <para>The <c>add_*</c> calls' registration tokens are not kept: <see cref="ControllerClose"/> drops every handler with the
/// view, and nothing is ever unsubscribed earlier. Every string WebView2 hands out is <c>CoTaskMemAlloc</c>'d and freed here
/// (<see cref="TakeString"/>). The environment gets an options object of ours (<see cref="WebViewOptions"/>) for the autoplay
/// flag: the spike had played with sound without it, the real window's first run did not.</para>
///
/// <para>Child processes: the runtime starts its own <c>msedgewebview2.exe</c> processes; none is started by this code, so
/// no process-start site is added (as the Claude CLI starting <c>--mcp-relay</c>). They exit when the view closes or this
/// process dies.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WebViewNative
{
    public const string LoaderFileName = VideoPage.LoaderFileName;

    // ── HRESULTs ─────────────────────────────────────────────────────────────

    internal const int SOk = 0;

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)</c>: what the loader answers when no WebView2 Runtime is installed.</summary>
    internal const int ENoRuntime = unchecked((int)0x80070002);

    /// <summary><c>HRESULT_FROM_WIN32(ERROR_INVALID_STATE)</c>: the user data folder is in use by a browser with other options.</summary>
    internal const int EInvalidState = unchecked((int)0x8007139F);

    // ── IIDs (WebView2.h) ────────────────────────────────────────────────────

    internal static readonly Guid IidWebView2_3 = new("A0D6DF20-3B92-416D-AA0C-437A9C727857");
    internal static readonly Guid IidEnvironmentCompleted = new("4E8A3389-C9D8-4BD2-B6B5-124FEE6CC14D");
    internal static readonly Guid IidControllerCompleted = new("6C4819F3-C9B7-4260-8127-C9F5BDE7F68C");
    internal static readonly Guid IidNavigationStarting = new("9ADBE429-F36D-432B-9DDC-F8881FBD76E3");
    internal static readonly Guid IidWebMessageReceived = new("57213F19-00E6-49FA-8E07-898EA01ECBD2");
    internal static readonly Guid IidNewWindowRequested = new("D4C185FE-C81C-4989-97AF-2D3FA7AB5651");
    internal static readonly Guid IidExecuteScriptCompleted = new("49511172-CC67-4BCA-9923-137112F4C4CC");
    internal static readonly Guid IidAcceleratorKeyPressed = new("B29C7E28-FA79-41A8-8E44-65811C76DCB2");
    internal static readonly Guid IidContainsFullScreenElementChanged = new("E45D98B1-AFEF-45BE-8BAF-6C7728867F73");
    internal static readonly Guid IidEnvironment5 = new("319E423D-E0D7-4B8D-9254-AE9475DE9B17");
    internal static readonly Guid IidBrowserProcessExited = new("FA504257-A216-4911-A860-FE8825712861");
    internal static readonly Guid IidNavigationCompleted = new("D33A35BF-1C49-4F98-93AB-006E0533FE1C");
    internal static readonly Guid IidNavigationCompletedArgs2 = new("FDF8B738-EE1E-4DB2-A329-8D7D7B74D792");
    internal static readonly Guid IidCallDevToolsProtocolMethodCompleted = new("5C4889F0-5EF6-4C5A-952C-D8F1B92D0574");
    internal static readonly Guid IidDocumentTitleChanged = new("F5F2B923-953E-4042-9F95-F3A118E1AFD4");
    internal static readonly Guid IidSourceChanged = new("3C067F9F-5388-4772-8B48-79F7EF1AB37C");
    internal static readonly Guid IidWebView2_4 = new("20D02D59-6DF2-42DC-BD06-F98A694B1302");
    internal static readonly Guid IidDownloadStarting = new("EFEDC989-C396-41CA-83F7-07F845A55724");
    internal static readonly Guid IidWebView2_7 = new("79C24D83-09A3-45AE-9418-487F32A58740");
    internal static readonly Guid IidPrintToPdfCompleted = new("CCF1EF04-FD8E-4D5F-B2DE-0983E41B8C36");
    internal static readonly Guid IidHistoryChanged = new("C79A420C-EFD9-4058-9295-3E8B4BCAB645");
    internal static readonly Guid IidWebView2_13 = new("F75F09A8-667E-4983-88D6-C8773F315E84");
    internal static readonly Guid IidProfile2 = new("FA740D4B-5EAE-4344-A8AD-74BE31925397");
    internal static readonly Guid IidClearBrowsingDataCompleted = new("E9710A06-1D1D-49B2-8234-226F35846AE5");

    /// <summary><c>COREWEBVIEW2_BROWSING_DATA_KINDS</c>: the browsing history and the download history, the Browser window's "history".</summary>
    internal const int BrowsingDataHistory = 0x1000 | 0x200;

    /// <summary><c>COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY</c>: the virtual host's files are the top-level page's alone.</summary>
    internal const int HostAccessDeny = 0;

    // ── Slots ────────────────────────────────────────────────────────────────

    private const int QueryInterfaceSlot = 0;
    private const int AddRefSlot = 1;
    private const int ReleaseSlot = 2;
    private const int CreateControllerSlot = 3;
    private const int PutIsVisibleSlot = 4;
    private const int PutBoundsSlot = 6;
    private const int NotifyParentWindowPositionChangedSlot = 23;
    private const int ControllerCloseSlot = 24;
    private const int MoveFocusSlot = 12;
    private const int GetCoreWebView2Slot = 25;
    private const int GetSettingsSlot = 3;
    private const int NavigateSlot = 5;
    private const int AddNavigationStartingSlot = 7;
    private const int ExecuteScriptSlot = 29;
    private const int PostWebMessageAsJsonSlot = 32;
    private const int AddWebMessageReceivedSlot = 34;
    private const int AddNewWindowRequestedSlot = 44;
    private const int SetVirtualHostNameToFolderMappingSlot = 71;
    private const int AddBrowserProcessExitedSlot = 12;
    private const int AddAcceleratorKeyPressedSlot = 19;
    private const int AddContainsFullScreenElementChangedSlot = 52;
    private const int GetContainsFullScreenElementSlot = 54;
    private const int KeyGetKeyEventKindSlot = 3;
    private const int KeyGetVirtualKeySlot = 4;
    private const int KeyPutHandledSlot = 8;

    /// <summary><c>COREWEBVIEW2_KEY_EVENT_KIND</c>: a key going down, plain (0) or with Alt, a system key (2).</summary>
    internal const int KeyDown = 0;
    internal const int SystemKeyDown = 2;
    private const int PutAreDefaultScriptDialogsEnabledSlot = 8;
    private const int PutIsStatusBarEnabledSlot = 10;
    private const int PutAreDevToolsEnabledSlot = 12;
    private const int PutAreDefaultContextMenusEnabledSlot = 14;
    private const int PutAreHostObjectsAllowedSlot = 16;
    private const int PutIsZoomControlEnabledSlot = 18;
    private const int NavigationGetUriSlot = 3;
    private const int NavigationPutCancelSlot = 8;
    private const int TryGetWebMessageAsStringSlot = 5;
    private const int NewWindowGetUriSlot = 3;
    private const int NewWindowPutHandledSlot = 6;
    private const int GetSourceSlot = 4;
    private const int AddSourceChangedSlot = 11;
    private const int AddNavigationCompletedSlot = 15;
    private const int ReloadSlot = 31;
    private const int CallDevToolsProtocolMethodSlot = 36;
    private const int GoBackSlot = 40;
    private const int AddHistoryChangedSlot = 13;
    private const int GetCanGoBackSlot = 38;
    private const int GetCanGoForwardSlot = 39;
    private const int GoForwardSlot = 41;
    private const int StopSlot = 43;
    private const int GetProfileSlot = 105;
    private const int ClearBrowsingDataSlot = 10;
    private const int ClearBrowsingDataAllSlot = 12;
    private const int AddDocumentTitleChangedSlot = 46;
    private const int GetDocumentTitleSlot = 48;
    private const int AddDownloadStartingSlot = 75;
    private const int PrintToPdfSlot = 80;
    private const int NavigationCompletedGetIsSuccessSlot = 3;
    private const int NavigationCompletedGetHttpStatusCodeSlot = 6;
    private const int NavigationStartingGetNavigationIdSlot = 9;
    private const int NavigationCompletedGetNavigationIdSlot = 5;
    private const int DownloadPutCancelSlot = 5;
    private const int DownloadPutHandledSlot = 9;

    // ── Imports ──────────────────────────────────────────────────────────────

    [LibraryImport(LoaderFileName, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CreateCoreWebView2EnvironmentWithOptions(string? browserExecutableFolder, string? userDataFolder, nint environmentOptions, nint environmentCreatedHandler);

    [LibraryImport(LoaderFileName)]
    private static partial int GetAvailableCoreWebView2BrowserVersionString(char* browserExecutableFolder, char** versionInfo);

    /// <summary>The installed runtime's version, or null with the HRESULT (<see cref="ENoRuntime"/> when there is none).</summary>
    internal static string? RuntimeVersion(out int hr)
    {
        char* version = null;
        hr = GetAvailableCoreWebView2BrowserVersionString(null, &version);
        string? text = TakeString(version);
        return hr >= 0 ? text : null;
    }

    /// <summary>A string WebView2 handed out, freed; null for a null pointer.</summary>
    internal static string? TakeString(char* text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return new string(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)text);
        }
    }

    // ── IUnknown ─────────────────────────────────────────────────────────────

    internal static void AddRef(nint unknown) =>
        _ = ((delegate* unmanaged<nint, uint>)Slot(unknown, AddRefSlot))(unknown);

    internal static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            _ = ((delegate* unmanaged<nint, uint>)Slot(unknown, ReleaseSlot))(unknown);
        }
    }

    internal static int QueryInterface(nint unknown, Guid iid, out nint found)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Slot(unknown, QueryInterfaceSlot))(unknown, &iid, &result);
        found = hr >= 0 ? result : 0;
        return hr;
    }

    // ── ICoreWebView2Environment, ICoreWebView2Controller ────────────────────

    internal static int CreateController(nint environment, nint parent, nint handler) =>
        ((delegate* unmanaged<nint, nint, nint, int>)Slot(environment, CreateControllerSlot))(environment, parent, handler);

    internal static int PutIsVisible(nint controller, bool visible) =>
        ((delegate* unmanaged<nint, int, int>)Slot(controller, PutIsVisibleSlot))(controller, visible ? 1 : 0);

    internal static int PutBounds(nint controller, Rect bounds) =>
        ((delegate* unmanaged<nint, Rect, int>)Slot(controller, PutBoundsSlot))(controller, bounds);

    internal static int NotifyParentWindowPositionChanged(nint controller) =>
        ((delegate* unmanaged<nint, int>)Slot(controller, NotifyParentWindowPositionChangedSlot))(controller);

    /// <summary><c>ICoreWebView2Environment5.add_BrowserProcessExited</c>: once every view is closed, the browser's processes are gone.</summary>
    internal static int AddBrowserProcessExited(nint environment5, nint handler) => Subscribe(environment5, AddBrowserProcessExitedSlot, handler);

    /// <summary>
    /// <c>add_AcceleratorKeyPressed</c>: with the view focused, keys go to the browser's own window, never the parent's procedure;
    /// this event is how the parent still sees F11, Esc and the Ctrl/Alt chords (a key that types nothing, or one with Ctrl or
    /// Alt held — Escape always).
    /// </summary>
    internal static int AddAcceleratorKeyPressed(nint controller, nint handler) => Subscribe(controller, AddAcceleratorKeyPressedSlot, handler);

    /// <summary>The keyboard to the page (<c>MoveFocus(COREWEBVIEW2_MOVE_FOCUS_REASON_PROGRAMMATIC)</c>): the address box's Esc and Enter.</summary>
    internal static int MoveFocus(nint controller) =>
        ((delegate* unmanaged<nint, int, int>)Slot(controller, MoveFocusSlot))(controller, 0);

    internal static int ControllerClose(nint controller) =>
        ((delegate* unmanaged<nint, int>)Slot(controller, ControllerCloseSlot))(controller);

    internal static int GetCoreWebView2(nint controller, out nint webView)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, nint*, int>)Slot(controller, GetCoreWebView2Slot))(controller, &result);
        webView = hr >= 0 ? result : 0;
        return hr;
    }

    // ── ICoreWebView2, ICoreWebView2_3 ───────────────────────────────────────

    internal static int GetSettings(nint webView, out nint settings)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, nint*, int>)Slot(webView, GetSettingsSlot))(webView, &result);
        settings = hr >= 0 ? result : 0;
        return hr;
    }

    internal static int Navigate(nint webView, string uri)
    {
        fixed (char* text = uri)
        {
            return ((delegate* unmanaged<nint, char*, int>)Slot(webView, NavigateSlot))(webView, text);
        }
    }

    internal static int AddNavigationStarting(nint webView, nint handler) => Subscribe(webView, AddNavigationStartingSlot, handler);

    internal static int AddWebMessageReceived(nint webView, nint handler) => Subscribe(webView, AddWebMessageReceivedSlot, handler);

    internal static int AddNewWindowRequested(nint webView, nint handler) => Subscribe(webView, AddNewWindowRequestedSlot, handler);

    /// <summary><c>add_ContainsFullScreenElementChanged</c>: the player's own full-screen button, which the window follows.</summary>
    internal static int AddContainsFullScreenElementChanged(nint webView, nint handler) => Subscribe(webView, AddContainsFullScreenElementChangedSlot, handler);

    internal static bool ContainsFullScreenElement(nint webView)
    {
        int value;
        return ((delegate* unmanaged<nint, int*, int>)Slot(webView, GetContainsFullScreenElementSlot))(webView, &value) >= 0 && value != 0;
    }

    internal static int ExecuteScript(nint webView, string script, nint handler)
    {
        fixed (char* text = script)
        {
            return ((delegate* unmanaged<nint, char*, nint, int>)Slot(webView, ExecuteScriptSlot))(webView, text, handler);
        }
    }

    internal static int PostWebMessageAsJson(nint webView, string json)
    {
        fixed (char* text = json)
        {
            return ((delegate* unmanaged<nint, char*, int>)Slot(webView, PostWebMessageAsJsonSlot))(webView, text);
        }
    }

    internal static int SetVirtualHostNameToFolderMapping(nint webView3, string hostName, string folder, int access)
    {
        fixed (char* host = hostName)
        fixed (char* path = folder)
        {
            return ((delegate* unmanaged<nint, char*, char*, int, int>)Slot(webView3, SetVirtualHostNameToFolderMappingSlot))(webView3, host, path, access);
        }
    }

    // ── The Browser window's (2026-10-13) ────────────────────────────────────

    /// <summary>The address the view is on (<c>get_Source</c>), null when it cannot say.</summary>
    internal static string? Source(nint webView) => GetString(webView, GetSourceSlot);

    /// <summary>The page's title (<c>get_DocumentTitle</c>), null when it cannot say.</summary>
    internal static string? DocumentTitle(nint webView) => GetString(webView, GetDocumentTitleSlot);

    internal static int AddSourceChanged(nint webView, nint handler) => Subscribe(webView, AddSourceChangedSlot, handler);

    internal static int AddNavigationCompleted(nint webView, nint handler) => Subscribe(webView, AddNavigationCompletedSlot, handler);

    internal static int AddDocumentTitleChanged(nint webView, nint handler) => Subscribe(webView, AddDocumentTitleChangedSlot, handler);

    internal static int Reload(nint webView) =>
        ((delegate* unmanaged<nint, int>)Slot(webView, ReloadSlot))(webView);

    internal static int GoBack(nint webView) =>
        ((delegate* unmanaged<nint, int>)Slot(webView, GoBackSlot))(webView);

    internal static int GoForward(nint webView) =>
        ((delegate* unmanaged<nint, int>)Slot(webView, GoForwardSlot))(webView);

    /// <summary>The page's load stopped (<c>Stop</c>): what a browser's ✕ does.</summary>
    internal static int Stop(nint webView) =>
        ((delegate* unmanaged<nint, int>)Slot(webView, StopSlot))(webView);

    /// <summary><c>add_HistoryChanged</c>: the back and forward list changed, so <see cref="CanGoBack"/> and <see cref="CanGoForward"/> may have.</summary>
    internal static int AddHistoryChanged(nint webView, nint handler) => Subscribe(webView, AddHistoryChangedSlot, handler);

    internal static bool CanGoBack(nint webView) => GetFlag(webView, GetCanGoBackSlot);

    internal static bool CanGoForward(nint webView) => GetFlag(webView, GetCanGoForwardSlot);

    /// <summary><c>ICoreWebView2_13.get_Profile</c>: the view's profile (an <c>ICoreWebView2Profile</c>, AddRef'd), or zero.</summary>
    internal static int GetProfile(nint webView13, out nint profile)
    {
        nint result;
        int hr = ((delegate* unmanaged<nint, nint*, int>)Slot(webView13, GetProfileSlot))(webView13, &result);
        profile = hr >= 0 ? result : 0;
        return hr;
    }

    /// <summary><c>ICoreWebView2Profile2.ClearBrowsingData(kinds, handler)</c>: the data of <paramref name="kinds"/> cleared; <paramref name="handler"/> gets the HRESULT (<see cref="WebViewHandler.Done"/>).</summary>
    internal static int ClearBrowsingData(nint profile2, int kinds, nint handler) =>
        ((delegate* unmanaged<nint, int, nint, int>)Slot(profile2, ClearBrowsingDataSlot))(profile2, kinds, handler);

    /// <summary><c>ICoreWebView2Profile2.ClearBrowsingDataAll(handler)</c>: everything the profile keeps.</summary>
    internal static int ClearBrowsingDataAll(nint profile2, nint handler) =>
        ((delegate* unmanaged<nint, nint, int>)Slot(profile2, ClearBrowsingDataAllSlot))(profile2, handler);

    private static bool GetFlag(nint instance, int slot)
    {
        int value;
        return ((delegate* unmanaged<nint, int*, int>)Slot(instance, slot))(instance, &value) >= 0 && value != 0;
    }

    /// <summary>
    /// A DevTools protocol command on the view's page (<c>CallDevToolsProtocolMethod</c>), its answer as JSON to
    /// <paramref name="handler"/> (the ExecuteScript shape, <see cref="WebViewHandler.Script"/>): how the Browser window reads its cookies
    /// (<c>Network.getCookies</c>, the answer <see cref="Web.BrowserCookie.Parse"/> already reads off the headless browser).
    /// </summary>
    internal static int CallDevToolsProtocolMethod(nint webView, string method, string parametersJson, nint handler)
    {
        fixed (char* name = method)
        fixed (char* json = parametersJson)
        {
            return ((delegate* unmanaged<nint, char*, char*, nint, int>)Slot(webView, CallDevToolsProtocolMethodSlot))(webView, name, json, handler);
        }
    }

    /// <summary><c>ICoreWebView2_4.add_DownloadStarting</c>: a file the page would download, which the window may cancel.</summary>
    internal static int AddDownloadStarting(nint webView4, nint handler) => Subscribe(webView4, AddDownloadStartingSlot, handler);

    /// <summary>A download cancelled and its UI kept away (<c>put_Cancel</c>, <c>put_Handled</c>).</summary>
    internal static int CancelDownload(nint args)
    {
        int hr = ((delegate* unmanaged<nint, int, int>)Slot(args, DownloadPutCancelSlot))(args, 1);
        return hr < 0 ? hr : ((delegate* unmanaged<nint, int, int>)Slot(args, DownloadPutHandledSlot))(args, 1);
    }

    /// <summary><c>ICoreWebView2_7.PrintToPdf</c> with the default print settings; <paramref name="handler"/> gets <c>(HRESULT, BOOL)</c> (<see cref="WebViewHandler.Flag"/>).</summary>
    internal static int PrintToPdf(nint webView7, string path, nint handler)
    {
        fixed (char* file = path)
        {
            return ((delegate* unmanaged<nint, char*, nint, nint, int>)Slot(webView7, PrintToPdfSlot))(webView7, file, 0, handler);
        }
    }

    /// <summary>Whether a navigation completed without a network error (<c>get_IsSuccess</c>; an HTTP error status still succeeds).</summary>
    internal static bool NavigationSucceeded(nint args)
    {
        int value;
        return ((delegate* unmanaged<nint, int*, int>)Slot(args, NavigationCompletedGetIsSuccessSlot))(args, &value) >= 0 && value != 0;
    }

    /// <summary>The completed navigation's HTTP status (<c>ICoreWebView2NavigationCompletedEventArgs2.get_HttpStatusCode</c>); 0 when it has none.</summary>
    internal static int NavigationStatus(nint args)
    {
        if (QueryInterface(args, IidNavigationCompletedArgs2, out nint args2) < 0)
        {
            return 0;
        }

        try
        {
            int status;
            return ((delegate* unmanaged<nint, int*, int>)Slot(args2, NavigationCompletedGetHttpStatusCodeSlot))(args2, &status) >= 0 ? status : 0;
        }
        finally
        {
            Release(args2);
        }
    }

    /// <summary>The starting navigation's id (<c>get_NavigationId</c>), the same for its redirects and its completion; 0 when the call fails.</summary>
    internal static ulong NavigationStartingId(nint args) => GetId(args, NavigationStartingGetNavigationIdSlot);

    /// <summary>The completed navigation's id (<c>get_NavigationId</c>), the one its start gave; 0 when the call fails.</summary>
    internal static ulong NavigationCompletedId(nint args) => GetId(args, NavigationCompletedGetNavigationIdSlot);

    private static ulong GetId(nint args, int slot)
    {
        ulong id;
        return ((delegate* unmanaged<nint, ulong*, int>)Slot(args, slot))(args, &id) >= 0 ? id : 0;
    }

    // ── ICoreWebView2Settings ────────────────────────────────────────────────

    /// <summary>
    /// What a video window needs and nothing more: no script dialogs (an <c>alert</c> would wait on a dialog nobody asked
    /// for), no status bar, devtools, browser context menu, host objects or Ctrl+wheel zoom. Returns the first failure.
    /// </summary>
    internal static int LockDown(nint settings)
    {
        int hr = PutFalse(settings, PutAreDefaultScriptDialogsEnabledSlot);
        hr = hr < 0 ? hr : PutFalse(settings, PutIsStatusBarEnabledSlot);
        hr = hr < 0 ? hr : PutFalse(settings, PutAreDevToolsEnabledSlot);
        hr = hr < 0 ? hr : PutFalse(settings, PutAreDefaultContextMenusEnabledSlot);
        hr = hr < 0 ? hr : PutFalse(settings, PutAreHostObjectsAllowedSlot);
        return hr < 0 ? hr : PutFalse(settings, PutIsZoomControlEnabledSlot);
    }

    /// <summary>
    /// What the Browser window needs (2026-10-13): a page that works as in a browser — its dialogs, context menu (paste into a
    /// form), status bar and zoom left on — with the DevTools window and host objects off. Returns the first failure.
    /// </summary>
    internal static int AllowBrowsing(nint settings)
    {
        int hr = PutFalse(settings, PutAreDevToolsEnabledSlot);
        return hr < 0 ? hr : PutFalse(settings, PutAreHostObjectsAllowedSlot);
    }

    // ── Event arguments ──────────────────────────────────────────────────────

    internal static string? NavigationUri(nint args) => GetString(args, NavigationGetUriSlot);

    internal static int CancelNavigation(nint args) =>
        ((delegate* unmanaged<nint, int, int>)Slot(args, NavigationPutCancelSlot))(args, 1);

    /// <summary>The page's message when it was a string (<c>postMessage(JSON.stringify(…))</c>), else null.</summary>
    internal static string? WebMessageString(nint args) => GetString(args, TryGetWebMessageAsStringSlot);

    internal static string? NewWindowUri(nint args) => GetString(args, NewWindowGetUriSlot);

    /// <summary>The key of an accelerator event as it goes down (<see cref="KeyDown"/> or <see cref="SystemKeyDown"/>), else null.</summary>
    internal static int? KeyGoingDown(nint args)
    {
        int kind;
        uint key;
        if (((delegate* unmanaged<nint, int*, int>)Slot(args, KeyGetKeyEventKindSlot))(args, &kind) < 0 || kind is not (KeyDown or SystemKeyDown))
        {
            return null;
        }

        return ((delegate* unmanaged<nint, uint*, int>)Slot(args, KeyGetVirtualKeySlot))(args, &key) >= 0 ? (int)key : null;
    }

    internal static int HandleKey(nint args) =>
        ((delegate* unmanaged<nint, int, int>)Slot(args, KeyPutHandledSlot))(args, 1);

    internal static int HandleNewWindow(nint args) =>
        ((delegate* unmanaged<nint, int, int>)Slot(args, NewWindowPutHandledSlot))(args, 1);

    private static string? GetString(nint instance, int slot)
    {
        char* text = null;
        int hr = ((delegate* unmanaged<nint, char**, int>)Slot(instance, slot))(instance, &text);
        string? value = TakeString(text);
        return hr >= 0 ? value : null;
    }

    private static int Subscribe(nint webView, int slot, nint handler)
    {
        long token;
        return ((delegate* unmanaged<nint, nint, long*, int>)Slot(webView, slot))(webView, handler, &token);
    }

    private static int PutFalse(nint settings, int slot) =>
        ((delegate* unmanaged<nint, int, int>)Slot(settings, slot))(settings, 0);

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
}
