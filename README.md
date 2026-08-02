<p align="center">
  <img src="assets/etch-256.png" alt="Etch" width="120">
</p>

# Etch

A fast, editor-first developer scratchpad for Windows.

You paste something into a tab. Etch works out what it is, and `Ctrl+Enter` does
the obvious thing to it — in place, so transforms chain. Everything is saved
continuously, so there is never a save dialog and never a lost thought.

> **Status: M2 complete.** Everything from M1 — tabs, continuous auto-save, session
> restore, non-destructive close, reopen-closed, find and replace, per-tab ephemeral
> buffers — plus format detection, the command palette, and the **42 transforms** of
> the v1 catalogue. M3 is polish and release: large-file modes, a settings UI, syntax
> highlighting, and the measurement this project has still never run.
> Full plan lives in `../Linda/Etch.md`; **its §0 says what to do next.**

## Stack

| Piece | Choice |
|---|---|
| Framework | .NET 10, WPF |
| Fluent shell | [WPF-UI](https://github.com/lepoco/wpfui) 4.3.0 |
| Editor | [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) 6.3.1 |
| Tests | xUnit v3 |
| Licence | MIT |

## Layout

```
src/Etch.Core          pure functions over text — no UI, no I/O, no platform
src/Etch.Persistence   every byte Etch writes: buffers, trash, session index, journal
src/Etch.App           WPF shell, tabs, find/replace, single-instance, diagnostics
tests/Etch.Core.Tests         detection corpus, every transform, palette ranking, search
tests/Etch.Persistence.Tests  atomic writes, retention, crash recovery, the journal
tests/Etch.App.Tests          workspace orderings, keyboard map, tab order, editor colours
assets                 the icon: SVG sources, the packed .ico, and build-icon.py
```

The icon is drawn from the SVGs in `assets/`, not traced from a raster, and
`assets/build-icon.py` packs the ten sizes Windows asks for — rendering each one at its
own resolution rather than downsampling. `Etch.App.csproj` embeds `assets/etch.ico` in
the executable; there is no second copy to fall out of sync.

`Etch.Core` is where the value of the product will live, which is why it is kept
free of any UI or I/O dependency: it stays exhaustively testable without a GUI.
If a `PackageReference` or a `using System.Windows` ever appears in that project,
something has gone wrong.

## Build and test

```powershell
dotnet build Etch.slnx -c Release
dotnet test  Etch.slnx
```

## Measure

Publish the way it will actually ship, then measure that — a debug build through
`dotnet run` is not the thing users launch:

```powershell
dotnet publish src\Etch.App\Etch.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -o artifacts\win-x64
```

Startup timeline:

```powershell
artifacts\win-x64\Etch.exe --diag
```

Large file, generated from a fixed seed so the test is reproducible:

```powershell
artifacts\win-x64\Etch.exe --gen-sample samples\big.ndjson --size 50
artifacts\win-x64\Etch.exe --diag samples\big.ndjson
```

`--gen-sample` will not replace an existing file unless you pass `--force`, and it
writes through a temporary file so a cancelled run leaves nothing behind.

Diagnostics print to the terminal *and* append to
`%LOCALAPPDATA%\Etch\diag\etch-<date>.log`, so a run launched from Explorer or one
that dies early still leaves the numbers behind.

The full protocol — how many runs, warm versus cold cache, and what each number
means — is in [`docs/M0-measurement.md`](docs/M0-measurement.md).

## Keyboard

| Key | Action |
|---|---|
| `Ctrl+T` / `Ctrl+N` | New scratch tab |
| `Ctrl+W` | Close tab — never destructive |
| `Ctrl+Shift+T` | Reopen the last closed tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab |
| `Ctrl+PageDown` / `Ctrl+PageUp` | Next / previous tab |
| `Ctrl+1..9` | Jump to a tab by position |
| `Ctrl+O` / `Ctrl+S` | Open a file / write through (or give a scratch tab a home) |
| `Ctrl+F` / `Ctrl+H` | Find / find and replace |
| `Ctrl+Enter` | Do the obvious thing to whatever is in the buffer |
| `Ctrl+Shift+P` | Command palette — everything else |
| `F2` | Rename the tab, in place |
| `Ctrl+Shift+E` | Toggle ephemeral — this tab is never written to disk |
| `Ctrl+K, P` | Pin or unpin the tab |
| `Esc` | Dismiss the palette or the find bar |

Everything else the text area does — cut, copy, paste, undo, redo, select all, the
caret and selection keys, `Tab` for indentation — is AvalonEdit's and is deliberately
left alone. The whole map is one table in `Etch.App.Input.KeyMap`, and
`KeyMapTests` asserts both that it matches this list and that it claims nothing the
editor owns.

## Tabs

The strip lives in the title bar. Nothing else does — no menu, no ribbon, no toolbar.

- **Drag a tab** along the strip to reorder it. Pinned tabs and ordinary tabs are
  separate groups, so a drag never pins or unpins anything as a side effect.
- **Right-click a tab** for pin, rename, ephemeral and close. `Ctrl+K, P` pins from the
  keyboard.
- **Pinned tabs** sit at the front of the strip and carry a pin glyph. The order
  survives a restart.
- A **caution dot** marks an ephemeral tab, which is never written to disk.
- The strip **scrolls on the wheel** when there are more tabs than fit. There is no
  scrollbar: `Ctrl+Tab` and `Ctrl+1..9` reach everything regardless.

## Transforms

Etch works out what a buffer is and puts the right action under `Ctrl+Enter`.
Transforms apply **in place**, so they chain: base64 → JSON → sorted keys is three
keystrokes in one buffer. With a selection, only the selection is transformed. Each
transform is a single undo.

Detected: JSON, NDJSON, base64, base64url, hex, URL-encoded, JWT, GUID, Unix time,
ISO-8601. The status bar names what it found, and says "(sampled)" when the document was
large enough that only its first 64 KB was examined.

**42 transforms** in the v1 catalogue — JSON format/minify/validate/sort-keys and string
escaping; base64, base64url, URL and HTML-entity encoding both ways; hex to text; JWT
decode; MD5, SHA-1, SHA-256, SHA-512 and a GUID generator; Unix time ↔ ISO-8601 and UTC ↔
local; six case conversions; and the line and whitespace operations (sort, reverse,
dedupe, blank lines, join, split, trim, collapse, tabs ↔ spaces, indent, dedent).
`Ctrl+Shift+P` fuzzy-searches all of them.

| `Ctrl+Enter` does | when the buffer is |
|---|---|
| Format JSON | JSON |
| Base64 decode | base64, base64url |
| URL decode | URL-encoded |
| Hex to text | hex |
| Decode JWT | JWT |
| Unix time to ISO-8601 | Unix time |
| ISO-8601 to Unix time | ISO-8601 |

Encoding and hashing are never the suggested action: decoding is something the buffer
tells you it needs, encoding is something you go looking for. Which transform wins a tie
is decided by an explicit precedence, not by the alphabet — until that landed, `Ctrl+Enter`
on JSON chose "Format" over "Minify" because F comes before M.

Encoding transforms are never the suggested action: decoding is something the buffer
tells you it needs, encoding is something you go looking for.

**JWT tokens are decoded, never verified.** The output says so on its first line, and
that is not decoration — a tool that renders claims as though they were established
facts teaches people to trust attacker-controlled input.

## Where your text lives

```
%LOCALAPPDATA%\Etch\
├─ session.json          tab order, titles, caret and scroll positions
├─ .lock                 held by the running instance
├─ buffers\<guid>.txt    one file per tab, raw UTF-8
├─ buffers\<guid>.prev   the previous revision of each, kept as a safety net
└─ trash\<guid>.txt      closed tabs, kept 7 days
```

There is no save dialog because there is nothing to save: every edit is written
about half a second after you stop typing, and at least every five seconds while
you keep going. Closing a tab moves it to `trash\`, which is what makes closing
safe to do without a confirmation prompt.

**Only one Etch runs per data directory.** Two would journal to the same files and
overwrite each other with no error anywhere. A second launch hands its file to the
window already open and exits.

## Command line

```
Etch [file]                Open a file.
Etch --diag                Print the startup timeline and continue.
Etch --gen-sample <path>   Write a synthetic file, then exit.
       [--size <MiB>]      Target size. Default 50, max 2048.
       [--shape json|text] Content shape. Default json.
       [--force]           Replace an existing file at that path.
Etch --help
```

## Design decisions worth knowing

**Etch owns its own `Main`.** Clearing `EnableDefaultApplicationDefinition` leaves
`App.xaml` to be picked up by the default `Page` glob, so the SDK generates
`InitializeComponent` but no `Main` of its own. That lets `Program.Main` start the
clock *before* the `Application` object and its XAML resource dictionaries exist.
Those dictionaries are the largest controllable cost in a WPF-UI startup; a
timeline that cannot see them separately cannot tell you whether to cut them.

**Single-file compression is off, permanently.** It trades startup time for file
size by decompressing on every launch. Etch sells startup time.

**No trimming.** WPF resolves types from XAML by reflection, so trimming breaks it
in ways that only surface at runtime. ReadyToRun is where the win is; NativeAOT is
not available for WPF at all.

**The theme is applied, not guessed.** The system theme is read straight from the
registry, then handed to WPF-UI unconditionally — skipping the call when it already
matches would save a resource merge but also skip the DWM dark-mode window
attribute that comes with it, leaving a light frame around dark content. The cost
shows up as its own phase in the timeline.

**No polling timers.** Idle CPU is a budget, not an aspiration. The one
`DispatcherTimer` is created on first use, is one-shot, and stops itself.

**The size ceiling is enforced on the handle being read**, not on a `FileInfo`
snapshot taken earlier, and the read is capped rather than unbounded. A log file
another process is still appending to is the most likely input this editor sees and
the one most able to win that race.

## Not yet verified

Etch has still never been **measured**. The M0 startup gate was answered by
judgement rather than by `--diag`, so there is no baseline, and M1's cost can no
longer be separated from M0's. The protocol in `docs/M0-measurement.md` is still
worth running, as a baseline rather than as a gate.

Runtime-only risks, which a successful build says nothing about:

1. **Theme resource keys** are `DynamicResource` lookups, so a wrong key degrades
   silently rather than throwing. Check the tab strip and status bar actually look
   right in both light and dark.
2. **AvalonEdit at 50 MB.** Still unverified, and still the risk M0 was built to
   answer.
3. **Idle CPU and idle working set** have never been checked against the budget.

Mica behind the editor used to be listed here as a readability risk, and it was a real
one: AvalonEdit's default selection is the system highlight at 70% opacity, which over a
transparent editor composites against the *wallpaper*. It sank into the page in dark mode
and put white text on 70% blue — about 2.9:1 — in light. The selection and current-line
colours are now derived from the accent to stated contrast floors in
`Etch.App.Editor.EditorColours`, and `EditorColoursTests` sweeps 125 accents against both
themes. Ordinary text over Mica is still worth an eye.

## Security posture

Every buffer is untrusted input, even on the desktop. In M0 that means: paths from
the command line are resolved to absolute form, device paths (`\\.\`, `\\?\`) are
refused, nothing reaches a shell, hyperlink detection in the editor is off, reads
are bounded, and `--gen-sample` will not overwrite a file without `--force`.

Etch initiates no network requests — no telemetry, no update check, nothing.
Opening a UNC path performs SMB I/O exactly as any Windows file open does; that is
the user's request, not Etch reaching out. Command-line parsing deliberately
touches no file system at all, so a hostile path cannot block startup on a network
timeout before the window exists.

Diagnostic logs under `%LOCALAPPDATA%\Etch\diag` record absolute file paths, which
means usernames and directory names. They roll at 4 MB.

**Everything you type is stored as plaintext under `%LOCALAPPDATA%\Etch`.** People
paste credentials into scratchpads, so this is worth being precise about:

- Each buffer is stored **twice** — the current text and one previous revision
  (`.prev`), kept so that a bad write is recoverable. A wipe removes both.
- Because every revision is written to a fresh file and renamed into place, older
  copies of your text exist on disk until their blocks are reused. Deleting a file
  unlinks it; it is not a secure erase, and shadow copies keep whole prior versions.
- The honest answer for a real secret is not to write it at all. `Ctrl+Shift+E`
  marks a tab **ephemeral**: it is never journaled, never trashed, and never named
  in `session.json`.
- Trash retention is 7 days by default and can be set to 0.
- "Wipe all scratch data" exists in `Etch.Persistence` and is not yet wired to a
  command — that lands with the settings UI in M3.

Etch initiates no network requests — no telemetry, no update check, nothing. The
instance hand-off uses a named pipe restricted to the current user, and every path
that arrives over it is validated with the same rules the command line uses.
