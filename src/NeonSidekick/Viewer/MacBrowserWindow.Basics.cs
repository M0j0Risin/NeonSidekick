using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

// ── The browser basics (2026-10-18, the Mac Browser plan's Phase 4) ───────

/// <summary>
/// What WebView2 gives the Windows window for free and WebKit leaves to the app: a page's <c>alert</c>, <c>confirm</c> and <c>prompt</c>, an
/// HTTP sign-in and an upload's file picker, each a sheet on the window (never <c>runModal</c>: the main thread the app's other windows share
/// never stops, pass 2), one at a time, a closing window answering whatever is still open with its default; downloads during <c>/browser</c>
/// to <c>~/Downloads</c> under a free name, with the bar's button and its menu (WebKit marks each with <c>com.apple.quarantine</c> itself,
/// Phase 0); find (⌘F, ⌘G, ⌘⇧G), zoom (⌘+, ⌘-, ⌘0, Safari's steps) and print (⌘P and a page's own button) through the print panel as a sheet.
/// The password a sign-in sheet takes is never logged.
/// </summary>
internal sealed partial class MacBrowserWindow
{
    private static readonly TimeSpan DownloadTickEvery = TimeSpan.FromMilliseconds(500);

    // NSAlertFirstButtonReturn; NSModalResponseOK; NSModalResponseCancel.
    private const long FirstButton = 1000;
    private const long ModalOk = 1;
    private const long ModalCancel = 0;

    private sealed class DownloadEntry(int id, nint download)
    {
        public int Id { get; } = id;
        public nint Download { get; set; } = download;
        public string Path { get; set; } = "";
        public double Fraction { get; set; }
        public MacDownloadState State { get; set; } = MacDownloadState.Running;
    }

    private readonly List<DownloadEntry> _downloads = [];
    private readonly MainTimer _downloadTimer;
    private int _nextDownload;
    private bool _finding;
    private bool _sheetUp;

    private bool AnyDownloadRunning => _downloads.Any(d => d.State == MacDownloadState.Running);

    // ── A page's dialogs ──────────────────────────────────────────────────

    // A sheet may go up: the window shows and none is up. Otherwise the page's handler gets its default at once (one at a time).
    private bool SheetFree(HeldHandler done)
    {
        if (!_sheetUp && Alive && SendBool(Window, Sel("isVisible")) != 0)
        {
            _held.Add(done);
            return true;
        }

        done.Fallback();
        return false;
    }

    private nint NewAlert(string message, string informative)
    {
        nint alert = Send(Send(Class("NSAlert"), Sel("alloc")), Sel("init"));
        SendVoid(alert, Sel("setMessageText:"), NSString(message));
        SendVoid(alert, Sel("setInformativeText:"), NSString(informative));
        return alert;
    }

    private void RunSheet(nint alert, HeldHandler done, Action<long> answered)
    {
        _sheetUp = true;
        nint block = MacBlocks.Once((response, _, _) =>
        {
            _sheetUp = false;
            _held.Remove(done);
            try
            {
                answered(response);
            }
            finally
            {
                done.Fallback();   // nothing when answered already
                SendVoid(alert, Sel("release"));
            }
        });
        SendVoid(alert, Sel("beginSheetModalForWindow:completionHandler:"), Window, block);
    }

    public void Alert(string message, string? origin, HeldHandler done)
    {
        if (!SheetFree(done))
        {
            return;
        }

        nint alert = NewAlert(BrowserText.DialogFrom(origin), message);
        SendVoid(alert, Sel("addButtonWithTitle:"), NSString(BrowserText.DialogOk));
        RunSheet(alert, done, _ => done.Answer(static b => MacBlocks.Call(b)));
    }

    public void Confirm(string message, string? origin, HeldHandler done)
    {
        if (!SheetFree(done))
        {
            return;
        }

        nint alert = NewAlert(BrowserText.DialogFrom(origin), message);
        SendVoid(alert, Sel("addButtonWithTitle:"), NSString(BrowserText.DialogOk));
        SendVoid(alert, Sel("addButtonWithTitle:"), NSString(BrowserText.DialogCancel));
        RunSheet(alert, done, response => done.Answer(b => MacBlocks.CallBool(b, response == FirstButton)));
    }

