# Etch

A fast, editor-first developer scratchpad for Windows.

You paste something into a tab. Etch works out what it is, and `Ctrl+Enter` does
the obvious thing to it — in place, so transforms chain. Everything is saved
continuously, so there is never a save dialog and never a lost thought.

> **Status: M0 — spike.** This is the go/no-go performance gate, not the product.
> There are no tabs, no persistence and no transforms yet. What exists is a shell
> that answers one question: *can WPF get on screen inside 250 ms, and can it hold
> a 50 MB file?* Full plan lives in `../Linda/Etch.md`.

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
src/Etch.Core        pure functions over text — no UI, no I/O, no platform
src/Etch.App         WPF shell, startup diagnostics, file loading
tests/Etch.Core.Tests   size policy, line-ending detection
tests/Etch.App.Tests    command line — the untrusted-input boundary
```

`Etch.Core` is where the value of the product will live, which is why it is kept
free of any UI or I/O dependency: it stays exhaustively testable without a GUI.
If a `PackageReference` or a `using System.Windows` ever appears in that project,
something has gone wrong.

## Build and test

```powershell
dotnet build Etch.slnx -c Release
dotnet test  Etch.slnx
```

## Measure (the point of M0)

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

It compiles. It has not been **measured**, and until it has, M0 has not happened —
the whole milestone is a number, not a build.

Runtime-only risks, which a successful build says nothing about:

1. **Mica behind the editor.** The editor background is transparent so the backdrop
   shows through. If text readability suffers, especially in light mode, swap it for
   a solid theme brush — a one-line change in `MainWindow.xaml`.
2. **Theme resource keys** are `DynamicResource` lookups, so a wrong key degrades
   silently rather than throwing. Check the status bar actually looks right in both
   light and dark.
3. **AvalonEdit at 50 MB.** Unverified behaviour, and the reason M0 exists.

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
means usernames and directory names. They roll at 4 MB and will come under M1's
"wipe all scratch data" command.

Session data will be plaintext under `%LOCALAPPDATA%` when persistence lands in M1.
People paste secrets into scratchpads, so that will ship alongside per-tab
ephemeral buffers and a "wipe all scratch data" command.
