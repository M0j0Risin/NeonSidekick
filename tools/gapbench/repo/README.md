# shelf

A small lending library for one shelf of books: who has which book, when it is due, and what is overdue. One SQLite
file holds everything (`shelf.db` in the current folder, or `--db FILE`).

```
python -m shelf add 9780441013593 "Dune" "Frank Herbert"
python -m shelf join Ada ada@example.com
python -m shelf lend 1 1          # book 1 to member 1, due in 21 days
python -m shelf list
python -m shelf renew 1           # another 21 days, at most twice
python -m shelf return 1
python -m shelf overdue
python -m shelf export march      # writes exports/march.csv
```

## Layout

- `shelf/store.py`: the schema and every query.
- `shelf/loans.py`: lending, returning, renewing and the overdue rule.
- `shelf/cli.py`: the commands.
- `shelf/export.py`: the CSV export.

## Tests

```
python -m unittest discover -s tests -t .
```