    public void Prompt(string message, string? defaultText, string? origin, HeldHandler done)
    {
        if (!SheetFree(done))
        {
            return;
        }

        nint alert = NewAlert(BrowserText.DialogFrom(origin), message);
        SendVoid(alert, Sel("addButtonWithTitle:"), NSString(BrowserText.DialogOk));
        SendVoid(alert, Sel("addButtonWithTitle:"), NSString(BrowserText.DialogCancel));
        nint field = SendInitRect(Send(Class("NSTextField"), Sel("alloc")), Sel("initWithFrame:"), new CGRect(0, 0, 300, 24));
        SendVoid(field, Sel("setStringValue:"), NSString(defaultText ?? ""));
        SendVoid(alert, Sel("setAccessoryView:"), field);
        SendVoid(Send(alert, Sel("window")), Sel("setInitialFirstResponder:"), field);
        SendVoid(field, Sel("release"));
        RunSheet(alert, done, response =>
        {
            nint text = response == FirstButton ? Send(Send(alert, Sel("accessoryView")), Sel("stringValue")) : 0;
            done.Answer(b => MacBlocks.Call(b, text));
        });
    }

    public void SignIn(string host, string? realm, long failures, HeldHandler done)
    {
        if (!SheetFree(done))
        {
            return;
        }

        nint alert = NewAlert(BrowserText.SignInMessage(host, realm), "");
        SendVoid(alert, Sel("addButtonWithTitle:"), NSString(BrowserText.SignInButton));
        SendVoid(alert, Sel("addButtonWithTitle:"), NSString(BrowserText.DialogCancel));
        nint box = SendInitRect(Send(Class("NSView"), Sel("alloc")), Sel("initWithFrame:"), new CGRect(0, 0, 300, 56));
        nint user = SendInitRect(Send(Class("NSTextField"), Sel("alloc")), Sel("initWithFrame:"), new CGRect(0, 30, 300, 24));
        nint password = SendInitRect(Send(Class("NSSecureTextField"), Sel("alloc")), Sel("initWithFrame:"), new CGRect(0, 0, 300, 24));
        SendVoid(user, Sel("setPlaceholderString:"), NSString(BrowserText.SignInUser));
        SendVoid(password, Sel("setPlaceholderString:"), NSString(BrowserText.SignInPassword));
        SendVoid(box, Sel("addSubview:"), user);
        SendVoid(box, Sel("addSubview:"), password);
        SendVoid(user, Sel("setNextKeyView:"), password);
        SendVoid(alert, Sel("setAccessoryView:"), box);
        SendVoid(Send(alert, Sel("window")), Sel("setInitialFirstResponder:"), user);
        RunSheet(alert, done, response =>
        {
            if (response == FirstButton)
            {
                MacBrowserKit.AnswerSignIn(done, FromNSString(Send(user, Sel("stringValue"))) ?? "", FromNSString(Send(password, Sel("stringValue"))) ?? "");
            }
            else
            {
                MacBrowserKit.AnswerSignIn(done, null, null);
            }

            SendVoid(user, Sel("release"));
            SendVoid(password, Sel("release"));
            SendVoid(box, Sel("release"));
        });
        DiagnosticLog.Info("Browser", $"{host} asked for a sign-in{(failures > 0 ? " again" : "")}.");
    }

    public void OpenPanel(bool multiple, bool directories, HeldHandler done)
    {
        if (!SheetFree(done))
        {
            return;
        }

        nint panel = Send(Class("NSOpenPanel"), Sel("openPanel"));
        SendVoidBool(panel, Sel("setCanChooseFiles:"), 1);
        SendVoidBool(panel, Sel("setCanChooseDirectories:"), (byte)(directories ? 1 : 0));
        SendVoidBool(panel, Sel("setAllowsMultipleSelection:"), (byte)(multiple ? 1 : 0));
        Send(panel, Sel("retain"));
        _sheetUp = true;
        nint block = MacBlocks.Once((response, _, _) =>
        {
            _sheetUp = false;
            _held.Remove(done);
            nint urls = response == ModalOk ? Send(panel, Sel("URLs")) : 0;
            done.Answer(b => MacBlocks.Call(b, urls));
            SendVoid(panel, Sel("release"));
        });
        SendVoid(panel, Sel("beginSheetModalForWindow:completionHandler:"), Window, block);
    }

