# Tools

Back to the [README](../README.md). In the app, `/tools` switches these and shows what each one does.

The tools the model can call, grouped as `/tools` and `/sys` show them. Each group has a switch that offers or withholds all of it: *File tools*, *Image tools*, *GitLib tools*, *Shell command policy*, *Obsidian tools*, *SQL tools*, *Oracle tools*, *MySQL tools*, *SQLite tools*, *PostgreSQL tools*, *UNC tools*, *Docker tools*, *ComfyUI tools*, *Home Assistant tools*, *Print tools*, *Camera tool*, *Screen capture tool*, *YouTube tools*, *Claude Code advisor tool*, *Web tools*, *Memory mode*, *Agent skills*, *Session tool*, *Ask user* and *MCP servers*. Single tools switch on the Offered tab of `/tools`.

## Contents

- [Clock & Timers](#clock--timers)
  - [Clock](#clock)
  - [Timers](#timers)
- [Help](#help)
- [Files & Git](#files--git)
  - [Files](#files)
  - [GitLib](#gitlib)
- [Obsidian](#obsidian)
- [SQL](#sql)
- [Oracle](#oracle)
- [MySQL and MariaDB](#mysql-and-mariadb)
- [SQLite](#sqlite)
- [PostgreSQL](#postgresql)
- [UNC shares and outside folders](#unc-shares-and-outside-folders)
- [Docker](#docker)
- [Home Assistant](#home-assistant)
- [Printing](#printing)
- [ComfyUI](#comfyui)
- [Media](#media)
  - [Images](#images)
  - [Camera](#camera)
  - [Screen](#screen)
  - [YouTube](#youtube)
- [Shell & Web](#shell--web)
  - [Shell](#shell)
  - [Web](#web)
- [Memory, Skills & Sessions](#memory-skills--sessions)
  - [Memory](#memory)
  - [Skills](#skills)
  - [Sessions](#sessions)
  - [Claude advisor](#claude-advisor)
  - [Advisors](#advisors)
  - [Questions](#questions)
  - [Plan](#plan)
- [MCP servers](#mcp-servers)

## Clock & Timers

### Clock

| Tool | Arguments | What it does |
|---|---|---|
| `get_current_time` | `zone?` | The current date, time, weekday and time zone. Seeded at the start of every conversation. |
| `shift_date` | `date, days?, weeks?, months?, years?` | Moves a date by days, weeks, months or years and gives its weekday. |
| `date_difference` | `from, to` | The days from one date to another (negative when the second is earlier), and in years, months and days once the gap is a month or more. |

### Timers

| Tool | Arguments | What it does |
|---|---|---|
| `start_timer` | `name?, hours?, minutes?, seconds?` | Starts a named countdown that alerts you when it ends. Several can run at once. |
| `stop_timer` | `name` | Stops a running timer, or silences one that has gone off. |
| `list_timers` | — | Every running timer and its time left. |

## Help

| Tool | Arguments | What it does |
|---|---|---|
| `neon_help` | `query?, kind?` | NeonSidekick's own manual: every form of a command; a setting's effect, default and place (pane › tab › row, with the menu on a group tab: `/tools`' Files, Integrations, Database and Media, `/settings`' Servers and Voice); a pane's or tab's rows; the keys. `query` is a command (`/camera`), a setting, a pane or tab (`/tools camera`), a key (`Ctrl+H`) or plain words; none gives an overview. `kind` (`command`, `setting`, `pane`, `keys`) narrows it; `kind: command` alone lists every command. |

It is offered on every turn, headless and in plan mode too, and the rules tell the model to use it instead of guessing. It reads only the built-in reference, never your current settings. To withhold it, switch it off on the Offered tab.

## Files & Git

### Files

Every path is relative to the working directory; nothing outside it can be reached.

| Tool | Arguments | What it does |
|---|---|---|
| `get_working_directory` | — | The working directory's path. Seeded at the start of every conversation. |
| `search_files` | `text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | Searches text files for a word, phrase or regex (`file:line: text`, with context lines when asked). Without `text`, lists a folder, a tree (`depth` 2–4), files matching a name pattern, or the most recently changed files. `limit` goes up to *File search max results*. A full path in the profile's `images` folder lists or searches there too (read-only; the results are full paths), so "show me the last 5 pictures" is one listing and one `view_image`. |
| `file_info` | `path` | A file's size, modified time, lines, words, line ending and BOM; a folder's counts and total size. Also checks that something exists. Takes a full path in the profile's `images` folder too. |
| `read_file` | `path, start_line?, max_lines?` | Reads a text file or part of it (a negative `start_line` counts from the end). A partial read names the line to continue from. |
| `write_file` | `path, content, mode?` | Writes a text file: `create` (default; leaves an existing file alone), `overwrite` or `append`. Reports size, lines and words. |
| `patch_file` | `path, old_text, new_text, replace_all?` | Replaces one occurrence of `old_text` (or all with `replace_all`), exactly or tolerating differences in spacing, indentation, escapes and typographic quotes. Shows the edited lines. |
| `create_directory` | `path` | Creates a folder and any missing parents. |
| `move` | `from, to, overwrite?` | Renames or moves a file or folder; replaces nothing unless `overwrite`. |
| `copy` | `from, to, overwrite?` | Copies a file or folder, under the same rule; a folder over a folder merges. |
| `delete` | `path` | Deletes a file or folder for good. `.git`, anything in it and a folder holding one are refused. |
| `zip` | `path, to?, overwrite?` | Packs a file or folder into a `.zip`, beside it by default. A folder's entries are written sorted by path, so the same folder makes the same zip on Windows and a Mac. |
| `unzip` | `path, to?, overwrite?` | Extracts a `.zip` into a folder, all or nothing. |
| `open` | `path?, share?` | Opens a file in your own editor or viewer, or a folder in Explorer (Finder on a Mac; the working directory by default). `share` (or a full `\\server\share` path) opens one on a UNC share; a network runas share is refused. |
| `convert_to_pdf` | `path? \| url? \| markdown?, title?, to?, overwrite?, landscape?, paper?` | Makes a PDF in the working directory from a file, a web page (needs *Web tools* too) or Markdown it writes (see Making PDFs). Not in plan mode. |

### GitLib

Git inside the app (LibGit2Sharp), for when the shell is off or the model should never run `git`. Turn *GitLib tools* off to leave git to the shell.

* Local only: no `fetch`, `pull`, `push` or `clone`.
* The repository's root must be the working directory or under it. Every tool takes an optional `path`, the file or folder it targets, which also picks the repository.
* `gitlib_delete` starts off; switch it on in the Offered tab.
* Commits need *GitLib email* and *GitLib name*, written into the repository by `/gituser`.

| Tool | Arguments | What it does |
|---|---|---|
| `gitlib_status` | `path?` | The branch, ahead/behind its upstream, and every staged, modified, untracked or conflicted path. |
| `gitlib_log` | `path?, ref?, max_commits?` | Commits reachable from `ref` (HEAD by default), newest first; with a file, only those that changed it. |
| `gitlib_show` | `ref, path?` | One commit's author, date, message and changed files; with a file, its text at that commit; with a folder, its entries. |
| `gitlib_diff` | `path?, ref?, from?, to?, staged?, max_lines?` | A unified diff of unstaged or staged changes, one commit against its parent, or two commits. |
| `gitlib_blame` | `path, from_line?, to_line?, ref?` | Who last changed each line, and in which commit, a window at a time. |
| `gitlib_branch` | `action, name?, new_name?, start_point?, switch_to?, path?` | `list`, `create`, `switch` or `rename` branches. A switch never overwrites local changes. |
| `gitlib_stage` | `action, paths, path?` | `stage` or `unstage` paths, or `.` for everything under `path`. |
| `gitlib_commit` | `message, amend?, allow_empty?, path?` | Commits what is staged as `user.name` / `user.email`. |
| `gitlib_stash` | `action, message?, index?, include_untracked?, path?` | `push`, `pop`, `apply` or `list` stashes. |
| `gitlib_discard` | `paths?, ref?, path?` | Throws away uncommitted changes: the paths named go back to `ref`; with none, a hard reset (untracked files stay). |
| `gitlib_delete` | `kind, name?, index?, path?` | Removes a local `branch` (never the checked-out one), a `tag`, or a `stash` by index. |

## Obsidian

The vault tools work on the vault's files directly: no plugin, no network, and Obsidian needn't be running. Notes are found by name, `[[wikilink]]`, alias or path; inline tags and frontmatter properties both count. Dot-folders are ignored, line endings are kept, and only `vault_delete` uses `.trash`.

| Tool | Arguments | What it does |
|---|---|---|
| `vault_search` | `query, tag?, folder?, max_results?` | Every line holding the text (any case), as `path:line`, and every note whose name or alias holds it, optionally within a tag or folder. |
| `vault_list` | `what?, folder?, tag?, property?, value?, max_results?` | Notes by folder, tag or property (`property: status, value: draft`); `what: tags` / `properties` lists every tag or key with its count. |
| `vault_read` | `note, heading?, start_line?, max_lines?` | A note with its properties, one heading's section, or a window of lines. |
| `vault_links` | `note` | The note's outgoing links and embeds (resolved or *unresolved*) and every backlink with its line. |
| `vault_daily` | `date?, append?` | The daily note for a day (`today`, `yesterday`, `+3`, `2026-09-22`), per the vault's Daily notes settings, created from its template if missing; `append` adds to its end. |
| `vault_write` | `note, content, mode?, heading?` | Writes a note: `create` (a bare name goes where Obsidian puts new notes), `overwrite`, `append` or `prepend`, to the note or one heading's section. |
| `vault_properties` | `note, set?, remove?` | Lists, sets or removes properties in one write; only the named keys change. |
| `vault_move` | `note, to` | Renames or moves a note and rewrites every link to it. |
| `vault_delete` | `note` | Moves a note or attachment into the vault's `.trash` and lists notes still linking to it. Never a folder or anything in a dot-folder. Only while *Obsidian allow delete (.trash)* is on. |

## SQL

Read-only queries against SQL Server over named connections, with no ODBC driver (`Microsoft.Data.SqlClient`). Connections live in `sql.json`: the home folder's is read by every profile, and the profile's wins a name clash. Each connection reaches one database, its own: to work in another database on the same server, add a connection for it.

### Connection settings

* **`server`**: `host`, `host,port` or `host\instance`.
* **`database`** (required): the one database the connection reaches. See [The connection's database](#the-connections-database).
* **`auth`**: `sql` (a SQL login: `user` and `password`), `windows` (your account; on a Mac, your Kerberos ticket from `kinit`), or `runas` (another Windows account, `DOMAIN\name` or `name@domain`, plus `password`; like `runas /netonly`, it signs in to the server only as that account). `runas` needs Windows; a Mac's wizard and `sql.json` template leave it out.
* **`encrypt`**: `strict`, `mandatory` (default) or `optional`. **`trustServerCertificate`**: `true` accepts a self-signed certificate.
* **`connectTimeoutSeconds`**: 1–120 (default 15).
* **`access`**: `read` (default) or `readwrite`. Changes through `sql_execute` need `readwrite` **and** *SQL mode* `read-write`, both checked at every call.
* **`passwordStore`**: `file` (default; a password typed into the file is encrypted in place with DPAPI on the next read) or `credman` (Windows Credential Manager, `NeonSidekick/sql/<connection_name>`). On a Mac, `file` encrypts under a key kept in the macOS Keychain (`keychain:…`) and `credman` is a Keychain generic password under the same name (`security add-generic-password -s NeonSidekick/sql/<connection_name> -a <user> -w`).

### Managing connections

* **SQL add/edit connection** (`/tools` › Database › SQL) walks through a new connection one page per choice, access (`read`/`readwrite`) included. With connections saved, its first page lists them: **+ New connection**, or one to edit, opened prefilled on its summary (Enter on a row changes it; the stored password is kept unless you type another). The summary can **test** the draft (`SELECT @@VERSION`, nothing written): it also names the other databases the login could open, or says it opens none. It saves with the file's comments kept. ESC steps back.
* **SQL set password** updates a password.
* Or edit `sql.json` directly: `%USERPROFILE%\.neonsidekick\sql.json` (global; `~/.neonsidekick/sql.json` on a Mac) or `…/profiles/<profile>/sql.json` (comments and trailing commas allowed).

```json
{
  "connections": {
    // SQL Auth: password encrypted in place after the first read
    "adventureworks": {
      "server": "127.0.0.1,1433",
      "database": "AdventureWorks2022",
      "auth": "sql",
      "user": "reader",
      "password": "type-password-here-once",
      "encrypt": "mandatory",
      "trustServerCertificate": true,
      "description": "Sample sales database"
    },
    // Windows Auth: Current account, no password required
    "reports-me": {
      "server": "sqlhost01.example.com,1453",
      "database": "Reports",
      "auth": "windows",
      "encrypt": "mandatory"
    },
    // RunAs Auth: Alternate account with password in Windows Credential Manager
    "reports-admin": {
      "server": "sqlhost01.example.com,1453",
      "database": "Reports",
      "auth": "runas",
      "user": "CONTOSO\\svc-reader",
      "passwordStore": "credman",
      "encrypt": "mandatory"
    }
  }
}
```

### Safety

* SQL Server's own parser (ScriptDom) lets through only a single `SELECT` (or a `WITH` ending in one); batches, DDL, `EXEC`, `INTO`, `DELETE` and linked servers are refused before reaching the server.
* It runs in a read-only-intent transaction that is always rolled back. Still give the login read-only permissions.
* Values go in as `@name` parameters. Results come back as a Markdown table, floats at full precision; CLR types (`geography`, `hierarchyid`) need `.ToString()`.

### The connection's database

A connection reaches its own `database` and nothing else on the server:

* No tool takes a database. A call that still sends `database` is refused, and there's no tool that lists the server's databases.
* The connection opens its database by name. If the server opens any other (a login's default database, when the configured one is refused), nothing runs.
* A statement the model writes (in `sql_query`, or `sql_execute` whether it reads or changes) is refused before it reaches the server when it names another database: a three-part name (`payroll.dbo.salaries`, `master.sys.objects`), a function called as `db.schema.f()`, a four-part column, or `OBJECT_ID('payroll.dbo.t')`. A call such as `payroll.dbo.f()` beside a table alias `payroll` reads the same as an alias's column method (`a.Shape.STAsText()`), so then the tools read the server's database names first (kept a minute): an alias spelled like another database is refused, and the refusal says to rename it. Its own database written out (`Sales.dbo.Orders`) is fine. So is a catalog tool's `table` in it, while another database's (`sql_describe` of `payroll.dbo.t`) is refused.
* What describes the whole server rather than the database is refused too: `sys.databases`, `sys.master_files`, the logins, servers and credentials views, every `sys.server_*`, `sys.availability_*` and `sys.dm_*` view and function (other sessions' queries among them), the old compatibility views (`sysdatabases`, `sysprocesses`…), the `fn_` functions that read the server's audit, trace and log files, `DB_NAME`, `DB_ID`, `HAS_DBACCESS` and `DATABASEPROPERTYEX` given another database, `OBJECT_NAME` and `OBJECT_SCHEMA_NAME` given a database id, `SUSER_NAME`, `SUSER_SNAME`, `SUSER_ID`, `SUSER_SID`, `LOGINPROPERTY` and `HAS_PERMS_BY_NAME` given an argument, and `IS_SRVROLEMEMBER` naming a login. The database's own catalog (`sys.objects`, `sys.database_principals`, `INFORMATION_SCHEMA`) reads as before, and so do `DB_NAME()`, `DB_ID()`, `SUSER_SNAME()` without an argument and `DATABASEPROPERTYEX(DB_NAME(), …)`.

What a statement's text can't show stays the login's to guard: a view, synonym, procedure or function in the database that reads another database, or a name built inside a string. The wizard's test and `--sql-check` name the other user databases the login could open. The server's own master, tempdb and msdb are left out of that list, since every login can usually open them. For a lock the server keeps too, give the connection a login that opens only its database.

| Tool | Arguments | What it does |
|---|---|---|
| `sql_connections` | — | The named connections (server, database, sign-in, description). Touches no server. Every other tool takes one by name in `connection`, which may be left out only when there is one. |
| `sql_tables` | `connection?, schema?, pattern?` | Tables and views as `schema.name`, with kind, approximate rows and `MS_Description`. `pattern` is text in the name or a `LIKE` pattern (`%`, `_`, `*`). |
| `sql_columns` | `pattern, connection?, schema?` | Every column whose name matches (`EmailAddress`, `%CustomerID`): table, type, nullability, description. |
| `sql_describe` | `table, connection?` | One table or view in full: description, columns (type, nullability, identity, computed, default, key), foreign keys both ways, indexes, CHECK constraints and triggers. A bare name finds the one schema with it. |
| `sql_relationships` | `connection?, table?` | Foreign-key join paths as `from_table.from_column -> to_table.to_column`, all or touching a table. |
| `sql_indexes` | `connection?, table?, schema?, missing?` | The indexes of a table, schema or database: kind, key and included columns, filter, size, and seeks, scans, lookups and updates since restart (unread ones marked). `missing: true` adds the optimizer's suggestions. Usage needs `VIEW SERVER STATE`. |
| `sql_query` | `sql, connection?, params?, max_rows?` | One read-only `SELECT`. `params` is an object (`{"id": 43659}` for `@id`); `max_rows` is 1–100000 (*SQL max rows* by default). Cut at *SQL query result max chars*. |
| `sql_execute` | `sql, connection?, params?, max_rows?` | Only under *SQL mode* `read-write`, on a `readwrite` connection. One statement that may change the database, of a kind *SQL statements allowed* ticks. Answers with the rows changed and any rows the statement returned (an `OUTPUT` clause's, a procedure's). |

`--sql-check <connection>` proves the tools against a real server on the published exe (the sign-in, every type, the gate, the lock to the connection's database and the other databases the login could open, the rollback, a cancel and a timeout).

### Changes

With *SQL mode* set to `read-write`, the model gets `sql_execute` beside the seven reading tools, for the connections whose entry says `"access": "readwrite"`. Either key off and a connection only reads. It is never offered headless or in plan mode.

1. **The kinds.** *SQL statements allowed* decides which kinds of statement may run; a statement needs every kind it does, and the refusal names the kinds that are ticked.

   | Kind | Statements | Default |
   |---|---|---|
   | changing data | `INSERT`, `UPDATE`, `MERGE` (`OUTPUT` included) | ✓ |
   | deleting | `DELETE`, `TRUNCATE TABLE`, a `MERGE` that deletes |  |
   | creating | `CREATE TABLE`, `INDEX`, `VIEW`, `SEQUENCE`, `TYPE`, `SCHEMA`, `SYNONYM`; `SELECT … INTO` | ✓ |
   | changing structure | `ALTER TABLE`, `VIEW`, `SEQUENCE`, `SCHEMA`; `CREATE OR ALTER VIEW` (with creating) |  |
   | dropping | `DROP` of those, and of procedures, functions and triggers |  |
   | upkeep | `UPDATE STATISTICS`, `CREATE STATISTICS`, `ALTER INDEX` (rebuild, reorganize) |  |
   | procedures and triggers | `EXEC` of a procedure (`sp_rename` and a few system ones that act on the database's own objects), `INSERT … EXEC`, and `CREATE`/`ALTER` of a procedure, function or trigger: code whose effects can't be read from the statement, so it's off by default. T-SQL reads a batch's lone first word as `EXEC` of it, so a typo there is a procedure call |  |
   | reading | `SELECT`: never asks, and runs as `sql_query` does (read-only intent, rolled back) once its gate passes it too | ✓ |

2. **The gate.** One batch, one statement, parsed by ScriptDom as the reading gate is (a procedure's body is one statement; `GO` makes two). An allow-list of statement types: one it doesn't know is refused. Every data change inside counts (an `INSERT … SELECT FROM (MERGE … OUTPUT …)` deletes too). Always refused: `BEGIN TRAN`/`COMMIT`/`ROLLBACK`/`SAVE` and `BEGIN … END` blocks (each call is its own transaction), `SET` and `USE`, `GRANT`/`DENY`/`REVOKE`, `EXECUTE AS`, logins, users, roles, keys and certificates, databases, `DBCC`, `BACKUP`/`RESTORE`, `KILL`, `SHUTDOWN`, `RECONFIGURE`, `CHECKPOINT`, `BULK INSERT`, `WAITFOR`, dynamic SQL (`EXEC` of a string, `sp_executesql`, a procedure named by a variable), every other `xp_`/`sp_` procedure, `EXEC … AT` a linked server, and the reading gate's doors out (`OPENROWSET`, `OPENQUERY`, `OPENDATASOURCE`, `OPENXML`, four-part names; `NEXT VALUE FOR` is allowed here).
3. **Your allow.** Every change asks on a pane that names the connection and database and shows the statement (`v` shows the whole of it, its lines numbered): **Deny**, **Allow once**, or **Allow for this session** (that connection only, until `/new`, `/clear` or a profile switch). A statement that names another database (`payroll.dbo.salaries`) never gets this far: it's refused (see [The connection's database](#the-connections-database)).
4. **The run.** The connection asks for read-write intent (its own pool), and signs in as for a read (`runas` too). There's no transaction of the app's: SQL Server commits the one statement on its own, atomically (a procedure's own transactions are its own). A failed, timed-out or cancelled statement changes nothing.
5. **The log.** Every change is written to the log: the connection and database, the rows changed and the statement.
6. **The account.** It's still the real guard. Give a `readwrite` connection a login with only the permissions you want the model to use.

## Oracle

The SQL tools' twin for Oracle, through ODP.NET Core (fully managed; no Oracle Client needed), over `oracle.json` (home and profile files, as for SQL). Where SQL's connections each reach one database, an Oracle connection is held to one schema.

### Connection settings

* **`dataSource`**: EZConnect `host:port/service` (`localhost:1521/FREEPDB1`; port 1521 by default) or a whole `(DESCRIPTION=…)`.
* **`user`**: the database user. `SYS` (and any `AS SYSDBA` sign-in) is refused, since Oracle doesn't hold SYS to a read-only transaction.
* **`schema`**: the one schema the connection is held to (the user's own by default). See [The connection's schema (Oracle)](#the-connections-schema-oracle).
* **`connectTimeoutSeconds`**: 1–120 (default 15).
* **`access`**: `read` (default) or `readwrite`. Changes through `oracle_execute` need `readwrite` **and** *Oracle mode* `read-write`, both checked at every call.
* **`passwordStore`**: `file` or `credman` (`NeonSidekick/oracle/<connection_name>`), as for SQL.

### Managing connections

**Oracle add/edit connection** (`/tools` › Database › Oracle) walks through a new connection, or edits a saved one as SQL's does. Its test (nothing written) shows who it signed in as, the container and the version, names the other schemas the account could read (and any `SELECT ANY` privilege) or says it reads none, and warns when the account could change data: the tools only write through `oracle_execute` on a `readwrite` connection, and a read-only account is the real guard. It asks for the access too (`read` or `readwrite`). **Oracle set password** updates a password; or edit `oracle.json` directly.

```json
{
  "connections": {
    // Password encrypted in place after the first read
    "hr": {
      "dataSource": "localhost:1521/FREEPDB1",
      "user": "hr_reader",
      "password": "type-password-here-once",
      "schema": "HR",
      "description": "The sample human-resources schema"
    },
    // A full descriptor, the password in the system's credential store (credman)
    "ledger": {
      "dataSource": "(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=dbhost01.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=LEDGER)))",
      "user": "ledger_ro",
      "passwordStore": "credman"
    }
  }
}
```

### Safety

`oracle_query` runs one read-only statement behind four layers:

1. **The gate.** The text is lexed (comments, literals, quoted names and binds understood) and only one `SELECT` or `WITH … SELECT` passes. Refused: a second statement, PL/SQL, `FOR UPDATE`, `INTO`, `NEXTVAL`, database links, external tables and `BFILENAME`, DML and DDL words, and packages that reach outside (`UTL_HTTP`, `UTL_FILE`, `DBMS_SQL`…).
2. **The session.** On 23ai and later, `ALTER SESSION SET READ_ONLY = TRUE`: the server refuses any write.
3. **The transaction.** `SET TRANSACTION READ ONLY`, always rolled back.
4. **The account.** Give the user only `SELECT` grants (or `ALTER USER … READ ONLY` on 23ai).

Values go in as `:name` parameters. A `NUMBER` past 28 digits keeps every digit; a CLOB or BLOB shows its start and length; object types (`SDO_GEOMETRY`, `XMLTYPE`) need converting to text.

### The connection's schema (Oracle)

An Oracle connection is held to its `schema`, or to the user's own when it names none:

* Every call works in it (`CURRENT_SCHEMA`), and every listing keeps to it. No tool takes a schema, and there's no tool that lists them. A call that still sends `schema` is refused, and so is a catalog tool's `table` in another schema (`HR.EMPLOYEES`).
* A qualifier naming another schema is refused in the model's SQL. A lexer can't tell `hr.employees` from an alias's `e.id`, so when a statement qualifies a name with anything but its own schema, the tools first read the database's names (kept a minute): the users (every schema is a user, `SYS` and Oracle's own among them), and refuse a qualifier among them. An alias spelled like a user's name is refused too (rename it). Names compare as Oracle reads them: a bare word in upper case, a quoted one as written. A sequence's `s.NEXTVAL`, a trigger's `:NEW.x` and a package call such as `DBMS_LOB.GETLENGTH` aren't qualifiers.
* The dictionary's views of the whole database are refused: `ALL_*`, `DBA_*` and `CDB_*` (every schema's objects) and `V$`/`GV$` (the instance and its sessions). `USER_*` stays. A word with one of those prefixes is refused only when the database has such a view (`SYS`'s public synonyms, read with the users), so a column such as `DBA_NAME` stands. A table of the schema's own named like a real view is written with the schema, `HR.ALL_USERS`.

An unqualified name still resolves through public synonyms, and what a synonym, view or routine reaches (another schema's data) stays the account's to guard. The wizard's test and `--oracle-check` name the other schemas whose tables the account can read, and any `SELECT ANY` privilege. Oracle's own schemas are left out, since the dictionary is every account's. An account that reads only its schema is the lock the database keeps too.

| Tool | Arguments | What it does |
|---|---|---|
| `oracle_connections` | — | The named connections (data source, user, schema, description). Touches no server. Every other tool takes one by name in `connection`, which may be left out only when there is one. |
| `oracle_tables` | `connection?, pattern?` | Tables and views as `SCHEMA.NAME`, with kind, the optimizer's row count and comment. `pattern` is text in the name (any case) or a `LIKE` pattern with `%` or `*`. |
| `oracle_columns` | `pattern, connection?` | Every column whose name matches: table, type, nullability, comment. |
| `oracle_describe` | `table, connection?` | One table or view in full: comment, columns (type, nullability, identity, virtual, default, key, comment), foreign keys both ways, indexes, CHECK constraints and triggers. |
| `oracle_relationships` | `connection?, table?` | Foreign-key join paths: the schema's or a table's. |
| `oracle_indexes` | `connection?, table?` | Indexes: kind, key columns, status, visibility, the optimizer's counts, and recorded use where `DBA_INDEX_USAGE` is readable. |
| `oracle_query` | `sql, connection?, params?, max_rows?` | One read-only `SELECT` (`FETCH FIRST n ROWS ONLY`, no trailing `;`). `params` as for SQL (`:id`); `max_rows` 1–100000. Cut at *SQL query result max chars*. |
| `oracle_execute` | `sql, connection?, params?, max_rows?` | Only under *Oracle mode* `read-write`, on a `readwrite` connection. One statement that may change the schema, of a kind *Oracle statements allowed* ticks. A PL/SQL block or unit counts as one statement. Answers with the rows changed (`RETURNING … INTO` is refused: select the rows after the change). |

`--oracle-check <connection>` proves the tools against a real database on the published exe (every type, the read-only layers, the lock to its schema and what else the account could read, a cancel and a timeout).

### Changes

With *Oracle mode* set to `read-write`, the model gets `oracle_execute` beside the seven reading tools, for the connections whose entry says `"access": "readwrite"`. Either key off and a connection only reads. It is never offered headless or in plan mode.

1. **The kinds.** *Oracle statements allowed* decides which kinds of statement may run; a statement needs every kind it does, and the refusal names the kinds that are ticked.

   | Kind | Statements | Default |
   |---|---|---|
   | changing data | `INSERT` (and `INSERT ALL`), `UPDATE`, `MERGE` | ✓ |
   | deleting | `DELETE`, `TRUNCATE`, a `MERGE` that deletes |  |
   | creating | `CREATE TABLE`, `INDEX`, `VIEW`, `MATERIALIZED VIEW`, `SEQUENCE`, `SYNONYM` | ✓ |
   | changing structure | `ALTER` of those, `RENAME`, `COMMENT ON`; `CREATE OR REPLACE VIEW`, `SYNONYM` (with creating) |  |
   | dropping | `DROP` of those, and of procedures, functions, packages, triggers and types |  |
   | upkeep | `ANALYZE` (`DBMS_STATS` is a package call: procedures) |  |
   | procedures and triggers | `CALL`, an anonymous `BEGIN … END;` or `DECLARE` block, and `CREATE`/`ALTER` of a procedure, function, package, trigger or type: code whose effects can't be read from the statement, so it's off by default |  |
   | reading | `SELECT`: never asks, and runs as `oracle_query` does (read-only, rolled back) once its gate passes it too; a table made a moment ago is read again after 3 s (ORA-01466) | ✓ |

2. **The gate.** One statement per call, lexed by the same rules as the reading gate. A PL/SQL unit (an anonymous block, or a `CREATE` of a procedure, function, package, trigger or type) is one whatever `;`s its body holds and keeps its final `;`; a trailing `/` is dropped, and a `/` with more text after it makes a second statement. An allow-list: a statement it doesn't know is refused. Always refused: `COMMIT`/`ROLLBACK`/`SAVEPOINT` (each call is its own transaction), `SET`, `ALTER SESSION`/`SYSTEM`, `GRANT`/`REVOKE`/`AUDIT`/`ADMINISTER`, users, roles, profiles, anything `PUBLIC`, tablespaces, directories, databases and database links, libraries and Java, `LOCK TABLE`, `PURGE`, `FLASHBACK`, and anywhere (bodies included) `@dblink`, the denied packages (`UTL_FILE`, `UTL_HTTP`, `DBMS_SQL`, `DBMS_SCHEDULER`…), `EXECUTE IMMEDIATE`, `WITH FUNCTION`, `BFILENAME` and `EXTERNAL(`; outside PL/SQL, `RETURNING … INTO`. `NEXTVAL` is allowed here. `SYS` stays refused.
3. **Your allow.** Every change asks on the pane, as for [SQL](#changes); *Allow for this session* covers that connection only. A statement that reaches another schema never gets this far: it's refused (see [The connection's schema (Oracle)](#the-connections-schema-oracle)).
4. **The run.** A session of its own (no pooling, so never one a read left `READ_ONLY`), `CURRENT_SCHEMA` set, and no transaction of the app's: the statement commits as it runs (DDL commits anyway). A failed, timed-out or cancelled statement changes nothing.
5. **The log.** Every change is written to the log: the connection and schema, the rows changed and the statement.
6. **The account.** It's still the real guard. Give a `readwrite` connection an account with only the privileges you want the model to use.

## MySQL and MariaDB

The same tools for MySQL 8.0.16+ and MariaDB 10.2+, through MySqlConnector (fully managed, MIT), over `mysql.json` (home and profile files, as for SQL). As for SQL, each connection reaches one database, its own.

### Connection settings

* **`host`**, **`port`** (3306 by default), **`database`** (required: the one database the connection reaches).
* **`user`**, and **`passwordStore`** `file` or `credman` (`NeonSidekick/mysql/<connection_name>`), as for SQL.
* **`sslMode`**: `preferred` (default), `required`, `verify-ca`, `verify-full` or `none`.
* **`allowPublicKeyRetrieval`**: `true` only for a `caching_sha2_password` account without TLS (off by default; a man in the middle could supply its own key).
* **`connectTimeoutSeconds`**: 1–120 (default 15).
* **`access`**: `read` (default) or `readwrite`. Changes through `mysql_execute` need `readwrite` **and** *MySQL mode* `read-write`, both checked at every call.

```json
{
  "connections": {
    "shop": {
      "host": "localhost",
      "port": 3306,
      "database": "shop",
      "user": "shop_reader",
      "password": "type-password-here-once",
      "description": "The sample retail database"
    },
    "billing": {
      "host": "db01.example.com",
      "database": "billing",
      "user": "billing_ro",
      "passwordStore": "credman",
      "sslMode": "verify-full"
    }
  }
}
```

**MySQL add/edit connection** (`/tools` › Database › MySQL) walks through a new one (or edits a saved one, as SQL's does) and can **test** it: who it signs in as, the version, a warning when `SHOW GRANTS` allows changes, and the other databases the account could reach (or that it sees none). It asks for the access too (`read` or `readwrite`). **MySQL set password** updates a password.

### Safety

1. **The gate.** The text is lexed by MySQL's rules and only one `SELECT` or `WITH … SELECT` passes. Refused: a second statement, executable comments (`/*! … */`), `INTO`, locking reads, `LOAD_FILE`, named locks, MariaDB sequence moves, and DML and DDL words.
2. **The session.** `sql_mode` drops `NO_BACKSLASH_ESCAPES` and `ANSI_QUOTES`, so the server reads strings as the gate did, and the server caps each statement's run time. The driver never runs `LOAD DATA LOCAL`.
3. **The transaction.** `START TRANSACTION READ ONLY`, always rolled back.
4. **The account.** Give the user `SELECT` grants alone.

### The connection's database (MySQL)

A MySQL connection reaches its own `database` and nothing else on the server, as SQL's does:

* No tool takes a database, and there's no tool that lists the server's databases. A call that still sends `database` is refused, and so is a catalog tool's `table` in another database (`payroll.salaries`).
* The session stays in its database (`USE` is refused by both gates). If it's in any other, nothing runs.
* A MySQL database is reached by qualifying a name (`payroll.salaries`, `` `payroll`.salaries ``, `payroll.f()`), and a lexer can't tell that from a table's alias (`t.col`). So when a statement qualifies a name with anything but the connection's own database, the tools first read the names of the databases the account can see, and refuse a qualifier among them. An alias spelled like another database's name is refused too; rename it. The names are kept a minute per connection. A statement that qualifies nothing asks the server nothing more. On a server whose names are case-sensitive, a database that differs from the connection's own only in case (`Shop` beside `shop`) is another one and is refused, even written as a bare word; the own database in backticks, spelled exactly, is always its own.
* The server's own databases are refused as a qualifier whatever the account sees: `information_schema` (every database's tables), `performance_schema` (other sessions' statements), `mysql` (the accounts) and `sys`. The catalog tools read the connection's own database instead.

What the text can't show (a view or routine in the database that reads another) stays the account's to guard. The wizard's test and `--mysql-check` name the other databases the account could reach. An account granted only its database is the lock the server keeps too.

| Tool | Arguments | What it does |
|---|---|---|
| `mysql_connections` | — | The named connections (host, database, user, description). Touches no server. Every other tool takes one by name in `connection`, which may be left out only when there is one. |
| `mysql_tables` | `connection?, pattern?` | Tables and views as `database.name`, with kind, approximate rows and comment. |
| `mysql_columns` | `pattern, connection?` | Every column whose name matches: table, type, nullability, comment. |
| `mysql_describe` | `table, connection?` | One table or view: comment, columns (type, nullability, auto_increment, default, key, comment), foreign keys both ways, indexes, CHECK constraints and triggers. |
| `mysql_relationships` | `connection?, table?` | Foreign-key join paths: the database's or a table's. |
| `mysql_indexes` | `connection?, table?` | Indexes: kind, key columns, cardinality, and reads and writes since restart where `performance_schema` allows. |
| `mysql_query` | `sql, connection?, params?, max_rows?` | One read-only `SELECT` (`LIMIT n`). `params` as for SQL (`@id`); `max_rows` 1–100000. Cut at *SQL query result max chars*. |
| `mysql_execute` | `sql, connection?, params?, max_rows?` | Only under *MySQL mode* `read-write`, on a `readwrite` connection. One statement that may change the database, of a kind *MySQL statements allowed* ticks. Answers with the rows changed and any rows the statement returned (MariaDB's `RETURNING`, `ANALYZE TABLE`'s report). |

`--mysql-check <connection>` proves the tools against a real server on the published exe (every type, the gate, the session, the lock to the connection's database and the other databases the account could reach, the transaction, a cancel and a timeout).

### Changes

With *MySQL mode* set to `read-write`, the model gets `mysql_execute` beside the seven reading tools, for the connections whose entry says `"access": "readwrite"`. Either key off and a connection only reads. It is never offered headless or in plan mode.

1. **The kinds.** *MySQL statements allowed* decides which kinds of statement may run; a statement needs every kind it does, and the refusal names the kinds that are ticked.

   | Kind | Statements | Default |
   |---|---|---|
   | changing data | `INSERT`, `UPDATE`, `REPLACE` (upserts included) | ✓ |
   | deleting | `DELETE`, `TRUNCATE` |  |
   | creating | `CREATE TABLE`, `INDEX`, `VIEW`, `SEQUENCE` (MariaDB) | ✓ |
   | changing structure | `ALTER TABLE`, `VIEW`, `SEQUENCE`; `RENAME TABLE`; `CREATE OR REPLACE VIEW`, `INDEX` (with creating) |  |
   | dropping | `DROP` of those, and of procedures, functions and triggers; MariaDB's `CREATE OR REPLACE TABLE`, `SEQUENCE` (with creating) |  |
   | upkeep | `ANALYZE`, `OPTIMIZE`, `CHECK`, `REPAIR`, `CHECKSUM TABLE` |  |
   | procedures and triggers | `CALL`, and `CREATE`/`ALTER` of a procedure, function or trigger: code whose effects can't be read from the statement, so it's off by default |  |
   | reading | `SELECT`: never asks, and runs as `mysql_query` does (read-only, rolled back) once its gate passes it too | ✓ |

2. **The gate.** One statement per call, lexed by the same rules as the reading gate (executable comments still refused). A `CREATE PROCEDURE`, `FUNCTION` or `TRIGGER` body between `BEGIN` and its `END` may hold its own `;`s; a body with a column, parameter or label named `begin` or `end` is refused until the name is in backticks. An allow-list: a statement it doesn't know is refused. Always refused: `START TRANSACTION`/`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVEPOINT`/`XA` (each call is its own transaction), `GRANT`/`REVOKE`, users and roles, `DEFINER =`, `SET`/`USE`, `PREPARE`/`EXECUTE`, `LOAD DATA`, `INTO OUTFILE`/`DUMPFILE`, `HANDLER`, `DO`, `LOCK`/`UNLOCK`, `FLUSH`, `KILL`, `SHUTDOWN`, `RESET`, `PURGE`, `INSTALL`, `CREATE`/`DROP` of a database, server, tablespace or event, and the reading gate's denied functions (MariaDB's `NEXTVAL`/`SETVAL` are allowed here).
3. **Your allow.** Every change asks on the pane, as for [SQL](#changes); *Allow for this session* covers that connection only. A statement that reaches another database never gets this far: it's refused (see [The connection's database (MySQL)](#the-connections-database-mysql)).
4. **The run.** The session starts as for a read (`sql_mode` without `NO_BACKSLASH_ESCAPES`/`ANSI_QUOTES`, the statement cap) but with no transaction of the app's: the statement commits as it runs (autocommit; DDL commits anyway). MariaDB caps every statement's run time; MySQL caps a `SELECT` only, and the query timeout stops the rest. A failed, timed-out or cancelled statement changes nothing.
5. **The log.** Every change is written to the log: the connection and database, the rows changed and the statement.
6. **The account.** It's still the real guard. Give a `readwrite` connection an account with only the grants you want the model to use.

## SQLite

SQLite database files, through the same Microsoft.Data.Sqlite the sessions use: no server, no password, nothing new to install. A database is a name from `sqlite.json` (home and profile files, as for SQL; a relative `path` is taken from the file's folder) or, with *SQLite sandbox files* on, any file in the working directory by its path.

```json
{
  "databases": {
    "chinook": { "path": "D:\\data\\chinook.db", "description": "The music store sample" }
  }
}
```

**SQLite add/edit database** (`/tools` › Database › SQLite) walks through a new one (or edits a saved one, as SQL's does) and can **test** it: it opens the file read-only and counts its tables.

### Safety

The four reading tools stay read-only whatever *SQLite mode* says:

1. **The gate.** The text is lexed by SQLite's rules and only one `SELECT`, `WITH … SELECT` or `VALUES` passes. Refused: a second statement, DML and DDL words anywhere (a `WITH` can lead an `INSERT`), `REPLACE INTO`, `ATTACH`/`DETACH`, `PRAGMA`, transaction words, `load_extension()` and its kin (by any name, quoted or not) and positional `?` placeholders.
2. **The file.** Opened read-only, without pooling.
3. **The session.** `PRAGMA query_only = ON`.
4. **The transaction.** Always rolled back. A statement past the timeout (or ESC) is interrupted.

| Tool | Arguments | What it does |
|---|---|---|
| `sqlite_databases` | — | The named databases (file, description), and whether working-directory files may be named. Opens nothing. Every other tool takes one in `database` (a name, or a working-directory file's path), which may be left out only when one is named. |
| `sqlite_tables` | `database?, pattern?` | Tables and views with their kind. |
| `sqlite_describe` | `table, database?` | One table or view: columns (type, nullability, primary key, default; generated and hidden columns marked), foreign keys both ways, indexes (their columns in order, an expression shown as `(expression)`) and the `CREATE` statement. |
| `sqlite_query` | `sql, database?, params?, max_rows?` | One read-only `SELECT` (`LIMIT n`). `params` binds `@name`, `:name`, `$name` or `#name`, keyed by the name with or without its mark (`{"id": 5}` or `{":id": 5}`); a TCL form is given whole (`{"a(1)": 5}`); an unbound one is NULL; `max_rows` 1–100000. Cut at *SQL query result max chars*. |
| `sqlite_execute` | `sql, database?, params?, max_rows?, create?` | Only under *SQLite mode* `read-write`. One statement that may change the database, of a kind *SQLite statements allowed* ticks (`RETURNING` allowed). `create: true` (with creating ticked) makes a new database file at `database`, a path in the working directory ending `.db`, `.sqlite`, `.sqlite3` or `.db3`. Answers with the rows changed and any rows the statement returned. |

`--sqlite-check <database>` proves the tools against a real file on the published exe (it opens and counts, every storage class, the gate, a write refused, the interrupt).

### Changes

With *SQLite mode* set to `read-write`, the model gets `sqlite_execute` beside the four reading tools. It is never offered headless or in plan mode.

1. **The kinds.** *SQLite statements allowed* decides which kinds of statement may run; a statement of a kind left unticked is refused, and the refusal names the kinds that are ticked. A `WITH` counts as the statement after its common table expressions.

   | Kind | Statements | Default |
   |---|---|---|
   | changing data | `INSERT`, `UPDATE`, `REPLACE` (upserts, `OR …`, `RETURNING` included) | ✓ |
   | deleting | `DELETE` (`RETURNING` included) | |
   | creating | `CREATE TABLE`, `INDEX`, `VIEW`, `TRIGGER` (and the kinds its body's changes need: a trigger that deletes needs deleting), `VIRTUAL TABLE`; also needed for `create` | ✓ |
   | changing structure | `ALTER TABLE` | |
   | dropping | `DROP TABLE`, `INDEX`, `VIEW`, `TRIGGER` | |
   | upkeep | `VACUUM`, `REINDEX`, `ANALYZE` | |
   | settings | `PRAGMA` | |
   | reading | `SELECT`, `VALUES`, `EXPLAIN`: never asks, and runs as `sqlite_query` does (the file opened read-only, `query_only`, rolled back) | ✓ |

2. **The gate.** One statement per call, lexed by the same rules as the reading gate. A `CREATE TRIGGER` body may hold its own `;`s (it ends at the first `END` after a `;`, as SQLite reads it). Still refused: `ATTACH`/`DETACH` and `VACUUM INTO` (they reach another file), `BEGIN`/`COMMIT`/`ROLLBACK`/`SAVEPOINT`/`RELEASE` as the statement (each call is its own transaction), `writable_schema` and `sqlite_dbpage` (they can corrupt the file), `load_extension()` and its kin, and positional `?` placeholders.
3. **Your allow.** Every change asks on a pane that names the file and shows the statement (`v` shows the whole of it, its lines numbered): **Deny**, **Allow once**, or **Allow for this session** (that file only, until `/new`, `/clear` or a profile switch).
4. **The run.** The file is opened read-write with no transaction of the app's, so the statement commits as it runs, atomically. A failed or interrupted statement changes nothing. The timeout and ESC interrupt it as they do a query.
5. **The log.** Every change is written to the log: the database, its file, the rows changed and the statement.

**Shell and files.** While *SQLite tools* is on, the rest of the app keeps out of the databases, in either mode:
* The shell police's SQLite rule (Shell guards) refuses a command, script or process input that reaches SQLite, so `sqlite_execute` and its Allow pane can't be walked around by a script. It's a tripwire that reads text, not a sandbox; only Shell command policy `ask` shows you every command.
* The file tools never change a database file: a `.db`/`.db3`/`.sqlite`/`.sqlite3` file (or its journal), or one *sqlite.json* names. That covers `write_file`, `patch_file`, `move`, `copy` onto one, `delete` (a folder holding one too), `unzip` into one, `download_file` and `unc_fetch`. Reading one and copying from one still work.

**Creating a database.** `create: true` needs creating ticked in *SQLite statements allowed* and *SQLite sandbox files* on. The new file must end `.db`, `.sqlite`, `.sqlite3` or `.db3`, and its folder must already exist. A file already there is just opened, and a name from `sqlite.json` is never made. If the statement fails, the new file is removed again. To make an empty database, create it with a first table, or with `PRAGMA user_version = 0`.

## PostgreSQL

The same tools for PostgreSQL, through Npgsql (fully managed, PostgreSQL licence, built slim for NativeAOT), over `postgres.json` (home and profile files, as for SQL). As for SQL, each connection reaches one database, its own, and it can be held to one schema too.

### Connection settings

* **`host`**, **`port`** (5432 by default), **`database`** (required: the one database the connection reaches).
* **`schema`** (optional): hold the connection to that one schema. Without it, every schema of the database stays open. See [The connection's database (PostgreSQL)](#the-connections-database-postgresql).
* **`user`**, and **`passwordStore`** `file` or `credman` (`NeonSidekick/postgres/<connection_name>`), as for SQL.
* **`sslMode`**: `prefer` (default), `require`, `verify-ca`, `verify-full` or `disable`.
* **`connectTimeoutSeconds`**: 1–120 (default 15).
* **`access`**: `read` (default) or `readwrite`. Changes through `postgres_execute` need `readwrite` **and** *PostgreSQL mode* `read-write`, both checked at every call.

```json
{
  "connections": {
    "shop": { "host": "localhost", "database": "shop", "user": "shop_reader", "password": "type-password-here-once", "description": "The sample retail database" },
    "billing": { "host": "db01.example.com", "database": "billing", "schema": "invoices", "user": "billing_ro", "passwordStore": "credman", "sslMode": "verify-full" }
  }
}
```

**PostgreSQL add/edit connection** (`/tools` › Database › PostgreSQL) walks through a new one (or edits a saved one, as SQL's does) and can **test** it: who it signs in as, the version, a warning when the role is a superuser or holds write grants, and what else the role could reach (other databases, foreign servers, and other schemas under a schema lock), or that it reaches nothing more. It asks for the schema and the access too (`read` or `readwrite`). **PostgreSQL set password** updates a password.

### Safety

1. **The gate.** The text is lexed by PostgreSQL's rules (nested comments, `E''` strings, dollar quoting) and only one `SELECT`, `WITH`, `VALUES` or `TABLE` passes. Refused: a second statement, DML and DDL words anywhere (a `WITH` can lead a `DELETE`), `SELECT INTO`, locking reads, `COPY`, `DO`, `U&` escapes, positional `$1`, and the functions that reach files or directories, large objects, sequences, locks, settings, other backends or other databases, the ones that run a query handed to them as text (`query_to_xml`, `ts_stat`, `crosstab`…), and the admin ones a rollback does not undo (replication slots, statistics resets, backups) (by any name, quoted or not).
2. **The session.** Every transaction read-only by default, `standard_conforming_strings` on (so the server reads strings as the gate does), and `statement_timeout` and `lock_timeout` set, at connection startup.
3. **The transaction.** `SET TRANSACTION READ ONLY`, always rolled back: Postgres refuses every write, `nextval` and a temporary table.
4. **The account.** Give the role `SELECT` grants alone.

### The connection's database (PostgreSQL)

A PostgreSQL session can't leave its database: a three-part name in another database is the server's own error, and `dblink` is refused by the gates. On top of that:

* No tool takes a database, and there's no tool that lists the server's databases. A call that still sends `database` is refused.
* What describes the whole server rather than the database is refused in the model's own SQL, locked or not: `pg_database`, `pg_roles`, `pg_user`, `pg_authid`, `pg_shadow`, `pg_auth_members`, `pg_settings`, `pg_hba_file_rules`, `pg_tablespace`, `pg_stat_activity` (other sessions' queries), `pg_stat_database`, `pg_stat_replication`, `pg_replication_slots`, `pg_user_mappings` (their passwords), `pg_subscription` and the shared catalogs, and `pg_database_size`, `has_database_privilege`, `pg_stat_get_activity` and `pg_stat_get_backend_*`. The database's own catalog (`pg_class`, `pg_namespace`…) reads as before.
* **A schema lock** (`"schema"` in the entry): the session's `search_path` is that schema alone, so an unqualified name finds only its objects (and `pg_catalog`'s). The listings keep to it, `postgres_schemas` lists only it, and a tool's `schema` (or a `table` written as `schema.name`) naming another is refused. In the model's SQL, a qualifier naming another schema is refused, including the schema in the middle of a three-part name (`shop.payroll.salaries`). A lexer can't tell `sales.orders` from an alias's `o.id`, so the tools first read the schemas' names (kept a minute) when a statement qualifies a name with anything but its own schema or `pg_catalog`; an alias spelled like a schema's name is refused too (rename it). Names compare as PostgreSQL reads them: a bare word in lower case, a quoted one as written, so `"Sales"` is not `sales`. `information_schema` is refused at once, since it lists every schema's tables, and so are the XML exports that read a table, schema or database named in a string (`table_to_xml`, `schema_to_xml`, `database_to_xml` and their `xmlschema` forms).

What the text can't show stays the role's to guard: a view, function or foreign table in the database that reads another schema, database or server. The wizard's test and `--postgres-check` name the other databases the role may connect to (PostgreSQL lets every role connect to a new database unless `CONNECT` is revoked from `PUBLIC`), the foreign servers it may use and, under a schema lock, the other schemas it may use. The system catalogs are every role's and aren't counted.

| Tool | Arguments | What it does |
|---|---|---|
| `postgres_connections` | — | The named connections (host, database, schema lock, user, description). Touches no server. Every other tool takes one by name in `connection`, which may be left out only when there is one. |
| `postgres_schemas` | `connection?` | The schemas the account may use (the locked one alone, under a lock), with table counts and owners. |
| `postgres_tables` | `connection?, schema?, pattern?` | Tables and views as `schema.name`, with kind, approximate rows and comment. |
| `postgres_columns` | `pattern, connection?, schema?` | Every column whose name matches: table, type, nullability, comment. |
| `postgres_describe` | `table, connection?, schema?` | One table or view: comment, columns (type, nullability, default, primary key, comment), foreign keys both ways, indexes and CHECK constraints. |
| `postgres_relationships` | `connection?, schema?, table?` | Foreign keys: a schema's, or a table's either way. |
| `postgres_indexes` | `connection?, schema?, table?` | Indexes: unique, primary, the definition, scans since the statistics reset. |
| `postgres_query` | `sql, connection?, params?, max_rows?` | One read-only `SELECT` (`LIMIT n`). `params` binds `@name` (straight after an operator, as in `id=@id`, only a name `params` gives: `<@tags` stays the operator and a column, and an unbound `id=@id` fails with a hint to pass `id`); `max_rows` 1–100000. Cut at *SQL query result max chars*. |
| `postgres_execute` | `sql, connection?, params?, max_rows?` | Only under *PostgreSQL mode* `read-write`, on a `readwrite` connection. One statement that may change the database, of a kind *PostgreSQL statements allowed* ticks (`RETURNING` allowed). Answers with the rows changed and any rows the statement returned. |

`--postgres-check <connection>` proves the tools against a real server on the published exe (who it is, every type, the gate, the lock to its database and schema and what else the role could reach, a write refused, the timeout).

### Changes

With *PostgreSQL mode* set to `read-write`, the model gets `postgres_execute` beside the eight reading tools, for the connections whose entry says `"access": "readwrite"`. Either key off and a connection only reads. It is never offered headless or in plan mode.

1. **The kinds.** *PostgreSQL statements allowed* decides which kinds of statement may run; a statement needs every kind it does (a `WITH` that deletes and inserts needs deleting and changing data), and the refusal names the kinds that are ticked.

   | Kind | Statements | Default |
   |---|---|---|
   | changing data | `INSERT`, `UPDATE`, `MERGE` (upserts and `RETURNING` included) | ✓ |
   | deleting | `DELETE`, `TRUNCATE`, a `MERGE` that deletes | |
   | creating | `CREATE TABLE`, `INDEX`, `VIEW`, `MATERIALIZED VIEW`, `SEQUENCE`, `TYPE`, `DOMAIN`, `SCHEMA` | ✓ |
   | changing structure | `ALTER` of those, `COMMENT ON`; `CREATE OR REPLACE VIEW` (with creating) | |
   | dropping | `DROP` of those, and of functions, procedures and triggers | |
   | upkeep | `VACUUM`, `ANALYZE`, `REINDEX`, `CLUSTER`, `REFRESH MATERIALIZED VIEW` | |
   | procedures and triggers | `CALL`, `DO`, and `CREATE`/`ALTER` of a function, procedure, trigger or rule: code whose effects can't be read from the statement, so it's off by default | |
   | reading | `SELECT`, `VALUES`, `TABLE`: never asks, and runs as `postgres_query` does (read-only, rolled back) once its gate passes it too | ✓ |

2. **The gate.** One statement per call, lexed by the same rules as the reading gate (a function's or `DO` block's body is dollar-quoted, so its `;`s don't count). An allow-list: a statement it doesn't know is refused. Always refused: `BEGIN`/`COMMIT`/`ROLLBACK`/`SAVEPOINT` (each call is its own transaction), `GRANT`/`REVOKE`, roles, users, policies and `OWNER TO`, `SET`/`RESET`/`DISCARD`, `PREPARE`/`EXECUTE`, `COPY`, `LOAD`, `LOCK`, `LISTEN`/`NOTIFY`, `CHECKPOINT`, `CREATE`/`DROP` of a database, tablespace, extension, server, foreign table or language, `ALTER SYSTEM`, and the reading gate's denied functions (`nextval`/`setval` are allowed here).
3. **Your allow.** Every change asks on the pane, as for [SQL](#changes); *Allow for this session* covers that connection only. A statement that reaches past the connection's database or schema lock never gets this far: it's refused (see [The connection's database (PostgreSQL)](#the-connections-database-postgresql)).
4. **The run.** The session starts as for a read but without the read-only default; `statement_timeout`, `lock_timeout` and `standard_conforming_strings` stay. There's no transaction of the app's, so the statement commits as it runs, atomically, and `VACUUM` can run. A failed, timed-out or cancelled statement changes nothing.
5. **The log.** Every change is written to the log: the connection and database, the rows changed and the statement.
6. **The account.** It's still the real guard: give a `readwrite` connection a role with only the grants you want the model to use.

## UNC shares and outside folders

**Windows only:** on a Mac the UNC group is never offered, and `/tools` shows it as *off: it needs Windows*.

The UNC tools reach named network shares (`\\server\share`, or a folder under one) and local folders outside the working directory (`D:\Data`), without mapped drives, as you (`windows`) or as another Windows account (`runas`, like `runas /netonly`). Shares are read-only unless two keys say otherwise. They live in `unc.json` (home and profile files, as for SQL).

### Share settings

* **`path`** (required): `\\server\share`, a folder under it, or a full local path. In JSON, double the backslashes (`"\\\\fs01\\eng"`) or use `/` (`"//fs01/eng"`). Device paths (`\\?\…`), relative paths and whole drives (`D:\`) are refused.
* **`auth`**: `windows` (default) or `runas`, with **`user`** (`DOMAIN\name` or `name@domain`) and **`passwordStore`** `file` or `credman` (`NeonSidekick/unc/<share_name>`).
* **`access`**: `read` (default) or `readwrite`. Changes need `readwrite` **and** *UNC writes* on, both checked at every call.
* **`description`**: what the share holds; the model reads it to choose.

```json
{
  "shares": {
    "eng": { "path": "//fs01/eng", "description": "Engineering specs and drawings" },
    "finance": {
      "path": "//fs02.corp.local/finance/reports",
      "auth": "runas",
      "user": "CORP\\svc_reader",
      "passwordStore": "credman"
    },
    "data": { "path": "D:/Data", "access": "readwrite" }
  }
}
```

**UNC add/edit share** (the UNC tab of `/tools`) walks through a new one (or edits a saved one, as SQL's does) and can **test** it by listing its root under its account. **UNC set password** updates a runas password.

### How it signs in

* A `runas` share gets its own logon session for each call, so it never collides with your mapped drives and leaves nothing in `net use`. A wrong password shows only when the server is reached ("unknown account or wrong password").
* A `windows` share uses whatever your sign-in has for that server (a mapped drive's credentials, or a `cmdkey /add:server` entry).
* Use UNC paths, not mapped drive letters (a runas token or an elevated app may not see the mapping), and server names, not IP addresses (an address falls back from Kerberos to NTLM).
* `runas` against `\\localhost` proves nothing: Windows signs a loopback session in as you.

### Safety

* **Paths** are relative to the share's root (or full paths under it), never above it. Streams (`file.txt:secret`) are refused, and walks don't follow reparse points, so add a DFS link's target as its own share.
* **Changes are permanent:** an overwrite replaces the file in place (keeping its permissions), and `unc_delete` deletes for good. `unc_delete` starts off even under *UNC writes*. Every change is logged with the share, the account and the path.
* **Budgets:** a search reads at most 256 MB with four readers and 100,000 entries, then says it stopped. A share that doesn't answer in 10 seconds is given up on.
* The shell can't use a runas share's sign-in, and `open` refuses a network runas share: `unc_fetch` the file, then open the copy.

| Tool | Arguments | What it does |
|---|---|---|
| `unc_shares` | `check?` | The named shares (path, account, access, description); `check` lists each root now. |
| `unc_search` | `share?, text?, path?, files?, regex?, context?, output?, order?, limit?, depth?` | `search_files` on a share. |
| `unc_info` | `share?, path?` | A file's size, dates, lines and words; a folder's counts. |
| `unc_read` | `share?, path, start_line?, max_lines?` | Reads a text file, whole or in part. |
| `unc_fetch` | `share?, path, to?, overwrite?` | Copies a file or folder into the working directory (with the File tools on), up to 5,000 files and 500 MB. |
| `unc_write` | `share?, path, content, mode?` | Writes a text file: `create` (default), `overwrite` or `append`. |
| `unc_patch` | `share?, path, old_text, new_text, replace_all?` | Changes part of a text file. |
| `unc_create_directory` | `share?, path` | Creates a folder. |
| `unc_move` / `unc_copy` | `share?, from, to, overwrite?` | Moves, renames or copies within one share. |
| `unc_delete` | `share?, path` | Deletes a file or folder, permanently. Off by default. |
| `unc_put` | `from, share?, to?, overwrite?` | Copies a file or folder from the working directory onto a share (with the File tools on). |

`share` may be left out when `path` is a full path under a share, or when there is only one share; with several, a call that names none is refused. `unc_write` through `unc_put` appear only under both keys.

`--unc-check <share>` proves the tools against a real share on the published exe (its reach, the runas token, a listing and a search).

## Docker

**Windows only:** on a Mac the Docker group is never offered, and `/tools` shows it as *off: it needs Windows*.

The Docker tools use the engine's own API on its named pipe (*Docker engine pipe*); the app never starts `docker.exe` or Docker Desktop, and says when Desktop isn't running. A container is named by its name, a unique part of one ("mysql" for `mysql_dev`), or an id prefix of four or more characters; an ambiguous name comes back as a question.

* **Changes need two keys:** *Docker writes* offers the changing tools, and every call asks on the pane ("Stop container mysql_dev (mysql:8.4, Up 3 days)?"); a prune says first how much it frees. Headless refuses them; `/docker` still works.
* **Removals are opt-in:** `docker_remove` and `docker_prune` start off even under *Docker writes*.
* **Secrets:** `docker_inspect` shows environment variable names, never values, and hides the values of secret-sounding labels and flags and passwords in URLs. Logs can't be redacted, so the model is told never to repeat a secret from one.
* **Audit:** every change, the model's or yours, is logged.
* **Compose:** a project is the containers with its `com.docker.compose.project` label. `docker_lifecycle` with `scope: project` acts on them all in dependency order. `compose up` needs the CLI and isn't offered.

| Tool | Arguments | What it does |
|---|---|---|
| `docker_containers` | `all?, filter?, project?` | The containers, running first: name, state and health, status, image, ports, compose project, short id. `all: false` leaves out stopped ones. |
| `docker_logs` | `container, tail?, since?, grep?, stream?, timestamps?` | The last `tail` lines (100, up to 2000), since an age (`10m`, `2h`, `1d`) or moment, stdout or stderr only. `grep` searches the last 5000. Colour codes removed; at most 16,000 characters, oldest cut first. |
| `docker_inspect` | `container` | One container in detail (state, health, exit code, restarts, image, command, environment names, ports, mounts, networks, restart policy, limits, compose project, labels), secrets hidden. |
| `docker_stats` | `container?` | CPU (as `docker stats` shows it), memory against its limit, network and disk traffic, processes; one container or all running. |
| `docker_resources` | `kind, filter?, unused?` | `images` (tags, size, age, users), `volumes` (who mounts each), `networks` (subnets, members) or `disk` (space per kind and what a prune would free). |
| `docker_compose` | `project?` | The compose projects: folder, files, services and their state. |
| `docker_lifecycle` | `target, action, scope?, timeout_seconds?` | `start`, `stop`, `restart`, `pause` or `unpause` a container or, with `scope: project`, a compose project. A stop waits `timeout_seconds` (10, up to 120) before the kill. Asks first. |
| `docker_pull` | `image, tag?` | Pulls a public image (`nginx`, `postgres:16`, `ghcr.io/owner/app`); no registry credentials are sent. Asks first. |
| `docker_remove` | `kind, name, force?` | Removes one container (keeping its anonymous volumes), image or volume. `force` removes a running container or a used image. Asks first; off by default. |
| `docker_prune` | `kind, all?` | Removes stopped containers, untagged images (every unused one with `all`), empty networks, anonymous unused volumes (named too with `all`) or the build cache. Asks first, with count and size; off by default. |

### Docker servers

Containers serving an OpenAI-compatible API (vLLM, SGLang…) can be `/server` choices, one running at a time so two models never fight over the GPU. Tick them in *Docker server containers* on `/settings` › Servers › Docker and turn on *Docker servers enabled*. This needs neither the Docker tools nor *Docker writes*, and touches only the ticked containers. **Windows only**, like the Docker tools: on a Mac `/server` lists no Docker rows. Working `docker run` commands: [vLLM](VLLM_EXAMPLES_WINDOWS.md) and [SGLang](SGLANG_EXAMPLES_WINDOWS.md).

* **Rows:** `/server` lists one **Docker** row per chosen container with its state, image and ports (`running · vllm/vllm-openai:latest · :8000`). `/server docker` lists them alone; `docker:<container>` (also for `--url` and `NEONSIDEKICK_LLM_URL`) picks one.
* **Switching:** picking one stops every other chosen container still running (waiting up to *Docker server stop timeout* plus 15 s each), waits *Docker server post-stop delay*, then starts or unpauses it. If a stop fails, nothing starts. The spinner shows each step; Ctrl+C or a double-click on it cancels.
* **Readiness:** the app polls `/v1/models` on the container's published TCP ports every second until one answers (minutes, for a large model). A container that exits while loading reports its exit code and last log lines; past *Docker server ready timeout* the switch fails and the container keeps running.
* **Model:** whatever the container serves (the saved *LLM model* if listed, else its first), with the context window it reports. A container serving just one model has its id saved as *LLM model* at each connect, so the setting names it; `--model` or `NEONSIDEKICK_LLM_MODEL` wins and is never saved over, and a headless run saves nothing.
* **Leaving:** picking another server that would use this machine's GPU stops the chosen containers first (before an embedded model loads), including one started outside the app: the embedded model, a server on this machine (`localhost`, `127.0.0.1`, its own name), or a blank *LLM URL* (the scan looks at this machine's ports). The Claude Code server, the Anthropic and OpenAI APIs and a server on another machine leave the container running. A profile switch follows the same rule. *Docker server stop on exit* stops the one in use when the app exits.
* **Bots and advisors:** a `/botchat` bot or an advisor pointing at a container shares it if it's the one running, even after you moved to a server that left it running, and never starts or stops one.

`--docker-check` proves the tools against the real engine on the published exe (the API version, the containers, a redacted inspect, a log and a stats sample).

## Home Assistant

The Home Assistant tools control your own Home Assistant over its REST API with a long-lived token (*Home Assistant URL*, *Home Assistant API key*): lights and scenes, a TV, sensors, to-do lists, whatever it has connected.

* **Names, not ids:** "the den", "kitchen and hallway", "Den Corner Lamp" or "all". A room goes to its group light when it has one, else to every light in it. An ambiguous name comes back as a question, an unknown one with close matches, so the model never guesses an id.
* **The action policy:** under `ask`, safe services run and anything else shows the service, device and data on the pane first. A no tells the model not to retry.
* **Fresh states:** read at most every 30 seconds, and again after any change.

| Tool | Arguments | What it does |
|---|---|---|
| `ha_overview` | — | Lights on per room, light groups, media players, temperatures, motion, low batteries (under 20%), to-do lists, the scene count and unavailable lights. |
| `ha_states` | `query?, domain?, area?` | Entities, one line each (id, name, state, and what matters for the domain), narrowed by words, domain and room (at most 80). An exact entity id gives every attribute. |
| `ha_history` | `entity, hours?` | One entity's states over the last 1–336 hours (24 by default), oldest first. |
| `ha_lights` | `target, action?, brightness_pct?, color_name?, color_temp_kelvin?, transition?` | `on` (default; also changes brightness or colour), `off` or `toggle`, with a colour name or white temperature (1500–9000 K) and a 0–300 s fade. |
| `ha_scene` | `scene, transition?` | Activates a scene by name or id. |
| `ha_media` | `action, target?, volume_pct?, source?` | `on`, `off`, `volume`, `volume_up`, `volume_down`, `mute`, `unmute`, `source`, `play`, `pause`, `play_pause`, `stop`, `next`, `previous`. The target may be left out when there's one player. A source matches by name or prefix (`hdmi 3` → `HDMI 3 (eARC/ARC)`). |
| `ha_todo` | `action, item?, list?` | `list` (allowed under every policy), or `add`, `complete` or `remove` an item. Off in a new profile; switch it on in `/tools` › Offered. |
| `ha_call_service` | `domain, service, entity?, data?` | Any other service (`remote.send_command` with `{"command": "Home"}`, `button.press`, `script.turn_on`), subject to the policy. |
| `ha_assist` | `text` | Hands a sentence to Home Assistant's Assist agent, as a last resort. Refused under policy `off`; Assist reaches only entities exposed to it. |

## Printing

`/print` and the print tools send work to any printer Windows has installed, or on a Mac to any printer set up in System Settings › Printers & Scanners (through CUPS).

* **Drawn by the app:** text and code print as a monospace listing (tabs as four columns, long lines wrapped, form feeds start pages); Markdown prints formatted (headings, emphasis, lists, quotes, code blocks, rules, links with their address, tables cut to fit); a picture (PNG, JPEG, GIF, WebP, BMP) is fitted to one page, never enlarged. Every page carries the file's name, the time and *page N of M*; paper, tray and quality are the printer's.
* **Anything else** (a PDF, a Word or Excel file) goes to the program Windows has for printing it, on the default printer only; a printer, copies, pages or landscape given with it is refused. A type nothing can print is refused.
* **On a Mac** the pages are drawn into a PDF in Helvetica Neue and Menlo (Segoe UI's and Consolas' stand-ins) on the printer's own paper and margins, and sent to CUPS; landscape is turned onto the paper by the app. The layout follows the same rules as on Windows, but the fonts' widths differ, so a long file may break lines and pages a little differently. A **PDF** goes to the printer as it is, and takes a printer, copies and pages (not landscape: each page keeps the PDF's own orientation). A Word, Excel or other file is refused: print it from the program that made it. Printers are listed by their CUPS queue name with the name System Settings shows (`Brother_HL_L2340D_series — Brother HL-L2340D series`), and `printer=` matches either. ESC (or Ctrl+C) cancels a print, on either system: the job is withdrawn, purged if it waits in the queue or stopped if it is printing, and the chat says *(print cancelled)*; a print that finished before the key says *Printed …* as usual.
* **The policy:** under `ask`, `print_file` shows the file, printer and sheets and waits for your yes. A no tells the model not to retry.

| Tool | Arguments | What it does |
|---|---|---|
| `list_printers` | — | The installed printers, marking the system's default (Windows' or the Mac's) and *Print default printer*. Available in plan mode. |
| `print_file` | `path, printer?, copies?, pages?, landscape?` | Prints a file from the working directory: a printer by name (else *Print default printer*, else the system's default), 1–10 copies, pages like `1-3`, `4-` or `1,3,5`, and landscape. Not in plan mode. |

### Making PDFs

`/pdf` and the `convert_to_pdf` file tool make a PDF in the working directory. Nothing new is installed: the browser you already have makes it, or Windows' own PDF printer. On a Mac only the browser makes it (Edge, Chrome, Brave or Chromium in `/Applications`); the `printer` engine needs Windows (it is Microsoft Print to PDF), even though a Mac prints.

* **Markdown** keeps its headings, emphasis, lists and task lists, quotes, tables, links (clickable), footnotes and code blocks, coloured as in the transcript. Its pictures come from the working directory, relative to the file; a picture outside it or on the web shows as `[image: alt]`. Raw HTML in it is shown as text.
* **Text and code** become one listing, coloured by the file's extension. **A picture** is fitted to one page, never enlarged.
* **An HTML file** is printed as the page, after its scripts, frames and embeds are taken out; its stylesheets and pictures come from the working directory only.
* **A web page** is printed as the browser shows it, after *Web browser network mode* allows its address (the page's own requests are not checked beyond that). It keeps its own layout, so paper and landscape are not taken.
* **The pages:** US Letter by default (`paper=` A4 or Legal), portrait unless landscape, with the title at the top left and *page N of M* at the top right.
* **The engine** (*PDF engine*): Edge, Chrome, Brave or (on a Mac) Chromium (*Web browser path*, else the first found) prints the page; it gets a minute. Without one, or when it fails under `auto`, **Microsoft Print to PDF** draws Markdown, text and pictures as `/print` would: black and white, two fonts, on the driver's paper. HTML and web pages need the browser.
* **The output** goes beside the source with `.pdf` (a web page or Markdown text at the top: `example.com-intro.pdf`, `reply-2026-10-03-1405.pdf`), or where `to` says (a file, or a folder). An existing PDF is replaced only with `overwrite`. A PDF, or a file that is neither text nor a picture, is refused.
* `convert_to_pdf` is a file tool, so *File tools* decides; a `url` needs *Web tools* as well. `/pdf` needs neither.

## ComfyUI

The image tools run **your own ComfyUI workflows** on your server (*ComfyUI URL*), save the pictures under the working directory, and show them to the model in the next message.

### Adding a workflow

**With the wizard** (*ComfyUI add workflow* on `/tools` › Integrations › ComfyUI):

* **Build** makes a standard text → image or image → image workflow from your server's lists: checkpoint, family, folder (this profile's or every profile's), name, CLIP skip, sampler, scheduler, size, steps, CFG, denoise, negative and description.
* **Import** takes a ComfyUI API export (*Workflow → Export (API)*). It finds the prompt, negative, seed, steps, CFG, size and input-image nodes, puts the placeholders in, and keeps the export's values as defaults. It reads both a plain `KSampler` graph and FLUX.2's custom-sampler graph. A value fed by a primitive node gets its placeholder there; one set by another node (a switch, a resolution picker) is left alone, and the wizard says so.
* FLUX.2, Krea 2, Z-Image, Qwen Image, Ernie Image, Boogu, LongCat Image, HiDream I1 and Ideogram 4 load separate model files that **Build** can't wire; export ComfyUI's own template and **Import** it.
* The summary can **test** the draft (one small run, at most 512 px and 8 steps, nothing saved) and saves `<name>.json` + `<name>.md`, offered or hidden until ticked. ESC steps back.

**By hand:**

1. Build the workflow in ComfyUI and export it in the **API format** (*Workflow → Export (API)*, or *Save (API)* in dev mode). A regular save, with `nodes` and `links`, is refused.
2. Put placeholders where the call's values go, and drop the file into `<profile>\comfy\` (this profile) or `<home>\comfy\` (every profile; the profile's wins a name clash). The file name is the workflow's name; workflows are re-read at every call.

| Placeholder | Becomes |
|---|---|
| `{{prompt}}` | The positive prompt. Required unless the workflow takes an input picture (a face swap or upscale, listed as *no prompt*). |
| `{{negative}}` | The negative prompt. |
| `{{seed}}`, `{{width}}`, `{{height}}`, `{{steps}}`, `{{cfg}}`, `{{denoise}}` | A number when it is the whole value (`"seed": "{{seed}}"`), or text inside a longer string (`"neon-{{seed}}"`). |
| `{{image}}` | The uploaded input picture's name, for a `LoadImage` node (img2img, upscale, inpaint). |
| `{{image2}}`, `{{image3}}` | The second and third input pictures (a face swap's face, Qwen-Image-Edit's pictures). They fill in order, or the workflow is skipped. |
| `{{!name}}` | A literal `{{name}}`, for nodes that use double braces themselves (Ideogram 4's `StringReplace`). **Import** escapes these for you. |

3. Optionally, a sidecar `<name>.md` beside it sets the defaults and tips:

```markdown
---
description: Anime portraits on Pony Diffusion XL
family: pony          # pony, illustrious, juggernaut, sdxl, flux, flux2, flux2klein, krea2, zimage, qwenimage, sd35, ernie, boogu, longcat, hidream, ideogram4, sd15 or other (guessed from the file name otherwise)
width: 832
height: 1216
steps: 25
cfg: 7
negative: score_6, score_5, score_4, blurry
reinforce: false      # optional: send this negative as written, nothing appended (ComfyUI reinforce negatives)
image: the picture to restyle   # optional, per input picture: its role, shown to the model (image, image2, image3)
---
Prefer source_anime; keep rating_safe unless asked.
```

### Prompts

- **Describe what you want** ("a cozy neon ramen stall at night") and the model writes the prompt in the family's style (below), adding the family's default negative unless the sidecar names one.
- **Give your own prompt** ("use this prompt: score_9, …") and the model passes it unchanged (`verbatim: true`), with nothing added.
- **Skip the model:** `/imagine score_9, score_8_up, source_anime, 1girl -- score_4, blurry --seed 42`.
- **Limit the choice** with *ComfyUI workflows offered*.

| Family | Prompt style the model writes |
|---|---|
| Pony Diffusion XL | `score_9, score_8_up, score_7_up`, a `source_*` tag, then well-known Danbooru tags, weighted like `(tag:1.3)` where it helps; a short phrase only where no tag is specific enough |
| Illustrious XL, NoobAI | `masterpiece, best quality, amazing quality, very aesthetic, absurdres`, then Danbooru tags |
| Juggernaut XL | A photographic description (subject, setting, light, lens) |
| Flux | Plain sentences |
| FLUX.2 dev | Long structured prose with hex colours |
| FLUX.2 Klein | A few clear sentences |
| Krea 2 | One rich paragraph |
| Z-Image Turbo | Concise prose |
| Qwen Image | Long prose with the exact words in quotes (it renders text well) |
| SD3.5 | Sentences plus style phrases |
| Ernie Image, LongCat Image | Rich prose with the words in quotes |
| Boogu | Descriptive sentences |
| HiDream I1 | Long, detailed prose |
| Ideogram 4 | A JSON prompt (description, style, colour palette, laid-out elements) |
| SD 1.5, SDXL | Tags and weights |

### Several-picture workflows (face swaps)

A workflow can take up to three pictures. For a face swap:

1. Install the ReActor node pack on your ComfyUI server and build the swap (two `LoadImage` nodes → `ReActorFaceSwap` → `SaveImage`).
2. *Export (API)* and **Import** it. The `LoadImage` nodes become `{{image}}` and `{{image2}}` in node-id order (the wizard says which); no sampler is needed.
3. Say which picture is which in its `.md`:

```markdown
---
description: Face swap (ReActor)
image: the picture whose face is replaced
image2: the face to put in
---
```

Then "put my face from [Image #2] on the person in [Image #1]" works in chat, or `/imagine faceswap --image target.png --image2 face.png` without the model. A Qwen-Image-Edit graph (prompt in `TextEncodeQwenImageEditPlus`) needs `{{prompt}}` put in by hand.

### The tools

| Tool | Arguments | What it does |
|---|---|---|
| `generate_image` | `prompt?, workflow?, negative?, negative_extra?, verbatim?, width?, height?, seed?, steps?, cfg?, denoise?, image?, image2?, image3?, count?` | Runs a workflow (the only fitting one when none is named), saving 1 to *ComfyUI max pictures per call* pictures, each with the next seed. The result names the files and seed; the pictures follow in the next message, as JPEG unless transparent. `prompt` may be left out only for a workflow without `{{prompt}}`. |
| `set_splash_image` | `path, name?` | Copies a picture from the working directory, or by full path from the profile's `images` folder (a generated picture, a photo), into the profile's `images\splash_images` folder, so it shows at start and on `/splash`. The first one replaces the bundled set. A picture already there is refused. |

* A refused run names the node and input at fault (a missing checkpoint, a bad value).
* `image` is a path under the working directory, the full path a saved picture was given (a generated one, a photo, a screenshot, a paste), or a pasted picture's `[Image #N]`. A pasted picture goes at its original size, from the file it was saved as in *Pasted images folder* (saved first if its message wasn't sent yet).
* The pictures are saved in *ComfyUI output folder* under the profile's `images` folder, out of the working directory; the result gives each one's full path.
* `image2` and `image3` take the roles the workflow's `.md` names. Without a workflow name, the one taking that many pictures is used.

## Media

### Images

*Image tools* offers them (on `/tools` › Files › Image, `/tools image` or the toolbar's 🌄; off in a fresh profile), whether *File tools* is on or not; they were file tools until 2026-10-10. They reach the working directory and, by full path, the pictures saved in the profile's `images` folder (photos, screenshots, ComfyUI's pictures, pastes); nothing else outside the working directory. The model finds those pictures with `search_files` on the folder's full path, which its description gives (`order: modified` for the newest). No other file tool reaches the folder: `read_file`, `delete`, `move` and the rest refuse a path there, naming the tools that do.

| Tool | Arguments | What it does |
|---|---|---|
| `view_image` | `path?, paths?` | Attaches images to the next message, up to *File view image max (per call)*. On a Mac it also takes HEIC/HEIF, TIFF and AVIF, sent to the model as JPEG (TIFF as PNG). |
| `image_info` | `path?, paths?` | A picture's format, upright size, file size, frames, transparency, EXIF orientation and the metadata it carries (EXIF, GPS, XMP, data after the picture…), read from its header (nothing goes to the model), then the formats `image_edit` can write here and its defaults. |
| `image_edit` | `path, to?, overwrite?, format?, quality?, max_kb?, width?, height?, scale?, fit?, anchor?, interpolation?, crop_x/y/width/height?, rotate?, flip?, filter?, brightness?, contrast?, saturation?, hue?, tint?, tint_amount?, blur?, sharpen?, pad?, background?, metadata?, dpi?, chroma?, colors?, dither?, interlace?, view?` | Resizes, crops, turns, recolours and converts a picture in one pass and writes a new file (see Editing pictures); `metadata: none` alone strips a JPEG, PNG, WebP or GIF losslessly. `view` attaches the result. Not in plan mode. |

#### Editing pictures

`image_edit` changes a picture in the working directory with the system's own codecs (WIC on Windows, ImageIO on a Mac; nothing to install), and an edit looks the same on either. `image_info` reads a picture's facts first.

* **One pass, in this order:** crop, resize, rotate, flip, colour, blur, border; then one encode, so a JPEG loses quality once.
* **Size:** `width` and/or `height` (one alone keeps the aspect) or `scale`. With both sides, `fit` is `contain` (the default; may enlarge), `cover` (fills and cuts at `anchor`), `pad` (fills the rest with `background`), `stretch`, or `shrink` (never enlarges). Enlarging is interpolation, not AI. `interpolation=nearest` keeps pixel art sharp.
* **Geometry:** a crop in the picture's pixels as it displays (`crop_x`, `crop_y`, `crop_width`, `crop_height`, all four), `rotate` by quarter turns only (90, 180, 270 clockwise), `flip` horizontal or vertical. Width and height always mean the final picture's sides.
* **Colour:** `filter` (grey, sepia, negative, polaroid), `brightness`, `contrast`, `saturation` (−100 to 100), `hue` (degrees), `tint` toward a colour by `tint_amount`, `blur`, `sharpen` (true firm, false none; a light one after a resize by default), and a `pad` border in `background` (white by default; `transparent` works for PNG).
* **Formats on Windows:** PNG, JPEG, GIF, BMP and TIFF; JPEG XL and HEIF when Windows has their extensions (`image_info` says which). WebP and AVIF read but never write: Windows has no encoder for them, so a WebP comes out as PNG. JPEG takes `quality` and `chroma` (444 keeps text crisp); PNG and GIF take `colors` (a palette, much smaller) and `dither`; PNG takes `interlace`; any format takes `dpi`. An option the format cannot take is refused, not ignored.
* **On a Mac:** it reads PNG, JPEG, GIF, BMP, TIFF, WebP, HEIC/HEIF (iPhone photos), AVIF and JPEG XL, and writes PNG, JPEG, GIF, BMP, TIFF and HEIF (no JPEG XL, WebP or AVIF). `quality`, `dpi` and `interlace` work; `chroma`, `colors` and `dither` are refused, since ImageIO's encoders have no such setting. A HEIC photo's orientation is read as a JPEG's is.
* **`max_kb`:** lowers the quality (lossy formats, down to 30), then shrinks the picture, until the file fits; nothing is written when it cannot.
* **Metadata** is dropped unless asked for (*Image edit metadata*, or `metadata` per call); the EXIF orientation is always baked into the pixels. `metadata: none` with nothing else is a **lossless strip** of a JPEG, PNG, WebP or GIF: the file's metadata segments are left out and every other byte copied, so the picture is not re-encoded and a WebP stays a WebP. EXIF (with its GPS position and thumbnail), XMP, IPTC, comments, timestamps, vendor data and anything after the picture's end (a motion photo's video, an Ultra HDR gain map) go; the colour profile stays, and a turned photo keeps its orientation as a bare tag. The result says what went and the sizes before and after; a picture with nothing to strip writes nothing. An animated picture gives its first frame, and the result says so.
* **The output** goes beside the source (or into *Image edit output folder*, a folder under the profile's `images` folder) as `photo-edited.png`, or `photo.png` for a format change alone, with `-2`, `-3` on a clash; or where `to` says. An existing file, the source included, is replaced only with `overwrite`. With *Image edit mode* `overwrite-original` and no `to`, the result replaces the source instead, and a format change writes `photo.jpg` and deletes `photo.png`.
* **Limits:** a source up to 200 MB and 100 megapixels; a result up to 32768 pixels a side and 100 megapixels.

### Camera

| Tool | Arguments | What it does |
|---|---|---|
| `camera_capture` | `prompt` | Shows the model's request ("Hold the label up to the camera."), then waits for you to take the photo (*Camera shutter* `user`) or for your permission (`model`). The photo is saved in *Camera output folder* under the profile's `images` folder (the result gives its full path) and attached after the result; a decline isn't retried that turn. Allowed in plan mode. |

On Windows the camera is Media Foundation's; on a Mac (macOS 14 or later) AVFoundation's: the built-in camera, a USB webcam, or an iPhone as Continuity Camera. A Mac asks once whether your terminal may use the camera (System Settings › Privacy & Security › Camera holds the answer); until it is allowed the tool's result says so, never a black picture.

### Screen

| Tool | Arguments | What it does |
|---|---|---|
| `screen_capture` | `target?, prompt` | Captures a monitor, every monitor or one window (see [Screen capture](COMMANDS.md#screen-capture)), after your yes under *Screen capture ask* `ask`. The screenshot is saved in *Screen capture output folder* under the profile's `images` folder (the result gives its full path) and attached after the result; a denial isn't retried that turn. Allowed in plan mode. |
| `screen_list` | (none) | Lists the monitors and the windows with the target that names each. Titles and sizes only. Allowed in plan mode. |

On Windows the pictures come from GDI; on a Mac (macOS 14 or later) from ScreenCaptureKit, at full pixels (a Retina screen at twice its points). A Mac needs Screen Recording for your terminal app (System Settings › Privacy & Security › Screen & System Audio Recording, then quit and reopen the terminal); without it no picture is sent, only the sentence saying how to turn it on, and `screen_list` gives the monitors without the windows.

### YouTube

Under *YouTube tools*. Videos play in the app's own video window: a Windows window hosting the Microsoft Edge WebView2 Runtime (Windows 11 has it) on YouTube's embedded player. It opens where it last closed without taking the keyboard from the terminal; F11 is full screen, Esc leaves full screen and then closes it, and Ctrl and Alt chords go to the chat. YouTube's own keys (Space, ← →, M, F) work once you click into the video. One window: a new video replaces the one playing. A video whose uploader turned embedding off, or that is private, age-restricted or gone, is refused, and the answer points at `open_url`.

On a Mac (macOS 14 or later, not over SSH) the window plays the same page through WebKit, nothing to install. ⌃⌘F is full screen (F11 too if macOS's Show Desktop shortcut is off); Esc leaves full screen, then closes the window, as ⌘W does; Tab brings the terminal forward; Ctrl and Option chords go to the chat. Links in the player open in your default browser, and its own full-screen button fills a Space of its own (Esc brings it back). YouTube's data is kept per home under `~/Library/WebKit/NeonSidekick/WebsiteDataStore/`, apart from Safari's; WebKit blocks the player's third-party cookies, so a consent asked inside the player may be asked again next run.

| Tool | Arguments | What it does |
|---|---|---|
| `youtube_search` | `query, max?, kind?, order?, length?, since?, region?, language?, live?, captions?, safe_search?` | Searches YouTube through the Data API with *YouTube API key* (100 quota units a search, 1 more for the details) and lists videos that play embedded: title, channel, length, views, year and id. `kind` finds playlists (with their video counts) or channels (subscribers and videos) instead. The filters: `order` (relevance, date, views, rating, title), `length` (short under 4 minutes, medium 4–20, long over 20), `since` (today, week, month, year or a date), `region` (two letters), `language` (a code whose results rank higher), `live`, `captions`, `safe_search` (none, moderate, strict); length, live and captions filter videos only. One left out is the user's saved choice (*YouTube search order*, *safe search*, *region*, *language*) or YouTube's own. Offered only with a key set, headless too. Allowed in plan mode. |
| `youtube_playlist` | `playlist?, max?` | Lists a public playlist's videos, numbered as `youtube_play`'s `entry` counts them, with title, channel and id; a deleted or private entry says so. Named by its id, a link with `list=` or a channel (its uploads); left out, the playlist playing. `max` 1–200, 50 by default (1 quota unit for the playlist, 1 per 50 videos). Offered only with a key set, headless too. Allowed in plan mode. |
| `youtube_play` | `video?, start?, playlist?, entry?` | Plays a video by its id or any YouTube link (a link's `t=` included), from `start` (`90` or `1:30`), and answers with what the player reports. Or a public playlist (`playlist`: an id, a link with `list=`, or a channel for its uploads; or such a link as `video`) from `entry` (1-based), the link's own place or the `video` named in it (at the link's `t=`): YouTube's player plays it through, passing over an entry it refuses. A mix (`list=RD…`, what a video shared from YouTube's radio carries) is no playlist: such a link plays its video. Starts at once, or cued under *YouTube autoplay* off. Needs no key; not headless. |
| `youtube_control` | `action, value?` | `play`, `pause`, `seek` (to a time), `forward` / `back` (by seconds, 10 by default), `volume` (0–100), `mute`, `unmute`, `next` / `previous` / `entry` (the playlist's, `entry`'s value 1-based) or `close`, and answers with where the video then is. |
| `youtube_status` | (none) | What the window is playing: the video and channel, the state, the position and length, the volume, and in a playlist which entry of how many. Allowed in plan mode. |
| `youtube_saved` | (none) | Lists your saved videos, numbered, with channel, where each was left (or watched) and id; any still untitled are looked up first. Headless too. Allowed in plan mode. |
| `youtube_save` | `action, video` | `add` saves a video (`current` for the one in the window, with where it is; one named by id or link is looked up for its title), `remove` takes one off (also by its number in `youtube_saved`). Headless too, by id or link. |

A playlist plays in YouTube's own player, which moves through it by itself; nothing is read through the API to play one, so it needs no key. A mix made for one viewer (`RD…`) may play but is not listed, and Watch Later and your own private lists are out of reach without signing in, which the app does not do. A saved video reached inside a playlist keeps its saved place: the playlist starts each entry at its beginning.

Saved videos (your bookmarks, per profile, in `youtube.json`) resume where they were left: the app writes a saved video's place when it is paused, ends, is switched away from or its window closes, and every 15 seconds while it plays. `youtube_play` with no `start` picks a saved video up a few seconds before that place; one seen to the end (or stopped in its last 10 seconds) starts over and reads `watched`. A video saved by its id or link gets its title and channel from YouTube's oEmbed (no key, no quota), or, when that fails and a *YouTube API key* is set, from the Data API (1 quota unit, the length too); one found nowhere keeps its id until it first plays. `/youtube save`, `/youtube list` and `/youtube unsave` are your own hand on the list (see [Commands](COMMANDS.md)).

## Shell & Web

### Shell

Runs commands on your machine, starting in the working directory (`workdir` picks a folder under it), guarded by *Shell command policy* and *Shell police* (see [Shell guards](SETTINGS.md#shell-guards)). A denied command tells the model not to work around it.

* Commands run hidden, output read as UTF-8 with colours and pagers off, and stdin closed (background processes keep it for `process`).
* A command that times out is killed with everything it started. Background processes stop when the app closes; a crash leaves running commands running.
* `/process` lists the background processes, and `/process <id>` watches one's output live in the [process window](COMMANDS.md#process-window), where Ctrl+K twice stops it.
* `/cmd <command>` runs a line of your own through the same layers: the policy, the police and, under `ask`, the approval pane ([Your own shell line](COMMANDS.md#your-own-shell-line)).

#### Headless runs

[HEADLESS.md](HEADLESS.md) has worked examples of every flag, the commands that work headless, yolo runs and scheduled jobs.

* `--headless` has no approval pane, so under `ask` only allow-listed commands run; the model is told to say what couldn't run.
* If any command was refused, the run ends with `[notice] N commands were not run: …` and **exit code 3**. 0 means nothing was refused; 2 means a bad argument or unknown profile.
* `--yolo` (or `NEONSIDEKICK_COMMAND_POLICY=yolo`; the flag wins) allows every command for one launch, never saved. The path police still applies unless `--no-police` (or `NEONSIDEKICK_SHELL_POLICE=off`) lifts it; together they leave no guard at all.
* A headless run loads `neon`, not the TUI's last profile; `--profile <name>` (or `NEONSIDEKICK_PROFILE`) names another for this launch only.

```powershell
Get-Content job.txt | NeonSidekick.exe --headless --yolo --cwd D:\Repo\MyApp
Get-Content job.txt | NeonSidekick.exe --headless --profile work
```

```zsh
cat job.txt | ./NeonSidekick --headless --profile work   # macOS
```

| Tool | Arguments | What it does |
|---|---|---|
| `run_command` | `command, shell?, workdir?, timeout?, background?, notify?` | Runs a command in `powershell` (default), `cmd` or `bash` (on macOS `zsh` (default), `bash` or `powershell`), returning `exit N in T s (shell)…`, stdout and stderr. `background` (or a long timeout) returns a `proc_…` id; `notify` shows `⚡` when it exits and queues a `process poll` for the next turn. |
| `execute_code` | `language, code, timeout?` | Runs a one-off `python` (`python3` on macOS), `node` or `powershell` script, approved once per language per session; nothing carries over between scripts. With *Shell tool bridge*, the script can call the app's tools (Python `from neon_tools import call`, Node `await neon.call(...)`, PowerShell `Invoke-NeonTool`), except `execute_code` and `ask_user`; `run_command` through the bridge still needs approval and can't run in the background. |
| `process` | `action, session_id?, data?, timeout?, offset?, limit?` | Manages up to 16 background processes (and the last 64 finished), named by any unique id prefix: `list`, `poll`, `log` (a window of the last 5,000 lines), `wait`, `kill` (with children), `write` / `submit` (to stdin; `submit` adds a newline), `close`. |

### Web

| Tool | Arguments | What it does |
|---|---|---|
| `web_search` | `query, max_results?` | Searches the web (DuckDuckGo or SearXNG): title, URL, snippet. |
| `web_fetch` | `url, offset?` | A page's readable content as Markdown, 32,000 characters at a time; also plain text, JSON, XML and CSV. |
| `open_url` | `url?, urls?` | Opens a link, or up to five, in your browser. |
| `download_file` | `url, path?, overwrite?` | Downloads a file into the working directory, up to *Web download max (MB)*, streamed to disk. Needs *File tools* too. |

## Memory, Skills & Sessions

### Memory

| Tool | Arguments | What it does |
|---|---|---|
| `save_memory` | `text` | Saves one lasting fact about you, known in every later session. Not offered while *Memory mode* is `read-only`. |
| `recall_memory` | — | Everything remembered, oldest first. Seeded at the start of every conversation. |

### Skills

| Tool | Arguments | What it does |
|---|---|---|
| `load_skill` | `name, file?` | Loads a skill's instructions (the list is in the system prompt), or one of its files, up to 64,000 characters. Offered only while a skill is installed; never a skill you [disabled](SETTINGS.md#disabling-a-skill). |
| `skill_editor` | `action, scope?, name, description?, instructions?, path?, content?, old_text?, new_text?, replace_all?, summary?` | `create` or `update` a skill under `profile` (default) or `global`. For supporting files, `write_file` writes a whole file and `edit_file` swaps `old_text` for `new_text`; `path` is relative to the skill folder. Never touches `.neon-source.json`, `.git` or `node_modules`, never edits external skills or a skill you [locked](SETTINGS.md#locking-a-skill) or [disabled](SETTINGS.md#disabling-a-skill) (nor creates one of a disabled skill's name), and never deletes. |

### Sessions

| Tool | Arguments | What it does |
|---|---|---|
| `session_manager` | `action, query?, id?, max_results?, from_turn?, to_turn?` | `search`, `list` or `read` this profile's earlier conversations (not the current one). Never restores or purges. |

### Claude advisor

| Tool | Arguments | What it does |
|---|---|---|
| `claude_code_advisor` | `question, context?` | Asks Claude Code for read-only advice; it can read the working directory and the web. The transcript shows the question, Claude's tools, the answer and a cost footer. One advisor conversation per session, separate from `/claude`'s. ESC stops it with the reply; `/usage` counts it. |

### Advisors

Other profiles of this home, each in a role you give it, set up under `/settings › Advisors` ([SETTINGS.md](SETTINGS.md#advisors)); `/advisor` asks one yourself ([COMMANDS.md](COMMANDS.md#advisor)).

| Tool | Arguments | What it does |
|---|---|---|
| `neon_advisor` | `advisor, question, context?` | Asks one advisor, named by its profile or role, for its view. It answers on its own profile's server and model, under its own `persona.md` and its role, and may call this chat's read-only tools (those plan mode keeps, never `ask_user`, the camera, the screen or an advisor) for up to *Advisor tool iterations* round trips. The transcript shows the question, its tools, the answer and a footer with its model and tokens. Each advisor keeps a thread of its questions and answers for the session. Offered while *Advisors enabled* is on and an advisor is listed; plan mode keeps it. An advisor on the Claude Code server gets the tools only when it is the first listed there and this chat is on another server; otherwise it answers text-only (see [SETTINGS.md](SETTINGS.md#advisors)). In [plan mode](COMMANDS.md#plan-mode) the model is asked to consult those whose role fits the plan before presenting it. |

### Questions

| Tool | Arguments | What it does |
|---|---|---|
| `ask_user` | `questions` | Multiple-choice questions on the pane: up to *Ask max questions*, each with 2 to *Ask max choices per question* options, `single` or `multi`, plus *Other…*. ESC declines them all. |

### Plan

| Tool | Arguments | What it does |
|---|---|---|
| `present_plan` | `title, markdown, name?` | Plan mode only. Saves the plan as `.neon/plans/<name>.md` with a `status` / `revision` / `requirement` header, prints it and asks for approval; revisions overwrite the same file. The result is your verdict. Headless, the plan is saved and `/plan approve` starts it. `status` is `draft`, `approved`, `cancelled`, `done` or `incomplete`. |

## MCP servers

Each connected server is its own tool group, named `<server>__<tool>` (`gateway__get_current_time`) with its server's descriptions. Servers connect in the background (🔌 and a count on the hint row); a reply started meanwhile gets the ones already connected. The Servers tab of `/mcp` switches whole servers, the Tools tab single tools.
