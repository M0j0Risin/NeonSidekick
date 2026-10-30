# Neon Sidekick on a Mac

How Neon Sidekick runs on an Apple Silicon Mac: setting it up, what works, and what differs from Windows. Back to the [README](../README.md).

## Contents

- [Install and first run](#install-and-first-run)
- [What works](#what-works)
- [What differs](#what-differs)
- [The Browser window on a Mac](#the-browser-window-on-a-mac)
- [SQL Server sign-in](#sql-server-sign-in)
- [Known issues](#known-issues)

## Install and first run

You need an Apple Silicon Mac (M1 or later). Unpack the Mac package as the [README's install steps](../README.md#install) say and run `./NeonSidekick` in iTerm2 or Terminal, or [build it yourself](BUILD.md#building-on-a-mac).

* **Use iTerm2.** The app works best in [iTerm2](https://iterm2.com), and we recommend it over Terminal.app. Terminal.app runs it, with less.
* **What iTerm2 adds.** Pictures, the splash and the robot in full color (256 banded colors in Terminal.app), and a right Option that still types å with Meta set up.
* **The Keychain question.** On the first run and after each new build, macOS asks whether the app may use its Keychain key. Choose **Always Allow**, or it asks every time.
* **Option as Meta.** The Ctrl+Alt shortcuts are Ctrl+Option on a Mac, and reach the app only if Option acts as Meta.
* **Setting it up.** Set iTerm2's *Left Option key* to *Esc+* (Settings › Profiles › Keys), or turn on Terminal.app's *Use Option as Meta key* (Settings › Profiles › Keyboard).
* **What it costs.** Option then no longer types characters such as å (iTerm2's right Option still can). `/keycheck` says whether your terminal is set up.
* **Permissions belong to the terminal app** (iTerm2 or Terminal). macOS asks the first time the microphone, camera or screen is used, and the answer covers everything run in that terminal.
* **Changing a refusal.** Turn the terminal on in System Settings › Privacy & Security › Microphone, Camera, or Screen & System Audio Recording. After allowing Screen Recording, quit and reopen the terminal.
* **Being asked again.** Quit the app and run `tccutil reset Microphone com.apple.Terminal` (or `Camera`, `ScreenCapture`; `com.googlecode.iterm2` for iTerm2).
* **The app's windows need the Mac's desktop.** Over SSH there are none, and a double-clicked picture opens in its default app.

## What works

| Area | On a Mac | Notes |
|---|---|---|
| Chat, tools, MCP, Git, sessions, skills, headless | Works | The shell is zsh unless you pick another ([below](#setup)). |
| Database tools: SQL Server, Oracle, MySQL, PostgreSQL, SQLite, DuckDB | Works | SQL Server's Windows sign-in is Kerberos only ([below](#sql-server-sign-in)); no named-pipe (`np:`) servers. |
| Embedded model | Works (Metal) | The NVFP4 models, made for NVIDIA cards, aren't offered. |
| Pictures: pasted, dropped, `image_info`, `image_edit` | Works | Also reads HEIC/HEIF, TIFF and AVIF; writes no JPEG XL, and `image_edit` refuses `chroma`, `colors` and `dither` ([Editing pictures](TOOLS.md#editing-pictures)). |
| Picture viewer, thumbnail browser, picture menu, log, process and Docker log windows | Works | On the Mac's desktop, not over SSH. |
| Voice: Kokoro, Whisper, push-to-talk, wake word, interrupt | Works | Whisper runs on the GPU. |
| Camera | Works (macOS 14+) | The built-in camera, a USB webcam, or an iPhone as Continuity Camera. |
| Screen capture | Works (macOS 14+) | A minimized window, or one on another Space, isn't listed until brought up. Without Screen Recording, `screen_list` lists only the monitors. |
| YouTube playback | Works (macOS 14+) | The video window is Safari's WebKit. Before macOS 14 there's no video window, so no Web UI YouTube tab. |
| The Browser window (`/browser`) and bot checks in a window | Works (macOS 14+) | WebKit, as Safari ([below](#the-browser-window-on-a-mac)). With no window (before macOS 14, over SSH), saved links open in your default browser, a bot check is reported refused, and `/view report.pdf` opens in Preview. |
| Performance bar | Works | VRAM shows as **GMEM** ([The performance bar](INTERFACE.md#the-performance-bar)). |
| `/terminal` | Works | A new Terminal window, or a new tab in iTerm2. |
| `/shortcut` | Works | A `.command` file on the Desktop that opens in iTerm2 or Terminal. |
| Printing | Works (CUPS) | The printers in System Settings › Printers & Scanners; Office files are refused ([below](#files--printing)). |
| PDFs: `convert_to_pdf`, `/pdf`, `read_pdf`, `pdf_edit`, attaching | Works, browser only | Needs Edge, Chrome, Brave or Chromium in `/Applications`, or the one *Web browser path* names. The `printer` engine needs Windows. |
| Docker tools and `/docker` | Works | Docker Desktop, OrbStack, Colima or Rancher Desktop; CPU and memory are the Linux VM's ([Docker on a Mac](TOOLS.md#docker-on-a-mac)). |
| Event logs: the event-log tools and `/eventlog` | Works on an administrator account | The unified log, crash reports, install.log and `.logarchive` folders; no remote computers or `.evtx` ([Event logs on a Mac](TOOLS.md#event-logs-on-a-mac)). |
| Services and scheduled tasks: their tools, `/services`, `/tasks` | Not available | A Mac has neither. `/tools`, the toolbar and the help leave them out, and the commands say there's nothing to read. |
| Monitors: their tools, `/monitor`, the home bar's 🖥️, `--monitor-check` | Not available | Windows only. `/tools` and the help leave them out; a profile's 🖥️ stays saved but isn't drawn. |
| Active Directory: its tools, `/ad`, `--ad-check` | Works | Read-only, Kerberos only; `runas` takes `name@REALM`, and there's no built-in `current` domain ([Active Directory on a Mac](TOOLS.md#active-directory-on-a-mac)). |
| Network tools and `/net` | Works | `net_certs` reads only `my` (your login keychain) and `root` (the system's trust). |
| Docker servers | Not available | Docker on a Mac passes no GPU to containers; the embedded model is the Mac's local server instead. |
| UNC shares and outside folders | Works | `\\server\share`, `//server/share` or `smb://` paths, mounted under `/Volumes` while the app runs ([UNC shares on a Mac](TOOLS.md#unc-shares-on-a-mac)). |
| `/keycheck` | Works | Finds macOS's shortcuts, apps' exclusive hotkeys and the terminal's Option key; most shared hotkeys don't show. |
| Web UI (`/webui`) | Works | The token is in the Keychain; macOS asks once to allow incoming connections. Pictures show scaled originals (no thumbnails); Home's *Now playing* needs macOS 14+, and its PC card shows the Mac's meters. |

## What differs

### Setup

* **Secrets go in the Keychain.** API keys and passwords are encrypted with a key in your login Keychain (the *NeonSidekick* item). A `passwordStore: credman` password is a Keychain item: `security add-generic-password -s <target> -a <user> -w`.
* **Shell and scripts.** `run_command` runs in zsh unless *Shell default shell* says `bash` or `powershell` (pwsh, when installed). Scripts run with `python3`, `node` or `pwsh`.
* **Editors.** `/sys persona` and the like open the file in its default app. For `/draft`, set *Draft editor* to a command that waits (`code --wait`, `open -W -t`); vim can't share the terminal with the app.
* **The embedded model.** Install one as on Windows ([SETTINGS](SETTINGS.md)); the app downloads llama.cpp's Mac build with it. Its server stops when the app ends, even on a crash.
* **GPU memory.** The GPU may use about two thirds of the Mac's memory (10.7 GB of 16 GB), so a 16 GB Mac suits the Gemma 4 E2B and E4B models best. GMEM on the performance bar shows its share in use.

### Keys & mouse

* **Stand-in keys.** A Mac terminal can't send some chords, so Ctrl+D opens `/model`, Ctrl+] `/terminal`, Ctrl+Option+A `/memory`, and Option+Delete deletes a word. On a bottom-pane list, Delete is fn+⌫ ([Keyboard shortcuts](INTERFACE.md#keyboard-shortcuts)).
* **No key for these.** Shift+F3 goes back in a find. Click the ☺ for the emoji picker (Ctrl+J arrives as a line break).
* **Copy and paste.** Cmd+V is the terminal's paste, text only. The app's paste (Ctrl+V, Option+V, or a right-click where the terminal passes it on) takes a picture first, such as a ⌃⇧⌘4 screenshot.
* **Pasting files.** A file copied in Finder pastes as its path, which attaches a picture or a PDF. A right-click in a filter or find box pastes the first copied file's path (on Windows, nothing).
* **Right-clicks in iTerm2.** Two settings give the right button to the app: *Enable mouse reporting* on (Settings › Profiles › Terminal), and the *Right Button* › *Open Context Menu* row deleted (Settings › Pointer › Bindings).
* **Without them** iTerm2 opens its own menu and the app sees no right-click. Use Ctrl+V to paste, and double-click a bar's open space for `/settings`.
* **Selecting text with the mouse:** hold Fn (Terminal.app) or Option (iTerm2), or press Cmd+R in Terminal.app to turn the app's mouse off and on.

### Windows

* **No Dock icon or menu bar.** A window you open with a command takes the focus; the video and camera windows, and windows opened in the background, leave it with the terminal.
* **Tab** returns the focus to the terminal (except in the Browser window, whose pages keep Tab), and so does closing the last window.
* **Shared keys.** ⌘W closes, ⌃⌘F is full screen (F11 too, once macOS's Show Desktop shortcut is off) and ⌫ stands in for Del. Each window's keys are in [The app's windows](INTERFACE.md#the-apps-windows).
* **The [picture menu](INTERFACE.md#the-picture-menu)** opens with a Control-click too, and has *Show in Finder*.
* **Thumbnails, the splash and its robot** are drawn in half blocks: full color in iTerm2, 256 colors (banded) in Terminal.app.
* **The [video window](INTERFACE.md#the-video-window)** sends a link clicked in the player where *YouTube open links in* says (anything but a video or playlist to your default browser). Delete `~/Library/WebKit/NeonSidekick/WebsiteDataStore/` to clear its cookies and cache.

### Files & printing

* **Paths.** `/cwd` completes a path starting with `/` but lists no network volumes, so typing never waits on a server. `/cwd ~` goes to the profile's `files/` folder, not your home folder.
* **Dot-files** show in listings and searches; only items hidden with `chflags hidden`, and `.git` folders, are skipped.
* **Deletes go to the Trash.** *File deletes to Recycle bin* reads *File deletes to Trash*; a volume with no Trash (a network or FAT volume) deletes directly.
* **Ralph loops and code audits on exFAT.** A plan on an exFAT drive (or a share that takes neither hard links nor exclusive renames) can't take its `ralph.lock` safely, so `/plan ralph` refuses it. FAT32 and the Mac's own disk work. `/code-audit` runs there anyway, with a warning that a second Neon Sidekick could write the same report.
* **Printing** goes through CUPS onto the printer's default paper, in Helvetica Neue and Menlo, so a printout may wrap a little differently. A PDF goes as it is (no landscape).
* **Office files don't print.** Word, Excel and other such files are refused; print them from the program that made them.

### Voice

* **A microphone that sends only silence** gets one warning. A MacBook's microphone and camera are off with the lid closed: open it, or pick another input in System Settings › Sound.
* **Echo.** Interrupting a reply by voice works with headphones and a MacBook's own speakers; *STT interrupt echo guard (%)* keeps the reply's own words from counting.

## The Browser window on a Mac

The [Browser window](INTERFACE.md#the-browser-window) does what it does on Windows: `/browser`, bot checks (from the web tools or a Web UI page), saved links, history, `/view report.pdf`, `/webui browser`, and *… open links in* `browser-window`. What differs:

* **WebKit, as Safari.** It sends Safari's user agent, so the web tools replay a check's cookies as Safari, with Safari's headers.
* **Safari's keys:** listed in [The Browser window](INTERFACE.md#the-browser-window).
* **No Dock icon.** Bring a window lost behind the terminal back by clicking it, with Mission Control, or with any `/browser` command. Over a full-screen terminal it opens on that Space.
* **Its data** is a WebKit store per profile, named in the profile's `webkit-browser.id` (kept on a rename, removed by `/profile delete`); `/browser clear all` empties it, except while a bot check runs.
* **`/profile delete` keeps the profile** when its store can't be removed: in a session with no desktop (over SSH), or when WebKit is busy or doesn't answer. It says why; delete it again from a desktop session, after restarting the app if WebKit still holds the store.
* **A page that keeps crashing** is reloaded once, then shows the window's error page; Reload tries again.
* **Ctrl+B closes a minimized window** too, as it does one on screen.
* **One copy at a time:** a second copy of the app on the same profile can't open the window.
* **Its own extras:** an error page that Reload retries, a status line with a hovered link's address, and page dialogs, HTTP sign-in and upload pickers as sheets.
* **Downloads** go to ~/Downloads under a free name, with a bar button for them; macOS quarantines each, as Safari's. The first download or upload may make macOS ask whether your terminal may use Downloads, Documents or Desktop.
* **What it lacks:** saved passwords, form autofill, passkeys and security keys (password or code sign-ins work), location, web notifications and developer tools.
* **Tracking prevention:** as in Safari, third-party cookies are blocked, so a sign-in that needs them fails.
* **A covered or minimized window** still draws, and a bot check driven from the Web UI still passes; its page's timers run slowly, as in Safari.

## SQL Server sign-in

A connection with `"auth": "windows"` in `sql.json` signs in with your Active Directory account and keeps no password in the file. Windows falls back to NTLM; a Mac is **Kerberos only**, so you need a ticket from macOS's built-in `kinit`. A server outside a domain, such as one in a Docker container, needs a SQL login (`"auth": "sql"`).

1. **Find the realm** of the server's domain, in capitals (`AD.EXAMPLE.EDU`): `$env:USERDNSDOMAIN` on a domain PC, `dsconfigad -show` on a Mac bound to AD (*Active Directory Domain*), or ask IT. It can differ from your sign-in's suffix (`jdoe@example.edu`).
2. **Get a ticket** on the organization's network or VPN: `kinit jdoe@AD.EXAMPLE.EDU` asks for the domain password and prints nothing when it works. Type the realm in capitals; a ticket taken in lowercase can't be used.
3. **Check it:** `klist` shows `krbtgt/AD.EXAMPLE.EDU@AD.EXAMPLE.EDU` and its expiry, usually about 10 hours away.
4. **Try the connection:** `./NeonSidekick --sql-check <connection>` reports the account it signed in as (`AD\jdoe`). `klist` then also lists the server's ticket, `MSSQLSvc/<server>:<port>`.

**Name the server** in `sql.json` by its full name: `host`, `host,port` or `host\instance,port`. The ticket is asked for under that name and port (`MSSQLSvc/sqlhost.ad.example.edu:1451`), so use the name it's registered under, never an IP address or an alias.

* `kinit --keychain …` keeps the password in your Keychain so later runs don't ask.
* A bare `kinit` fails with `Configuration file does not specify default realm` unless `/etc/krb5.conf` names one (`[libdefaults]` / `default_realm = AD.EXAMPLE.EDU`).
* Tickets expire, and the connection fails until you run `kinit` again; `kinit -R` renews a still-valid one without the password, if the domain allows it.
* `kdestroy` drops the default cache's tickets (`-p <principal>` one account's, `-A` all). The **Ticket Viewer** app (`/System/Library/CoreServices/Ticket Viewer.app`) does what `kinit`, `klist` and `kdestroy` do.
* macOS keeps a ticket cache per account, and the newest `kinit` becomes the default the connection uses. `klist -A` lists them; `kswitch -p jdoe@AD.EXAMPLE.EDU` makes another the default.

If it fails:
* **`Cannot find KDC for realm`:** the Mac can't reach a domain controller. Check the network or VPN and the realm's spelling; `dig +short SRV _kerberos._tcp.ad.example.edu` should list the controllers. If it lists none, IT can give you an `/etc/krb5.conf` that names them.
* **`Clock skew too great`:** the clock is more than 5 minutes off. Turn on *Set time and date automatically* (System Settings › General › Date & Time).
* **`Matching credential … not found`:** the ticket was taken with the realm in lowercase. Run `kdestroy -p <principal>`, then `kinit` again in capitals.
* **`The target principal name is incorrect. Cannot generate SSPI context.`:** no ticket for the server's name. Its domain doesn't trust yours ([below](#a-server-in-another-domain)), or its Kerberos name (SPN) isn't registered for that host and port, which its admin must fix; `kgetcred` tells the two apart.

On the server, `sys.dm_exec_connections.auth_scheme` shows `KERBEROS` for such a connection (`NTLM` for a Windows client that fell back).

### A server in another domain

A ticket from your domain reaches only servers in that domain or one that trusts it. With no trust, the connection fails with `The target principal name is incorrect. Cannot generate SSPI context.`, even though `kinit` worked (a Windows PC may still connect, through NTLM). To see which domain a server is in, and whether you can get a ticket for it:

```sh
dig +short SRV _kerberos._tcp.test.example.edu                               # that domain's domain controllers, if it is one
kgetcred MSSQLSvc/sqlhost.test.example.edu:1451@AD.EXAMPLE.EDU               # a ticket from your own domain
kgetcred MSSQLSvc/sqlhost.test.example.edu:1451@TEST.EXAMPLE.EDU             # a ticket from the server's domain
```

* `kgetcred` prints nothing when the ticket is issued (`klist` then lists it).
* `Server (…) unknown`: that domain has no such name. `Server (krbtgt/TEST.EXAMPLE.EDU@AD.EXAMPLE.EDU) unknown`: your domain has no trust with the server's.
* Then sign in with an account in the server's domain (`kinit jdoe-test@TEST.EXAMPLE.EDU`), or ask the server's DBA for a SQL login.

### Another account (runas)

A connection with `"auth": "runas"` signs in as another account, its password in the Keychain (`"passwordStore": "credman"`) or encrypted in `sql.json`. Set it with *SQL set password*, or `security add-generic-password -s NeonSidekick/sql/<connection> -a <account> -w`. The app gets that account's Kerberos ticket itself at each connect, so you never run `kinit` for it.

* **The account is `name@REALM`** (`svc-reader@AD.EXAMPLE.EDU`). `DOMAIN\name` is refused, with the form to use; the app puts the realm in capitals.
* **It's Kerberos only**, so the realm, trust and server-name rules above apply.
* **Its ticket** is in a cache of its own: `klist -l` lists it, `kdestroy -p <account>` removes it. Your own ticket and `"auth": "windows"` connections are untouched.
* **If it fails,** the app says `the Kerberos sign-in as <account> failed (<status>)`. The status doesn't tell a wrong password from an account the server won't take, so check both.

## Known issues

* **A bot check with the screen locked** hasn't been tried; covered or minimized works ([above](#the-browser-window-on-a-mac)).
* **Event logs under a standard account, or from a terminal without Full Disk Access,** haven't been tried.
* **A `.logarchive` with links inside** is refused; whether the unified log would follow them hasn't been tried. install.log's lines from a reset clock (2001) are passed over, a run of 50 older ones ending the read; that hasn't been tried on a real log.
* **The Web UI's Tailscale tab** (a `utun` interface's address) and **the chat page's picture strip** haven't been tried.
* **Deleting on a volume with no Trash** (a network or FAT volume) deletes directly; that path hasn't been tried.
* **YouTube may ask for consent each run:** WebKit blocks the player's third-party cookies, so a consent given in it isn't kept.
* **Switching the audio output mid-reply** (to AirPods, say) hasn't been tried; the next reply plays on the new default output.
* **With Bluetooth headphones the microphone may open on the reply's last word**, since they play about a fifth of a second behind.
* **A large picture is decoded whole:** one at the 40-megapixel limit takes about 160 MB of memory for a moment.
* **Screen capture on macOS 14.0–15.1, and on a 1× screen beside a Retina one,** hasn't been tried on a real Mac.
* **The robot splash following the mouse** hasn't been tried on a Mac. Terminal.app reports the mouse only with View › Allow Mouse Reporting on.
