---
status: draft
revision: 1
requirement: "Charge a late fee when an overdue book comes back"
verify: "python -m unittest discover -s tests -t ."
---
# Late fees

## Goal

When a book comes back after its due date, `shelf return` prints the late fee owed. The fee is shown, not stored: payment is handled at the desk.

## Context

- `loans.give_back(store, book_id, today=None)` closes the open loan; the loan row has `due_on` as an ISO date.
- `cmd_return` in `shelf/cli.py` calls `give_back` and prints `returned`.
- Amounts are kept as whole cents (`int`) and printed with two decimals, so no float rounding creeps in.

## Steps

- [ ] Add `FEE_PER_DAY_CENTS` and `FEE_CAP_CENTS` to `shelf/loans.py`, and `late_fee_cents(due_on, returned_on)`: the whole days late (0 when returned on or before the due date) times the rate, held at the cap.
  Done when: `python -m unittest discover -s tests -t .` passes.
- [ ] Make `give_back` read the loan's `due_on` before closing it and return the fee in cents (0 when on time).
  Done when: `python -m unittest discover -s tests -t .` passes.
- [ ] In `cmd_return`, print `returned, late fee 1.50` when the fee is above 0, else `returned` as now.
  Done when: returning a book three days late prints the fee for three days.
- [ ] Add tests in `tests/test_loans.py` for `late_fee_cents`: returned on the due date (0), one day late, and far enough late to reach the cap.
  Done when: `python -m unittest discover -s tests -t .` passes, the new tests among them.

## Files and areas

- `shelf/loans.py`: the two constants, `late_fee_cents`, and `give_back`'s return value.
- `shelf/cli.py`: `cmd_return`'s line.
- `tests/test_loans.py`: the fee tests.

## Risks

- `give_back` now returns cents instead of `True`: `cmd_return` is its only caller.
- Open question (your call): the rate and whether there is a cap. I'd pick 0.20 a day with no cap.

## Verification

- `python -m unittest discover -s tests -t .` passes.
- By hand: lend a book with an old date, return it, and read the fee.
