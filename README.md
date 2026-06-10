# mdpad

mdpad is a Windows Notepad-style markdown editor that stores notes in SQLite first and uses normal files as an optional sync target.

## Features

- WPF desktop shell with WebView2.
- CodeMirror 6 editor with `@codemirror/lang-markdown`.
- SQLite note store in `%LOCALAPPDATA%\mdpad\mdpad.db`.
- Daily note organization with an automatic `scratch` tab for each day.
- Multiple titled tabs per day.
- Tags, date navigation, recents, and free search in the sidebar.
- System light/dark preference by default, with explicit light/dark/system setting.
- Open any file into today's database notes.
- Autosave all editor changes to SQLite.
- Explicit `Save`/`Ctrl+S` and `Save As` for writing file-backed notes to disk.
- Closing an editor tab removes that note record from SQLite.

## Build

```bash
cd src/MdPad.Web
npm install
npm run build

cd ../..
dotnet restore
dotnet build
```

The desktop project targets `net6.0-windows10.0.19041.0` and is intended to run on Windows with the WebView2 runtime installed.
