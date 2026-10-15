# Neon Sidekick on a Mac

How Neon Sidekick runs on an Apple Silicon Mac: setting it up, what works, and what differs from Windows. Back to the [README](../README.md).

## Contents

- [Install and first run](#install-and-first-run)
- [What works](#what-works)
- [What differs](#what-differs)
- [SQL Server sign-in](#sql-server-sign-in)
- [Known issues](#known-issues)

## Install and first run

You need a Mac with Apple Silicon (M1 or later). Download and unpack the Mac package as the [README's install steps](../README.md#install) say, then run `./NeonSidekick` in Terminal or iTerm2. To build it yourself, see [Building on a Mac](BUILD.md#building-on-a-mac).

* **The Keychain question.** On the first run, and after each new build, macOS asks whether the app may use its Keychain key. Choose **Always Allow**, or it asks again each time.
* **Option as Meta.** The app's Ctrl+Alt shortcuts are Ctrl+Option on a Mac, and they reach the app only if Option acts as Meta. In Terminal.app turn on *Use Option as Meta key* (Settings › Profiles › Keyboard); in iTerm2 set *Left Option key* to *Esc+* (Settings › Profiles › Keys).
* **What it costs.** Option no longer types characters such as å in that profile (iTerm2's right Option still can). `/keycheck` says whether your terminal is set up.
* **Permissions belong to the terminal app** (Terminal or iTerm2), not to Neon Sidekick. macOS asks the first time the microphone, camera or screen is used, and the answer covers everything run in that terminal.
* **Changing a refusal.** Turn the terminal on in System Settings › Privacy & Security › Microphone, Camera, or Screen & System Audio Recording. After allowing Screen Recording, quit and reopen the terminal.
* **Being asked again.** Quit the app and reset the terminal's answer: `tccutil reset Microphone com.apple.Terminal` (or `Camera`, `ScreenCapture`; `com.googlecode.iterm2` for iTerm2).
* **The app's windows need the Mac's desktop.** Over SSH there are none, and a double-clicked picture opens in the app macOS opens it with.

## What works

| Area | On a Mac | Notes |
|---|---|---|
| Chat, tools, MCP, Git, sessions, skills, headless | Works | The shell is zsh; `bash` and `powershell` (pwsh, when installed) can be picked instead. |
| Database tools: SQL Server, Oracle, MySQL, PostgreSQL, SQLite | Works | SQL Server's Windows sign-in is Kerberos only ([below](#sql-server-sign-in)), and a named-pipe (`np:`) server can't be reached. |
| Embedded model | Works (Metal) | The NVFP4 models, made for NVIDIA cards, aren't offered. |
| Pictures: pasted, dropped, `image_info`, `image_edit` | Works | Pasting, dropping and `view_image` also take HEIC/HEIF, TIFF and AVIF; `image_info` and `image_edit` read JPEG XL too. Writes PNG, JPEG, GIF, BMP, TIFF and HEIF, not JPEG XL; `image_edit` refuses `chroma`, `colors` and `dither`. |
| Picture viewer, thumbnail browser, picture menu, log and process windows | Works | They need the Mac's desktop, not an SSH session. |
| Voice: Kokoro, Whisper, push-to-talk, wake word, interrupt | Works | Whisper runs on the GPU. |
| Camera | Works (macOS 14+) | The built-in camera, a USB webcam, or an iPhone as Continuity Camera. |
| Screen capture | Works (macOS 14+) | A window on another Space, or minimized, isn't listed until you bring it up. |
| YouTube playback | Works (macOS 14+) | The video window uses Safari's WebKit. |
| The Browser window (`/browser`) and bot checks in a window | Not available | It needs Windows. A page behind a bot check is reported as refused, and *Web open links in* `browser-window` opens links in your default browser. `/view report.pdf` opens the PDF in Preview. |
| Performance bar | Works | VRAM shows as **GMEM**: the GPU's share of the Mac's memory. |
| `/terminal` | Works | A new Terminal window, or a new tab in iTerm2. |
| `/shortcut` | Works | A `.command` file on the Desktop that opens in iTerm2 or Terminal. |
| Printing | Works (CUPS) | Office files are refused ([Printing](TOOLS.md#printing)). |
| PDFs: `convert_to_pdf`, `/pdf`, `read_pdf`, `pdf_edit`, attaching | Works, browser only | Making, reading, editing and attaching a PDF need Edge, Chrome, Brave or Chromium in `/Applications`, or the one *Web browser path* names. The `printer` engine (Microsoft Print to PDF) needs Windows. |
| Docker tools and `/docker` | Works | Docker Desktop, OrbStack, Colima or Rancher Desktop ([Docker on a Mac](TOOLS.md#docker-on-a-mac)). CPU and memory figures are the Linux VM's. |
| Event logs: the event-log tools and `/eventlog` | Not available | They read Windows' own event logs; a Mac has none. The group is never offered, 🪵 stays on the slab, and `/eventlog` says it needs Windows. |
| Docker servers | Not available | Docker Desktop on a Mac passes no GPU to containers; the embedded model is the Mac's local server instead. |
| UNC shares and outside folders | Works | A share is `smb://server/share`, mounted under `/Volumes` while the app runs ([UNC shares on a Mac](TOOLS.md#unc-shares-on-a-mac)). |
| `/keycheck` | Works | Finds macOS's own shortcuts, apps' exclusive hotkeys and the terminal's Option key. Most apps' shared hotkeys don't show. |
| Web UI (`/webui`) | Expected to work | Unproven until a Mac publish runs it. The token is kept with the keychain, and macOS asks to let the app accept incoming connections the first time it serves the network. The Tailscale tab is a `utun` interface's 100.64/10 address there (Tailscale's own adapter has no name of its own on a Mac). An iPhone's HEIC photo is sent to the page as a JPEG, as on Windows. |

## What differs

* **Secrets go in the Keychain.** API keys and passwords are encrypted with a key kept in your login Keychain (the *NeonSidekick* item). A `passwordStore: credman` password is a Keychain item: `security add-generic-password -s <target> -a <user> -w`.
* **Shell and scripts.** `run_command` runs in zsh unless *Shell default shell* says `bash` or `powershell`. Scripts run with `python3`, `node` or `pwsh`.
* **Paths.** `/cwd` completes a path that starts with `/`, but doesn't list network volumes, so typing never waits on a server. `/cwd ~` goes back to the profile's `files/` folder, not your home folder.
* **Dot-files** show in listings and searches. Only items hidden with `chflags hidden`, and `.git` folders, are skipped.
* **The embedded model.** Pick one in `/settings` › Servers › Embedded › *Embedded models*; the app downloads llama.cpp's Mac build with it. The server stops when the app ends, even if it crashes.
* **GPU memory.** The GPU may use about two thirds of the Mac's memory (10.7 GB of 16 GB), so a 16 GB Mac suits the Gemma 4 E2B and E4B models best.
* **The app's windows** have no Dock icon or menu bar. A window you open with a command takes the keyboard, while the video and camera windows, and one a chord opens behind, leave it with the terminal; Tab gives it back, and so does closing the last window.
* **Window keys.** ⌘W closes, ⌃⌘F is full screen (F11 too, once macOS's Show Desktop shortcut is off) and ⌫ stands in for Del. A Control-click opens the [picture menu](INTERFACE.md#the-picture-menu), with *Show in Finder*. Every window's keys are in [The app's windows](INTERFACE.md#the-apps-windows).
* **Printing** goes through CUPS onto the printer's default paper, in Helvetica Neue and Menlo, so a printout can wrap a little differently than on Windows.
* **What prints.** A PDF goes to the printer as it is (no landscape). Word, Excel and other files are refused; print them from the program that made them.
* **Thumbnails and the splash** are drawn in half blocks: full color in iTerm2, 256 colors (banded) in Terminal.app.
* **A microphone that sends only silence** gets one warning. A MacBook's microphone and camera are off with the lid closed: open the lid or pick another input in System Settings › Sound.
* **Echo.** Interrupting a reply by voice works with headphones and with a MacBook's own speakers; *STT interrupt echo guard (%)* keeps the reply's own words from counting.
* **The [video window](INTERFACE.md#the-video-window)** opens a link in the player in your default browser. Its cookies and cache are in `~/Library/WebKit/NeonSidekick/WebsiteDataStore/`; delete that folder to clear them.
* **Copy and paste.** Cmd+V is the terminal's paste, text only. The app's paste (Ctrl+V, Option+V or a right-click on the input line) takes a picture first, such as a screenshot copied with ⌃⇧⌘4; a file copied in Finder pastes as its path, which attaches a picture or a PDF. A right-click into a pane's filter or find pastes the first copied file's path; on Windows, files copied in Explorer paste nothing there.
* **Stand-in keys.** A Mac terminal can't send some chords, so Ctrl+D opens `/model`, Ctrl+] `/terminal` and Ctrl+Option+A `/memory`, and Option+Delete deletes a word. See [Keyboard shortcuts](INTERFACE.md#keyboard-shortcuts).
* **No key for these.** Type `/find` (a Mac terminal sends Ctrl+Shift+F as Ctrl+F, which is `/perfbar`), and use Shift+F3 to go back in a find. Click the ☺ for the emoji picker (Ctrl+J arrives as a line break).
* **Editors.** `/sys persona` and the like open the file in its default app. For `/draft`, set *Draft editor* to a command that waits, such as `code --wait` or `open -W -t`; vim can't share the terminal with the app.
* **Selecting text with the mouse** while the app has it: hold Fn (Terminal.app) or Option (iTerm2), or press Cmd+R in Terminal.app to turn the app's mouse off and on.

## SQL Server sign-in

A connection with `"auth": "windows"` in `sql.json` signs in with your Active Directory account and keeps no password in the file. On Windows that's Kerberos with an NTLM fallback; on a Mac it is **Kerberos only**, so you need a ticket from `kinit` (built into macOS).

It can't reach a SQL Server that isn't in a domain, such as one in a Docker container; use a SQL login (`"auth": "sql"`) there.

1. **Find the realm of the server's domain**, in capitals (e.g. `AD.EXAMPLE.EDU`). On a Windows PC on the domain, `$env:USERDNSDOMAIN` in PowerShell shows it; on a Mac bound to AD, `dsconfigad -show` (*Active Directory Domain*); or ask IT. It can differ from your sign-in name's suffix (`jdoe@example.edu`).
2. **Get a ticket**, on the organization's network or VPN: `kinit jdoe@AD.EXAMPLE.EDU` asks for the domain password and prints nothing when it works. Type the realm in capitals: a ticket taken in lowercase can't be used.
3. **Check it:** `klist` shows `krbtgt/AD.EXAMPLE.EDU@AD.EXAMPLE.EDU` and its expiry, usually about 10 hours away.
4. **Try the connection:** `./NeonSidekick --sql-check <connection>` reports the account it signed in as (`AD\jdoe`). `klist` then also lists the server's ticket, `MSSQLSvc/<server>:<port>`.

Name the server in `sql.json` by its full name, as `host`, `host,port` or `host\instance,port`. The ticket is asked for under that name and port (`MSSQLSvc/sqlhost.ad.example.edu:1451`), so use the name the server is registered under, never an IP address or an alias.

* `kinit --keychain …` keeps the password in your Keychain so later runs don't ask.
* A plain `kinit` with no name fails with `Configuration file does not specify default realm` unless `/etc/krb5.conf` names one (`[libdefaults]` / `default_realm = AD.EXAMPLE.EDU`).
* Tickets expire, and the connection fails until you run `kinit` again; `kinit -R` renews a still-valid ticket without the password, if the domain allows it.
* `kdestroy` throws away the default cache's tickets (`kdestroy -p <principal>` one account's, `kdestroy -A` all). The **Ticket Viewer** app (`/System/Library/CoreServices/Ticket Viewer.app`) does the same as `kinit`, `klist` and `kdestroy`.
* macOS keeps one ticket cache per account, and the newest `kinit` becomes the default the connection uses. `klist -A` lists them all; `kswitch -p jdoe@AD.EXAMPLE.EDU` makes another the default.

### A server in another domain

A ticket from your domain reaches only servers in that domain or in one that trusts it. Against a server in a domain with no trust, the connection fails with `The target principal name is incorrect. Cannot generate SSPI context.`, even though `kinit` worked. (A Windows PC may still connect, through NTLM.)

To see which domain a server is in, and whether you can get a ticket for it:

```sh
dig +short SRV _kerberos._tcp.test.example.edu                               # that domain's domain controllers, if it is one
kgetcred MSSQLSvc/sqlhost.test.example.edu:1451@AD.EXAMPLE.EDU               # a ticket from your own domain
kgetcred MSSQLSvc/sqlhost.test.example.edu:1451@TEST.EXAMPLE.EDU             # a ticket from the server's domain
```

* `kgetcred` prints nothing when the ticket is issued (`klist` then lists it).
* `Server (…) unknown` means that domain has no such name; `Server (krbtgt/TEST.EXAMPLE.EDU@AD.EXAMPLE.EDU) unknown` means your domain has no trust with the server's.
* Then sign in with an account in the server's domain (`kinit jdoe-test@TEST.EXAMPLE.EDU`), or ask the server's DBA for a SQL login.

If it fails:
* **`Cannot find KDC for realm`:** the Mac can't reach a domain controller. Check the network or VPN and the realm's spelling; `dig +short SRV _kerberos._tcp.ad.example.edu` should list the controllers, and if it lists none, IT can give you an `/etc/krb5.conf` that names them.
* **`Clock skew too great`:** the clock is more than 5 minutes off. Turn on *Set time and date automatically* (System Settings › General › Date & Time).
* **`Matching credential … not found`:** the ticket was taken with the realm in lowercase. Run `kdestroy -p <principal>`, then `kinit` again in capitals.
* **`The target principal name is incorrect. Cannot generate SSPI context.`:** no ticket could be had for the server's name. Its domain doesn't trust yours ([above](#a-server-in-another-domain)), or its Kerberos name (SPN) isn't registered for that host and port, which its admin must fix; `kgetcred` tells the two apart.

On the server, `sys.dm_exec_connections.auth_scheme` shows `KERBEROS` for such a connection (`NTLM` for a Windows client that fell back).

### Another account (runas)

A connection with `"auth": "runas"` signs in as another account, whose password the app keeps in the Keychain (`"passwordStore": "credman"`) or encrypted in `sql.json`. Set it with *SQL set password*, or with `security add-generic-password -s NeonSidekick/sql/<connection> -a <account> -w`.

On a Mac the app gets that account's Kerberos ticket itself at each connect, so you never run `kinit` for it.

* **The account is `name@REALM`**, e.g. `svc-reader@AD.EXAMPLE.EDU`. A Windows-style `DOMAIN\name` is refused, with the form to use; the app puts the realm in capitals.
* **It is Kerberos only**, so everything above about realms, trusts and the server's name applies.
* **macOS keeps its ticket** in a cache of its own: `klist -l` lists it, and `kdestroy -p <account>` removes it. Your own ticket and your `"auth": "windows"` connections are untouched.
* **If it fails,** the app says `the Kerberos sign-in as <account> failed (<status>)`. The status doesn't tell a wrong password from an account the server won't take, so check both.

## Known issues

* **YouTube may ask for consent again each run.** WebKit blocks the embedded player's third-party cookies, so a consent given inside the player isn't kept.
* **Switching the audio output mid-reply (to AirPods, say) hasn't been tried.** The next reply plays on the new default output.
* **With Bluetooth headphones the microphone may open on the reply's last word**, since they play a fifth of a second or so behind.
* **A large picture is decoded whole:** one at the 40-megapixel limit takes about 160 MB of memory for a moment.
* **Screen capture on macOS 14.0–15.1, and on a 1× screen beside a Retina one,** takes a path that hasn't been tried on a real Mac.