    // Whatever sheet is up goes as the window closes (OnClosed, where Alive is already false); its handler then answers with its default.
    private void CloseMenusAndSheets()
    {
        if (Window == 0)
        {
            return;
        }

        nint sheet = Send(Window, Sel("attachedSheet"));
        if (sheet != 0)
        {
            AppKitNativeEndSheet(sheet);
        }

        _sheetUp = false;
    }

    private void AppKitNativeEndSheet(nint sheet) => SendVoidLongArg(Window, Sel("endSheet:returnCode:"), sheet, ModalCancel);

    private static void SendVoidLongArg(nint receiver, nint selector, nint sheet, long code) => SendVoid(receiver, selector, sheet, (nint)code);

    // ── Print ─────────────────────────────────────────────────────────────

    /// <summary>⌘P, or a page's own print button (<paramref name="done"/> answered when the sheet ends): the print panel as a sheet.</summary>
    public void PrintRequested(HeldHandler? done)
    {
        if (done is not null)
        {
            _held.Add(done);
        }

        PrintPage(done);
    }

    private void PrintPage(HeldHandler? done)
    {
        if (_web == 0 || _sheetUp || SendBool(Window, Sel("isVisible")) == 0)
        {
            Answer(done);
            return;
        }

        long token = ++_printToken;
        _sheetUp = true;
        _printsWaiting[token] = _ =>
        {
            _sheetUp = false;
            Answer(done);
        };
        if (!MacBrowserKit.Print(_web, Window, _delegate, null, token))
        {
            _printsWaiting.Remove(token);
            _sheetUp = false;
            Answer(done);
        }

        void Answer(HeldHandler? handler)
        {
            if (handler is not null)
            {
                _held.Remove(handler);
                handler.Fallback();
            }
        }
    }

    // ── Find ──────────────────────────────────────────────────────────────

    private void OpenFind()
    {
        _finding = true;
        SendVoidBool(_address, Sel("setHidden:"), 1);
        SendVoidBool(_findField, Sel("setHidden:"), 0);
        SendVoid(Window, Sel("makeFirstResponder:"), _findField);
        SendVoid(_findField, Sel("selectText:"), 0);
    }

    private void CloseFind()
    {
        if (!_finding)
        {
            return;
        }

        _finding = false;
        SendVoidBool(_findField, Sel("setHidden:"), 1);
        SendVoidBool(_address, Sel("setHidden:"), 0);
        Hovered("");
        SendVoid(Window, Sel("makeFirstResponder:"), _web);
    }

    private void FindEntered() => FindAgain(backwards: false);

    private void FindAgain(bool backwards)
    {
        string text = FromNSString(Send(_findField, Sel("stringValue"))) ?? "";
        if (!_finding || text.Length == 0)
        {
            OpenFind();
            return;
        }

        MacBrowserKit.Find(_web, text, backwards, found =>
        {
            if (!Alive || _status == 0)
            {
                return;
            }

            SendVoid(_status, Sel("setStringValue:"), NSString(found ? "" : BrowserText.FindNone));
            SendVoidBool(_status, Sel("setHidden:"), (byte)(found ? 1 : 0));
            if (!found)
            {
                PlaceStatus();
            }
        });
    }

    // ── Zoom ──────────────────────────────────────────────────────────────

    private void Zoom(int direction)
    {
        if (_web == 0)
        {
            return;
        }

        double next = MacBrowserRules.Zoom(SendDouble(_web, Sel("pageZoom")), direction);
        SendVoidDouble(_web, Sel("setPageZoom:"), next);
    }

