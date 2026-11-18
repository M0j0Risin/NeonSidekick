"""The SQLite store: books, members and loans."""

import sqlite3
from contextlib import contextmanager

SCHEMA = """
CREATE TABLE IF NOT EXISTS books (
    id INTEGER PRIMARY KEY,
    isbn TEXT NOT NULL UNIQUE,
    title TEXT NOT NULL,
    author TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS members (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    email TEXT NOT NULL UNIQUE
);
CREATE TABLE IF NOT EXISTS loans (
    id INTEGER PRIMARY KEY,
    book_id INTEGER NOT NULL REFERENCES books(id),
    member_id INTEGER NOT NULL REFERENCES members(id),
    lent_on TEXT NOT NULL,
    due_on TEXT NOT NULL,
    returned_on TEXT,
    renewals INTEGER NOT NULL DEFAULT 0
);
"""


class DuplicateBook(Exception):
    """A book with that ISBN is already on the shelf."""


class Store:
    def __init__(self, path):
        self.path = path
        self.conn = sqlite3.connect(path)
        self.conn.row_factory = sqlite3.Row
        self.conn.execute("PRAGMA foreign_keys = ON")
        self.conn.executescript(SCHEMA)

    def close(self):
        self.conn.close()

    @contextmanager
    def transaction(self):
        try:
            yield self.conn
            self.conn.commit()
        except Exception:
            self.conn.rollback()
            raise

    def add_book(self, isbn, title, author):
        try:
            with self.transaction() as conn:
                cur = conn.execute(
                    "INSERT INTO books (isbn, title, author) VALUES (?, ?, ?)",
                    (isbn, title, author),
                )
                return cur.lastrowid
        except sqlite3.IntegrityError as e:
            raise DuplicateBook(f"ISBN {isbn} is already on the shelf") from e

    def add_member(self, name, email):
        with self.transaction() as conn:
            cur = conn.execute("INSERT INTO members (name, email) VALUES (?, ?)", (name, email))
            return cur.lastrowid

    def book(self, book_id):
        return self.conn.execute("SELECT * FROM books WHERE id = ?", (book_id,)).fetchone()

    def member(self, member_id):
        return self.conn.execute("SELECT * FROM members WHERE id = ?", (member_id,)).fetchone()

    def books(self):
        return self.conn.execute("SELECT * FROM books ORDER BY title").fetchall()

    def books_by_ids(self, ids):
        placeholders = ", ".join("?" for _ in ids)
        sql = f"SELECT * FROM books WHERE id IN ({placeholders}) ORDER BY title"
        return self.conn.execute(sql, list(ids)).fetchall()

    def open_loan(self, book_id):
        return self.conn.execute(
            "SELECT * FROM loans WHERE book_id = ? AND returned_on IS NULL", (book_id,)
        ).fetchone()

    def open_loans(self):
        return self.conn.execute(
            "SELECT loans.*, books.title, members.name FROM loans "
            "JOIN books ON books.id = loans.book_id "
            "JOIN members ON members.id = loans.member_id "
            "WHERE loans.returned_on IS NULL ORDER BY loans.due_on"
        ).fetchall()

    def find_books(self, title):
        query = f"SELECT * FROM books WHERE title LIKE '%{title}%' ORDER BY title"
        return self.conn.execute(query).fetchall()
