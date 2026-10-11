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
* `Whisper.net`
* `Silero VAD`
* `Vosk`
* `PhotoSauce.MagicScaler`
* `Markdig`
* `LibGit2Sharp`
* `Microsoft.Web.WebView2` (Windows)
* `llama.cpp` (`llama-server`, downloaded on first use of the embedded LLM)
* `pdf.js` (Mozilla, Apache-2.0; `pdfjs-dist` 4.10.38, embedded: reads a store's reference PDFs for the shopping list inside the headless browser)
* Unicode's `emoji-test.txt` and CLDR's English annotations (the avatar picker's emoji, names and keywords; CLDR's `windowsZones.xml` maps the clock tools' time zones), under the [Unicode License v3](https://www.unicode.org/license.txt)

## System libraries

* **Windows:** WinMM (sound), WIC (pictures), Media Foundation (camera), GDI and DWM (screen capture, the app's windows), the print spooler, NVML, PDH and DXGI (performance bar), DPAPI and Credential Manager (secrets), the WebView2 Runtime (YouTube).
* **macOS:** AudioToolbox and Core Audio (sound), ImageIO (pictures), AVFoundation (camera), ScreenCaptureKit (screen capture), AppKit (the app's windows), WebKit (YouTube), IOKit and Metal (performance bar), Security (the Keychain).
