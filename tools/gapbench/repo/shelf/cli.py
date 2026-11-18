"""The command line: shelf [--db FILE] <command> [args]."""

import argparse
import sqlite3
import sys

from . import loans
from .export import export_loans
from .store import DuplicateBook, Store

DEFAULT_DB = "shelf.db"


def cmd_add(store, args):
    try:
        book_id = store.add_book(args.isbn, args.title, args.author)
    except DuplicateBook as e:
        print(e, file=sys.stderr)
        return 1
    print(f"added book {book_id}")
    return 0


def cmd_join(store, args):
    try:
        member_id = store.add_member(args.name, args.email)
    except sqlite3.IntegrityError:
        print(f"{args.email} is already a member", file=sys.stderr)
        return 1
    print(f"added member {member_id}")
    return 0


def cmd_list(store, args):
    for book in store.books():
        loan = store.open_loan(book["id"])
        state = f"lent, due {loan['due_on']}" if loan else "on the shelf"
        print(f"{book['id']:>4}  {book['title']} ({book['author']}): {state}")
    return 0


def cmd_find(store, args):
    for book in store.find_books(args.title):
        print(f"{book['id']:>4}  {book['title']} ({book['author']})")
    return 0


def cmd_lend(store, args):
    loans.lend(store, args.book, args.member)
    print("lent")
    return 0


def cmd_return(store, args):
    loans.give_back(store, args.book)
    print("returned")
    return 0


def cmd_renew(store, args):
    due = loans.renew(store, args.book)
    print(f"renewed, due {due.isoformat()}")
    return 0


def cmd_overdue(store, args):
    for loan in loans.overdue(store):
        print(f"{loan['title']}: {loan['name']}, due {loan['due_on']}")
    return 0


def cmd_export(store, args):
    path = export_loans(store, args.name)
    print(f"wrote {path}")
    return 0


def build_parser():
    parser = argparse.ArgumentParser(prog="shelf")
    parser.add_argument("--db", default=DEFAULT_DB, help="the database file")
    sub = parser.add_subparsers(dest="command", required=True)

    add = sub.add_parser("add", help="put a book on the shelf")
    add.add_argument("isbn")
    add.add_argument("title")
    add.add_argument("author")
    add.set_defaults(func=cmd_add)

    join = sub.add_parser("join", help="add a member")
    join.add_argument("name")
    join.add_argument("email")
    join.set_defaults(func=cmd_join)

    sub.add_parser("list", help="every book and whether it is lent").set_defaults(func=cmd_list)

    find = sub.add_parser("find", help="books whose title has the text")
    find.add_argument("title")
    find.set_defaults(func=cmd_find)

    for name, func, text in (("lend", cmd_lend, "lend a book"), ("return", cmd_return, "take a book back"),
                             ("renew", cmd_renew, "renew a loan")):
        command = sub.add_parser(name, help=text)
        command.add_argument("book", type=int)
        if name == "lend":
            command.add_argument("member", type=int)
        command.set_defaults(func=func)

    sub.add_parser("overdue", help="loans past their due date").set_defaults(func=cmd_overdue)

    export = sub.add_parser("export", help="write the loans to exports/NAME.csv")
    export.add_argument("name")
    export.set_defaults(func=cmd_export)
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    store = Store(args.db)
    try:
        return args.func(store, args)
    except loans.LoanError as e:
        print(e, file=sys.stderr)
        return 1
    finally:
        store.close()
