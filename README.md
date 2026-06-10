# mdpad

mdpad is a Windows Notepad-style markdown editor that stores notes in SQLite first and uses normal files as an optional sync target.

## Features

- Cross-platform Photino desktop shell: WebView2 on Windows and WebKitGTK on Linux.
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

The desktop project targets `net10.0`. The repo pins SDK `10.0.301` in `global.json`.

## Linux / WSLg

Photino uses WebKitGTK on Linux. On Ubuntu 22.04 / WSL install:

```bash
sudo apt-get install libwebkit2gtk-4.0-37
```

Then run:

```bash
dotnet run --project src/MdPad/MdPad.csproj
```

## Windows

On Windows the same project uses WebView2. Install the .NET 10 SDK and the WebView2 runtime, then build and run from the repo root.