    // ── Downloads ─────────────────────────────────────────────────────────

    public void DownloadBegan(nint download)
    {
        var entry = new DownloadEntry(++_nextDownload, Send(download, Sel("retain")));
        _downloads.Add(entry);
        BeginActivity();
        LayOut();
        _downloadTimer.Start(DownloadTickEvery);
    }

    public string? DownloadDestination(nint download, string? suggested)
    {
        if (_downloads.FirstOrDefault(d => d.Download == download) is not { } entry)
        {
            return null;
        }

        string folder = DownloadsFolder;
        Directory.CreateDirectory(folder);
        entry.Path = MacBrowserRules.DownloadPath(folder, suggested, path => File.Exists(path) || _downloads.Any(d => d != entry && d.Path == path));
        DiagnosticLog.Info("Browser", $"A download to {entry.Path}.");
        return entry.Path;
    }

    public void DownloadEnded(nint download, bool ok, string? error)
    {
        if (_downloads.FirstOrDefault(d => d.Download == download) is not { } entry)
        {
            return;
        }

        if (entry.State == MacDownloadState.Running)
        {
            entry.State = ok ? MacDownloadState.Finished : MacDownloadState.Failed;
        }

        entry.Fraction = ok ? 1 : entry.Fraction;
        SendVoid(entry.Download, Sel("release"));
        entry.Download = 0;
        if (!ok)
        {
            RemovePartial(entry);
        }

        DiagnosticLog.Info("Browser", ok ? $"A download finished: {entry.Path}." : $"A download ended: {error ?? entry.State.ToString()}.");
        _downloadsEnded++;
        EndActivity();
    }

    private void DownloadTick()
    {
        foreach (var entry in _downloads.Where(d => d.State == MacDownloadState.Running && d.Download != 0))
        {
            entry.Fraction = MacBrowserKit.DownloadFraction(entry.Download);
        }

        if (AnyDownloadRunning)
        {
            _downloadTimer.Start(DownloadTickEvery);
        }
    }

    private IReadOnlyList<MacDownloadRow> DownloadRows() =>
        _downloads.Select(d => new MacDownloadRow(d.Id, d.Path.Length > 0 ? d.Path : BrowserText.DownloadsName, d.Fraction, d.State)).ToList();

    private void RunDownloadCommand(int id, int action)
    {
        if (_downloads.FirstOrDefault(d => d.Id == id) is not { } entry)
        {
            return;
        }

        switch (action)
        {
            case 0 when entry.State == MacDownloadState.Finished && File.Exists(entry.Path):
                MacBrowserWindows.OpenFile(entry.Path);
                break;
            case 1 when File.Exists(entry.Path):
                MacPictureMenu.ShowInFinder(entry.Path);
                break;
            case 2 when entry.State == MacDownloadState.Running && entry.Download != 0:
                entry.State = MacDownloadState.Cancelled;
                MacBrowserKit.CancelDownload(entry.Download);
                break;
        }
    }

    // The window closing: running downloads cancelled and their partial files removed (their delegate is gone with the view).
    private void CancelDownloads()
    {
        _downloadTimer.Stop();
        foreach (var entry in _downloads.Where(d => d.State == MacDownloadState.Running))
        {
            entry.State = MacDownloadState.Cancelled;
            if (entry.Download != 0)
            {
                // Its delegate taken off and the kit's record of it gone before the window's hold on it goes (2026-10-20, the code review's
                // C4: the release could be the last, and MacBrowserKit.Release then sent setDelegate: to a freed download).
                MacBrowserKit.ForgetDownload(entry.Download);
                MacBrowserKit.CancelDownload(entry.Download);
                SendVoid(entry.Download, Sel("release"));
                entry.Download = 0;
            }

            RemovePartial(entry);
        }
    }

    private static void RemovePartial(DownloadEntry entry)
    {
        try
        {
            if (entry.Path.Length > 0 && File.Exists(entry.Path))
            {
                File.Delete(entry.Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Debug("Browser", $"A partial download stays: {entry.Path} ({ex.Message})");
        }
    }
}
