# Components & Libraries

What Neon Sidekick is built on. Back to the [README](../README.md).

* `.NET 10 (NativeAOT)`
* `Spectre.Console`
* `Microsoft.Extensions.AI`
* `Microsoft.Extensions.AI.OpenAI`
* `ModelContextProtocol.Core`
* `Microsoft.Data.Sqlite`
* `Microsoft.Data.SqlClient`
* `Microsoft.SqlServer.TransactSql.ScriptDom`
* `Oracle.ManagedDataAccess.Core`
* `MySqlConnector`
* `Npgsql`
* `Microsoft.ML.OnnxRuntime`
* `KokoroSharp` (with `espeak-ng`, for the voices' pronunciation)
* `Whisper.net` (with its native `whisper.cpp` runtimes)
* `Silero VAD`
* `Vosk`
* `PhotoSauce.MagicScaler`
* `Markdig`
* `LibGit2Sharp` (with its native `libgit2`)
* `Microsoft.Web.WebView2` (Windows: its loader DLL, for the video window and the Browser window)
* `ASP.NET Core` (Kestrel, part of .NET: the Web UI's HTTP server)
* `System.Security.Cryptography.Pkcs` (pinned to a patched version for the SQL Server and Oracle clients)
* `llama.cpp` (`llama-server`, downloaded on first use of the embedded LLM)
* `pdf.js` (Mozilla, Apache-2.0; `pdfjs-dist` 4.10.38, embedded: reads PDFs in the headless browser for `read_pdf` and the shopping list), with its packed CMaps (Adobe's cmap-resources, BSD-3-Clause) for Chinese, Japanese and Korean text
* `pdf-lib` (MIT; `@cantoo/pdf-lib` 2.11.1, the maintained fork, embedded: edits PDFs in the headless browser for `pdf_edit` and `/pdf`'s edits).
  * Its build bundles culori, fflate, html-entities and node-html-better-parser (MIT), tslib (0BSD), and a stream decoder ported from pdf.js (Apache-2.0); `assets/pdflib/README.md` lists them
* Unicode's `emoji-test.txt` and CLDR's English annotations (the avatar picker's emoji, names and keywords) and CLDR's `windowsZones.xml` (the clock tools' time zones), under the [Unicode License v3](https://www.unicode.org/license.txt)

## System libraries

* **Windows:** WinMM (sound), WIC (pictures), Media Foundation (camera), GDI and DWM (screen capture, the app's windows), the print spooler, NVML, PDH and DXGI (performance bar), DPAPI and Credential Manager (secrets), the WebView2 Runtime (YouTube and the Browser window).
* **macOS:** AudioToolbox and Core Audio (sound), ImageIO (pictures), AVFoundation (camera), ScreenCaptureKit (screen capture), AppKit (the app's windows), WebKit (YouTube and the Browser window), IOKit and Metal (performance bar), Security (the Keychain).
* **macOS, too:** CUPS and PDFKit (printing), NetFS (network shares), Carbon (`/keycheck`), the unified log (event logs), Kerberos and GSS (Active Directory, SQL Server sign-in).
