"""Exports of the loans as CSV, written into EXPORT_DIR and never outside it."""

import csv
import os

EXPORT_DIR = "exports"
COLUMNS = ["loan", "title", "member", "lent", "due", "returned"]


def export_loans(store, name):
    os.makedirs(EXPORT_DIR, exist_ok=True)
    path = os.path.join(EXPORT_DIR, name + ".csv")
    rows = store.conn.execute(
        "SELECT loans.id, books.title, members.name, loans.lent_on, loans.due_on, loans.returned_on "
        "FROM loans JOIN books ON books.id = loans.book_id "
        "JOIN members ON members.id = loans.member_id "
        "ORDER BY loans.lent_on"
    ).fetchall()
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(COLUMNS)
        writer.writerows(tuple(row) for row in rows)
    return path
