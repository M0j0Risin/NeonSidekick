# Command-line options

Everything `NeonSidekick.exe` (`./NeonSidekick` on a Mac) takes on its command line. Options apply to this launch only, and both `--option value` and `--option=value` work. [HEADLESS.md](HEADLESS.md) shows them in scripted runs; [ENVIRONMENT.md](ENVIRONMENT.md) lists the variables that do the same.

| Option | What it does |
|---|---|
| `--url <url>` | Uses this LLM server: a URL, or `embedded`, `claude-code` or `docker:<container>` (Windows only). |
| `--model <id>` | Uses this model. |
| `--cwd <path>` | Uses this working directory. |
| `--profile <name>` | Opens this profile without changing the saved choice in `settings.json`. An unknown name exits with code 2. |
| `--yolo` | Runs every shell command without asking. |
| `--no-police` | Lets shell commands touch paths outside the working directory. |
| `--log <path>` | Appends every diagnostic line to a file. `{ts}` in the path becomes the start time, so each run gets its own log (`logs/neon_{ts}.log` → `logs/neon_20261003-142530.log`). `/log file` opens it. |
| `--headless` | A plain text prompt over stdin/stdout, no TUI. See [HEADLESS.md](HEADLESS.md). |
| `--smoke` | Checks the native parts load, then exits. |
| `--audio-check` | Plays a test tone through the speech output, then exits. |
| `--voice-check` | Records up to 5 seconds from the microphone, transcribes it, then exits. On a Mac the terminal app needs the Microphone permission. |
| `--sql-check <connection>` | Tests the SQL tools against a connection in `sql.json`, then exits. |
| `--oracle-check <connection>` | The same for a connection in `oracle.json`. |
| `--mysql-check <connection>` | The same for a connection in `mysql.json`. |
| `--sqlite-check <database>` | The same for a database in `sqlite.json`, or a SQLite file in the working directory. |
| `--postgres-check <connection>` | The same for a connection in `postgres.json`. |
| `--unc-check <share>` | The same for a share in `unc.json`. On a Mac it mounts a network share and unmounts it on exit. |
| `--docker-check` | The same for the Docker engine: Docker Desktop's pipe on Windows, the engine's socket on a Mac. |
| `--camera-check` | Opens the camera, reports brightness and noise, and encodes a test photo (nothing saved; the light comes on briefly), then exits. On a Mac it needs macOS 14 and the terminal app's Camera permission. |
| `--version` / `--help` (`-h`) | Prints the version or the help text. |

The `--*-check` modes change nothing (`--sql-check` makes one temporary table inside a transaction that is rolled back). Each tool's section in [TOOLS.md](TOOLS.md) says what its check covers.

**Exit codes:** 0 done (a check passed), 1 a check failed, 2 a bad argument or an unknown profile, 3 a headless run in which a shell command was refused ([HEADLESS.md](HEADLESS.md)).

`--mcp-relay` and `--llama-guard` are used when the app starts its own executable (for the Claude Code server, and on a Mac to stop the embedded LLM's server if the app crashes). Don't pass them yourself.
