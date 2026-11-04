# Command-line options

Everything `NeonSidekick.exe` (`./NeonSidekick` on a Mac) takes on its command line. Back to the [README](../README.md).

* **For this launch only:** options are never saved, and a flag outranks its environment variable and the saved setting.
* **Two spellings:** `--option value` or `--option=value`; use the `=` form for a value that starts with `-`.
* **See also:** [HEADLESS.md](HEADLESS.md) for scripted runs, and [ENVIRONMENT.md](ENVIRONMENT.md) for the variables.

| Option | What it does |
|---|---|
| (none) | Opens the interactive app (the TUI). |
| `--url <url>` | Uses this LLM server: a URL, `embedded` (also turns *Embedded servers enabled* on for the run), `claude-code` or `docker:<container>` (Windows only). |
| `--model <id>` | Uses this model. |
| `--cwd <path>` | Uses this working directory; a relative path starts from where you launch. |
| `--profile <name>` | Opens this profile for this launch (without it, `neon`). An unknown name lists the profiles and exits with code 2. |
| `--yolo` | Runs every shell command and script without asking; the shell police still applies unless you add `--no-police`. |
| `--no-police` | Turns off the shell police: commands may name paths outside the working directory, and the forbidden-string and database checks stop. |
| `--log <path>` | Appends every diagnostic line to a file (making its folder); `/log file` opens it. `{ts}` becomes the start time (`logs/neon_{ts}.log` → `logs/neon_20261003-142530.log`). |
| `--headless` | A plain text prompt over stdin/stdout, no TUI; it loads `neon` unless `--profile` or `NEONSIDEKICK_PROFILE` names another ([HEADLESS.md](HEADLESS.md)). |
| `--smoke` | Checks that the native parts load, then exits. |
| `--audio-check` | Plays a 440 Hz test tone through the speech output, then exits. |
| `--voice-check` | Records up to 5 seconds from the microphone and transcribes it, then exits. On a Mac the terminal needs the Microphone permission. |
| `--sql-check <connection>` | Tests the SQL tools against a connection in `sql.json`, then exits. |
| `--oracle-check <connection>` | The same for a connection in `oracle.json`. |
| `--mysql-check <connection>` | The same for a connection in `mysql.json`. |
| `--sqlite-check <database>` | The same for a database in `sqlite.json`, or a SQLite file in the working directory. |
| `--duckdb-check <database>` | The same for a database in `duckdb.json`, or a DuckDB file in the working directory; it also checks that data files outside the working directory are refused. |
| `--postgres-check <connection>` | The same for a connection in `postgres.json`. |
| `--unc-check <share>` | The same for a share in `unc.json`. On a Mac it mounts the share and unmounts it on exit. |
| `--eventlog-check <computer>` | The same for a computer in `eventlog.json`, or `local`: the sign-in, the newest System event, and whether the Security log is readable. On a Mac only `local`: the unified log, crash reports and install.log. |
| `--network-check <host>` | Tests the network tools against a host (a DNS lookup, a ping, port 443, a TLS handshake) and opens a certificate store. Reads only. |
| `--services-check <computer>` | The same for a computer in `services.json`, or `local`: its services listed and one read in full. Changes nothing. Windows only. |
| `--tasks-check <computer>` | The same for a computer in `tasks.json`, or `local`: the Task Scheduler connected, the tasks listed and one read. Windows only. |
| `--ad-check <domain>` | The same for a domain in `ad.json`, or `current`: the bind, the domain's root object and policy, and one user read. On a Mac the bind line says how it connected, and there's no `current`. |
| `--docker-check` | The same for the Docker engine: Docker Desktop's pipe on Windows, the engine's socket on a Mac. |
| `--camera-check` | Opens the camera, reports brightness and noise, and encodes a test photo (nothing saved; the light comes on briefly), then exits. On a Mac: macOS 14+ and the terminal's Camera permission. |
| `--monitor-check` | Lists the monitors and reads each one's capabilities and current input over DDC/CI, then exits. Changes nothing. Windows only. |
| `--version` | Prints the version and exits. |
| `--help` (`-h`, `-?`, `/?`) | Prints the help text and exits. |

The check modes change nothing: they read only (`--sql-check` makes one temporary table inside a transaction that is rolled back). Each tool's section in [TOOLS.md](TOOLS.md) says what its check covers. `--headless` ignores them.

**Exit codes:** 0 done (a check passed), 1 a check failed, 2 a bad argument, an unknown profile, or a copy started during an [update](COMMANDS.md#updating) that finished, 3 a headless run in which a shell command was refused ([When a command is refused](HEADLESS.md#when-a-command-is-refused)).

`--mcp-relay`, `--llama-guard`, `--shell-guard` and `--update-probe` are for when the app starts its own executable: the Claude Code server, on a Mac stopping the embedded model's server or background commands if the app crashes, and test-starting an update. Don't pass them yourself.
