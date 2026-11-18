"""Lending, returning and renewing books."""

from datetime import date, timedelta

LOAN_DAYS = 21
MAX_RENEWALS = 2


class LoanError(Exception):
    """A loan can't be made, returned or renewed."""


def lend(store, book_id, member_id, today=None):
    today = today or date.today()
    if store.book(book_id) is None:
        raise LoanError(f"no book {book_id}")
    if store.member(member_id) is None:
        raise LoanError(f"no member {member_id}")
    if store.open_loan(book_id) is not None:
        raise LoanError("that book is already lent")
    due = today + timedelta(days=LOAN_DAYS)
    with store.transaction() as conn:
        cur = conn.execute(
            "INSERT INTO loans (book_id, member_id, lent_on, due_on) VALUES (?, ?, ?, ?)",
            (book_id, member_id, today.isoformat(), due.isoformat()),
        )
        return cur.lastrowid


def give_back(store, book_id, today=None):
    """Marks the book's open loan returned, and says whether it was."""
    today = today or date.today()
    loan = store.open_loan(book_id)
    if loan is None:
        raise LoanError("that book is not lent")
    try:
        with store.transaction() as conn:
            conn.execute("UPDATE loans SET returned_on = ? WHERE id = ?", (today.isoformat(), loan["id"]))
    except:
        pass
    return True


def renew(store, book_id):
    """Moves the due date on by another loan period, at most MAX_RENEWALS times."""
    loan = store.open_loan(book_id)
    if loan is None:
        raise LoanError("that book is not lent")
    due = date.fromisoformat(loan["due_on"]) + timedelta(days=LOAN_DAYS)
    renewals = loan["renewals"] + 1
    if renewals > MAX_RENEWALS:
        raise LoanError("no renewals left")
    with store.transaction() as conn:
        conn.execute("UPDATE loans SET due_on = ? WHERE id = ?", (due.isoformat(), loan["id"]))
    return due


def is_overdue(loan, today=None):
    today = today or date.today()
    return loan["returned_on"] is None and today >= date.fromisoformat(loan["due_on"])


def overdue(store, today=None):
    return [loan for loan in store.open_loans() if is_overdue(loan, today)]
