# GapBench material

Planted test material for `tools/GapBench.cs`, which runs `/plan gaps` and `/code-audit` headless against it and scores
what the model found. **Every bug and gap here is on purpose.** Keep this folder out of any audit of this repository.

- `repo/`: "shelf", a small Python lending library. Its tests pass (`python -m unittest discover -s tests -t .`) although
  it holds the planted bugs.
- `repo.expect.json`: the planted bugs (file, line, a snippet that must be on that line, severity, dimension) and two
  decoys that look wrong but aren't.
- `plans/<name>.md`: plans about `repo/`, each missing things on purpose. `plans/<name>.expect.json` beside each holds the
  planted gaps and the keywords that match a reported gap to them.

The model never sees this README or any `*.expect.json`: a run copies only `repo/` and the one plan.

## Running a bench

```
dotnet run tools/GapBench.cs -- --check                                  # the material is consistent; no model
dotnet run tools/GapBench.cs -- --url http://127.0.0.1:30000/v1          # every item, 3 runs each
dotnet run tools/GapBench.cs -- --url http://127.0.0.1:30000/v1 --runs 1 --only version-flag,audit
dotnet run tools/GapBench.cs -- --url http://127.0.0.1:30000/v1 --only audit --set CodeAuditLowKeepsStreak=true
dotnet run tools/GapBench.cs -- --score publish/gapbench/<folder>        # re-score saved runs
```

A bench uses the published exe (`publish/output`) and takes hours: a run can need 50 minutes on a 27B model.

- **Leave the model server to the bench.** Other requests slow its passes and skew the times.
- **Keep the server up.** A Docker server chosen in NeonSidekick is stopped when that app exits. Either leave the app
  open and idle for the whole bench, or start the container yourself (`docker start <name>`) with the app closed. The
  bench's own runs never start or stop a server.
- **Pass the server's real address**, such as a Docker server's published port. The app's `docker.localhost` and
  `embedded.localhost` addresses are refused.
- **A bench that stops can be resumed.** Run the same command with the same `--out` and the same `--set` pairs:
  finished runs are skipped. `--fresh` starts the folder over.
- **`--set Name=value` tries a setting.** The name is a `profile.json` key; a misspelled one is refused.

The results' headline also says how often the plan shadow rule (gaps tagged low don't restart the clean streak) would
have stopped a review before a planted gap was caught, and how the gaps' severity tags agree with the planted ones.

## Adding material

- A new plan is a `plans/<name>.md` and a `plans/<name>.expect.json`. The bench finds it; nothing is listed in code.
- Keep keywords specific (`bulk_insert`, `migrat`, never `test`), and keep every plan in the app's shape: steps as
  `- [ ]` lines, each with `Done when:`.
- After any change, `--check` must pass. It checks every snippet against its line, the plans' shape, and that
  `repo/`'s tests pass.
