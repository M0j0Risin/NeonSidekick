# Command-line options

Everything `NeonSidekick.exe` takes on its command line. [HEADLESS.md](HEADLESS.md) shows them in scripted runs, and [ENVIRONMENT.md](ENVIRONMENT.md) lists the variables that do the same for one launch.

Options that set something apply to this launch only. Both `--option value` and `--option=value` work.

| Option | What it does |
|---|---|
| `--url <url>` | Uses this LLM server. |
| `--model <id>` | Uses this model. |
| `--cwd <path>` | Uses this working directory. |
| `--profile <name>` | Opens this profile. |
| `--yolo` | Runs every shell command without asking. |
| `--no-police` | Lets shell commands touch paths outside the working directory. |
| `--log <path>` | Writes every diagnostic line to a file (`/log --file` opens it; `/log`'s window works without it). `{ts}` in the path becomes the start time, so each run gets its own log: `--log logs\NeonSidekick_{ts}.log` → `logs\NeonSidekick_20261003-142530.log`. |
| `--headless` | A plain text prompt over stdin/stdout, no TUI. See [HEADLESS.md](HEADLESS.md). |
| `--smoke` | Checks the native parts load, then exits. |
| `--audio-check` | Plays a test tone through the speech output, then exits. |
| `--voice-check` | Records up to 5 seconds from the microphone and transcribes it, then exits. |
| `--sql-check <connection>` | Proves the SQL tools against a connection of `sql.json`, then exits. |
| `--oracle-check <connection>` | The same for a connection of `oracle.json`. |
| `--mysql-check <connection>` | The same for a connection of `mysql.json`. |
| `--sqlite-check <database>` | The same for a database of `sqlite.json`, or a SQLite file in the working directory by its path. |
| `--postgres-check <connection>` | The same for a connection of `postgres.json`. |
| `--unc-check <share>` | The same for a share of `unc.json`. |
| `--docker-check` | The same for Docker Desktop's engine. |
| `--camera-check` | Opens the camera until the picture settles, reports its brightness and noise and encodes a test photo (nothing is saved; the light comes on briefly), then exits. |
| `--version` / `--help` | Prints the version or the help text. |

The `--*-check` modes keep nothing (`--sql-check` makes one temporary table inside a transaction to prove the rollback); see each tool's section in [TOOLS.md](TOOLS.md) for what they cover.
