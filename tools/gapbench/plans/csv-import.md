---
status: draft
revision: 1
requirement: "Put many books on the shelf at once from a CSV file"
verify: "python -m unittest discover -s tests -t ."
---
# Import books from a CSV file

## Goal

`shelf import books.csv` puts every book in the file on the shelf in one command, instead of one `shelf add` per book.

## Context

- The file has a header row `isbn,title,author` and one book per row, as a spreadsheet saves it (UTF-8, maybe with a BOM).
- `shelf/store.py` holds every query; `Store.add_book` puts one book on the shelf.
- `shelf/cli.py` has one `cmd_*` function per command, wired up in `build_parser`.

## Steps

- [ ] Add `shelf import <file>` to `build_parser` in `shelf/cli.py`, calling a new `cmd_import`.
  Done when: `python -m shelf import --help` shows the command.
- [ ] In `cmd_import`, read the rows with `csv.DictReader` (opened with `encoding="utf-8-sig"`) and pass them to `store.bulk_insert(rows)`.
  Done when: `python -m shelf import books.csv` puts the file's books on the shelf.
- [ ] Refuse a row with an empty ISBN, title or author, naming its line number.
  Done when: a file with a blank title prints the line it is on.
- [ ] Print how many books were put on the shelf.
  Done when: the import prints `added N books`.
- [ ] Add tests in `tests/test_import.py`: a good file, a row with a blank title, and a file with a BOM.
  Done when: `python -m unittest discover -s tests -t .` passes.
- [ ] Add the command to the README's command list.
  Done when: the README shows a `shelf import` line.

## Files and areas

- `shelf/cli.py`: `cmd_import` and its parser entry.
- `tests/test_import.py`: new.
- `README.md`: one line.

## Risks

- A large file is read into memory whole; a shelf's catalog is small, so that is fine.

## Verification

- `python -m unittest discover -s tests -t .` passes.
- By hand: import a file saved from a spreadsheet, then `python -m shelf list`.
