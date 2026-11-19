---
status: draft
revision: 1
requirement: "Take a worn-out book off the shelf, and keep its loan history"
verify: "python -m unittest discover -s tests -t ."
---
# Withdraw books

## Goal

`shelf withdraw <book>` takes a worn-out book off the shelf for good. Its past loans stay in `shelf export`.

## Context

- `shelf/store.py` holds the schema and every query; `books` has `id`, `isbn` (UNIQUE), `title` and `author`.
- Books worn out enough to withdraw have been lent many times over the years.
- `shelf/cli.py` has one `cmd_*` function per command, wired up in `build_parser`; `main` prints a `LoanError` and returns 1.

## Steps

- [ ] Add `Store.withdraw_book(book_id)` (`DELETE FROM books WHERE id = ?`) to `shelf/store.py`.
  Done when: `python -m unittest discover -s tests -t .` passes.
- [ ] Add `shelf withdraw <book>` to `shelf/cli.py`, printing `withdrawn`. An unknown id raises `LoanError(f"no book {id}")`.
  Done when: in a throwaway `--db`, after `shelf add`, `python -m shelf withdraw 1` prints `withdrawn`.
- [ ] Add the command to the README's command list.
  Done when: the README shows a `shelf withdraw` line.

## Files and areas

- `shelf/store.py`: `withdraw_book`.
- `shelf/cli.py`: the `withdraw` command.
- `README.md`: one line.

## Risks

- An unknown book id: the command refuses it with `no book <id>`, as step 2 says.

## Verification

- `python -m unittest discover -s tests -t .` passes.
- By hand: add a book, then `shelf withdraw` it.
