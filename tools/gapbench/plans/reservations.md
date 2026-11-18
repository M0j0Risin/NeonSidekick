---
status: draft
revision: 1
requirement: "Let a member reserve a book that is lent out"
verify: "python -m unittest discover -s tests -t ."
---
# Book reservations

## Goal

A member can reserve a book that someone else has borrowed, and `shelf list` shows which books are reserved and by whom.

## Context

- `shelf/store.py` holds the schema (`SCHEMA`, run with `executescript` when the store opens) and every query.
- `shelf/loans.py` has `lend`, `give_back` and `renew`; `LoanError` is the error the CLI prints.
- `shelf/cli.py` has one `cmd_*` function per command, wired up in `build_parser`.
- One reservation per book is enough: a waiting list is out of scope.

## Steps

- [ ] Add a `reserved_by INTEGER REFERENCES members(id)` column to the `books` table in `SCHEMA`, and `Store.reserve(book_id, member_id)` and `Store.reservation(book_id)` in `shelf/store.py`.
  Done when: `python -m unittest discover -s tests -t .` passes.
- [ ] Add `loans.reserve(store, book_id, member_id)`: it refuses a book that is on the shelf (lend it instead), a book already reserved, and a member reserving a book they have borrowed themselves, each with a `LoanError`.
  Done when: `python -m unittest discover -s tests -t .` passes.
- [ ] Add `shelf reserve <book> <member>` to `shelf/cli.py`, printing `reserved`.
  Done when: `python -m shelf reserve 1 2` prints `reserved` for a lent book.
- [ ] Show `reserved by <name>` after the loan state in `shelf list`.
  Done when: `python -m shelf list` shows the reservation made above.
- [ ] Add the command to the README's command list.
  Done when: the README shows a `shelf reserve` line.

## Files and areas

- `shelf/store.py`: the new column and the two reservation queries.
- `shelf/loans.py`: `reserve`.
- `shelf/cli.py`: the `reserve` command and the `list` line.
- `README.md`: one line.

## Risks

- A member could reserve every book: no limit is planned, since the shelf is small.

## Verification

- `python -m unittest discover -s tests -t .` passes.
- By hand: lend a book to one member, reserve it for another, and check `shelf list`.
