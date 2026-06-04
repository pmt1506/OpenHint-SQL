# OpenHint SQL

OpenHint SQL is an SSMS extension that adds fast, context-aware SQL autocomplete, FK-based JOIN suggestions, and snippet expansion for day-to-day query writing.

## Features

- Context-aware autocomplete for `FROM`, `JOIN`, `SELECT`, `WHERE`, `ORDER BY`, `GROUP BY`, `EXEC`, and `USE`
- FK-aware JOIN suggestions with the `ON` clause generated from real foreign keys
- Dot completion for `alias.`
- Built-in snippet shortcuts expanded with `Tab`
- Custom snippets managed from an in-editor settings popup
- Configurable `dbo` omission when inserting tables/views
- Smarter alias generation for underscore-heavy names such as `TT_NOITRU_BENHAN -> tnb`
- Token-aware and light fuzzy object search, so typing `tiepnhan` can still find `TT_TIEPNHAN`
- Persistent schema cache plus eager preload on query-window open
- Native SSMS IntelliSense suppression while OpenHint SQL's popup is visible

## Supported SSMS Versions

OpenHint SQL targets SSMS 18 through SSMS 22.

| SSMS version | Architecture | Support status |
|---|---:|---|
| 18.x | 32-bit | Supported |
| 19.x | 32-bit | Supported |
| 20.x | 32-bit | Supported |
| 21.x | 64-bit | Supported |
| 22.x | 64-bit | Supported |

## Installation

### Option A - Installer

1. Download `OpenHintSQLSetup-1.0.2.exe` from the [v1.0.2 release](https://github.com/pmt1506/OpenHint-SQL/releases/tag/v1.0.2).
2. Close SSMS.
3. Run the installer as Administrator.
4. Reopen SSMS and start typing in a query window.

### Option B - Build and install from source

Prerequisites:

- .NET SDK 6+
- .NET Framework 4.8
- At least one SSMS installation
- Visual Studio 2022 recommended if you want `MSBuild.exe` available locally

```cmd
git clone https://github.com/pmt1506/OpenHint-SQL
cd OpenHint-SQL

REM Build + install to all detected SSMS versions (run as Administrator)
scripts\install.bat

REM Install to one version only
scripts\install.bat 22
```

`scripts\install.bat` auto-builds `Release` and prefers `MSBuild.exe` when available.

To build the installer:

```cmd
scripts\build-installer.bat
```

This writes `dist\OpenHintSQLSetup-1.0.2.exe`.

## Usage

### Autocomplete

The popup opens automatically as you type.

Navigation:

- `Up` / `Down` to move
- `Tab` or `Enter` to accept
- `Esc` to dismiss

| What you type | What the popup shows |
|---|---|
| After `FROM ` or `JOIN ` | Tables and views |
| After `JOIN ` when a FROM table is in scope | FK-related tables with generated `ON` clause |
| After `SELECT `, `WHERE `, `ORDER BY `, `GROUP BY ` | Columns from tables already in scope |
| After `alias.` | Columns of the resolved table |
| After `EXEC ` | Stored procedures and functions |
| After `USE ` | Databases visible on the active server |
| Anywhere else | T-SQL keywords, functions, and snippet shortcuts |

Insert behavior:

- Databases insert as `[DatabaseName]`
- Tables/views can insert without `dbo`, for example `TT_TIEPNHAN`
- `FROM` and `JOIN` insertions also append an alias, for example `TT_TIEPNHAN tt`

Search behavior:

- `TT_TIEPNHAN` can be found by typing `tiepnhan`
- Light typo tolerance helps with near-miss names
- Snake-case names generate shorter aliases such as `TT_NOITRU_BENHAN -> tnb`

### Snippets

Type the shortcut and press `Tab` to expand.

Examples:

- `ssf` -> `SELECT * FROM `
- `st10` -> `SELECT TOP 10 * FROM `
- `ut` -> `UPDATE $table$ SET $column$ = $value$ WHERE `
- `del` -> `DELETE FROM $table$ WHERE `
- `jn` -> `INNER JOIN $table$ ON `
- `lj` -> `LEFT JOIN $table$ ON `
- `wh` -> `WHERE `
- `ob` -> `ORDER BY `
- `cte` -> `WITH $name$ AS (...) SELECT * FROM $name$`

### Settings Popup

Press `Ctrl+Alt+Q` inside a SQL query window to open **OpenHint SQL Settings**.

Current settings:

- Toggle omission of `dbo` when inserting tables/views
- Add, edit, or remove custom snippets
- Save custom snippets and use them immediately without restarting SSMS

Settings are stored at:

```text
%LocalAppData%\OpenHintSQL\settings.json
```

## Architecture

```text
src/OpenHintSQL/
|-- Completion/
|   |-- CompletionCommandFilter.cs
|   |-- CompletionEngine.cs
|   `-- CompletionViewCreationListener.cs
|-- Context/
|   `-- SqlContextParser.cs
|-- Schema/
|   |-- AsyncSchemaLoader.cs
|   |-- AsyncDatabaseLoader.cs
|   |-- SchemaCache.cs
|   |-- DatabaseListCache.cs
|   |-- SchemaPersister.cs
|   `-- TableInfo / ColumnInfo / ForeignKeyInfo / ProcedureInfo
|-- Connection/
|   `-- ConnectionTracker.cs
|-- Providers/
|   |-- SqlKeywordProvider.cs
|   `-- CompletionItemData.cs
|-- Snippets/
|   |-- SnippetProvider.cs
|   `-- SnippetDefinition.cs
|-- Settings/
|   |-- OpenHintSqlSettings.cs
|   `-- SettingsProvider.cs
|-- UI/
|   |-- CompletionPopup.cs
|   `-- SettingsWindow.cs
`-- Utils/
    |-- Logger.cs
    `-- TextViewExtensions.cs
```

## Privacy and Security

- Runs locally inside SSMS
- Sends no telemetry
- Uses normal SQL metadata queries against the active connection
- Stores schema cache under `%LocalAppData%\OpenHintSQL\schemacache`
- Stores user settings under `%LocalAppData%\OpenHintSQL\settings.json`

Optional diagnostics:

```cmd
setx OPENHINTSQL_DEBUG 1
setx OPENHINTSQL_FILE_LOG 1
```

Disable disk cache:

```cmd
setx OPENHINTSQL_DISABLE_DISK_CACHE 1
```

## Troubleshooting

### Popup never appears

1. Confirm the extension files are under `<SSMS>\Common7\IDE\Extensions\OpenHintSQL`
2. Re-run `scripts\install.bat` as Administrator
3. Check `View -> Output -> OpenHint SQL`

### Tables or columns do not appear

1. Confirm the query window is connected to a database
2. Check the Output pane for schema-load messages
3. Open a fresh query window if the active editor was created before the connection was established

### Database names do not appear after `USE `

1. Confirm the query window is connected
2. Confirm the login can see `sys.databases`

### Schema is stale

Delete the cache files under:

```text
%LocalAppData%\OpenHintSQL\schemacache
```

## Contributing

- Target .NET Framework 4.8
- Test against both 32-bit SSMS (18-20) and 64-bit SSMS (21-22) when possible
- Keep JSON-related code localized and avoid introducing fragile early-load dependencies

## License

[MIT](LICENSE) © 2026 Jarvis
