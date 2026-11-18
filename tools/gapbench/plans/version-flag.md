---
status: draft
revision: 1
requirement: "shelf --version prints the version"
verify: "python -m unittest discover -s tests -t ."
---
# A --version flag

## Goal

`python -m shelf --version` prints `shelf 0.3.1` (the package's version) and exits with code 0, without opening the database.

## Context

- The version lives in one place, `__version__` in `shelf/__init__.py`; `pyproject.toml` reads it from there (`tool.setuptools.dynamic`).
- `build_parser` in `shelf/cli.py` builds the `argparse` parser; `main` opens the store only after `parse_args` returns.
- argparse's `action="version"` prints its text and exits with code 0 while parsing, before the required subcommand is checked, so `--version` alone is accepted.

## Steps

- [ ] In `build_parser` (`shelf/cli.py`), add `parser.add_argument("--version", action="version", version=f"shelf {__version__}")`, importing `__version__` with `from . import __version__`.
  Done when: `python -m shelf --version` prints `shelf 0.3.1`.
- [ ] Add `tests/test_cli.py` with a `unittest` test that calls `main(["--version"])` with `sys.stdout` redirected (`contextlib.redirect_stdout`), expects `SystemExit` with code 0, and checks the printed line is `shelf ` followed by `shelf.__version__`.
  Done when: `python -m unittest discover -s tests -t .` passes, the new test among them.
- [ ] Add `python -m shelf --version` to the README's command list.
  Done when: the README shows the `--version` line.

## Files and areas

- `shelf/cli.py`: the flag in `build_parser`, and the import.
- `tests/test_cli.py`: new, one test.
- `README.md`: one line.

## Risks

- None worth a step: `--version` exits before `main` opens the store, so no `shelf.db` is created by it.

## Verification

- `python -m unittest discover -s tests -t .` passes.
- `python -m shelf --version` prints `shelf 0.3.1` in a folder with no `shelf.db`, and no `shelf.db` appears.
