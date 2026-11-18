---
status: draft
revision: 1
requirement: "Show a member's loans, and let a member leave"
verify: "python -m unittest discover -s tests -t ."
---
# Member records

## Goal

`shelf member <id>` shows a member's name, email and open loans with the renewals each has left, and `shelf leave <id>` removes a member who has left the library.

## Context

- `shelf/store.py` holds the schema and every query; `members` has `id`, `name` and `email` (UNIQUE).
- `loans.renew` counts each renewal in `loans.renewals` and refuses after `MAX_RENEWALS` (2), so renewals left is `MAX_RENEWALS - loan["renewals"]`.
- `shelf/cli.py` has one `cmd_*` function per command, wired up in `build_parser`; `main` prints a `LoanError` and returns 1.

## Steps

- [ ] Add `Store.member_loans(member_id)` to `shelf/store.py`: the member's open loans, each joined with its book's title, ordered by due date.
  Done when: `python -m unittest discover -s tests -t .` passes.
- [ ] Add `shelf member <id>` to `shelf/cli.py`: the member's name and email, then one line per open loan: `<title>, due <date>, <n> renewals left`. An unknown id raises `LoanError(f"no member {id}")`.
  Done when: after `shelf join`, `shelf add` and `shelf lend` in a throwaway `--db`, `python -m shelf member 1` prints the member line and one loan line ending `2 renewals left`.
- [ ] Add `Store.remove_member(member_id)` (`DELETE FROM members WHERE id = ?`) to `shelf/store.py`, and `shelf leave <id>` to `shelf/cli.py`, printing `removed`. An unknown id raises the same `LoanError` as step 2.
  Done when: in step 2's throwaway `--db`, after `shelf join` adds a second member who borrows nothing, `python -m shelf leave 2` prints `removed`, and `python -m shelf list` no longer shows them.
- [ ] Add tests in `tests/test_members.py` for `member_loans` and `remove_member`.
  Done when: `python -m unittest discover -s tests -t .` passes, the new tests among them.
- [ ] Add both commands to the README's command list.
  Done when: the README shows a `shelf member` line and a `shelf leave` line.

## Files and areas

- `shelf/store.py`: `member_loans` and `remove_member`.
- `shelf/cli.py`: the `member` and `leave` commands.
- `tests/test_members.py`: new.
- `README.md`: two lines.

## Risks

- An unknown member id: both commands refuse it with `no member <id>`, as steps 2 and 3 say.

## Verification

- `python -m unittest discover -s tests -t .` passes.
- By hand: join two members, lend one a book, run `shelf member` for both, then `shelf leave` the one who never borrowed.
