# M0 - measurement protocol

M0 is a go/no-go gate. The decision is: **can WPF hold the §4 budgets?** If it
cannot, the stack gets rethought before anything is built on top of it. That only
works if the numbers are trustworthy, so the procedure matters as much as the
result.

## Budgets under test

| Scenario | Budget | Measured by |
|---|---|---|
| Cold start → window interactive | **≤ 250 ms** (≤ 500 ms truly cold) | `Etch.exe --diag` |
| Load 1 MB → first frame | ≤ 100 ms | `--diag <1MB file>` |
| Keystroke → frame | ≤ 16 ms | observed, not instrumented in M0 |
| Idle working set | ≤ 120 MB | `--diag` reports working set and retained memory |
| Idle CPU | **0%** | Task Manager, 60 s untouched |

The 20-tab restore and the 10 MB format budgets belong to M1 and M2. They are out
of scope here.

**The 1 MB budget in §4 of the plan is for a *paste*, and this is not that.**
Pasting goes through AvalonEdit's document-replace path and onto the undo stack;
loading builds a fresh `TextDocument` and skips both. The load number is a lower
bound on the paste number, not a substitute for it. Instrumenting paste properly
belongs with M1, when there is a buffer worth pasting into.

### How precise are these numbers?

Not as precise as the report's two decimal places suggest. The largest slice -
`process start → managed Main` - is derived from `Process.StartTime`, which the
kernel records on a system-clock tick, nominally every 15.625 ms. Everything after
managed entry is `Stopwatch`-accurate.

So treat the total as **±16 ms**. A run at 240 ms and a run at 260 ms are the same
result. Anything inside roughly 20 ms of the budget is not a decision - it is a
prompt to reduce the noise (close things, plug in, more runs) and measure again.

## Before measuring

Measure the artifact users launch, not a development build.

```powershell
dotnet publish src\Etch.App\Etch.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -o artifacts\win-x64
```

Then:

- Plug the laptop in. Battery saver parks cores and will cost 50–100 ms.
- Close anything that hammers the disk (indexers, sync clients, antivirus scans).
- Add `artifacts\` to the Defender exclusion list, or accept that the first launch
  of a fresh binary pays a full AV scan and treat that run as discardable.

## Startup

Ten runs, discarding the first:

```powershell
1..10 | ForEach-Object { artifacts\win-x64\Etch.exe --diag; Start-Sleep -Milliseconds 800 }
```

Close the window between runs. Then read
`%LOCALAPPDATA%\Etch\diag\etch-<date>.log` and take the **median** and the
**p95** of the `= window interactive` line. The plan's budget is p95, warm file
cache - the median is only there to show how noisy the machine is.

The report separates three things, and which one is over budget determines what
to do about it:

| Line | What it is | If it dominates |
|---|---|---|
| `process start → managed Main` | Host startup, runtime init, single-file bundle probing, AV | Floor of the platform. Check R2R is applied; verify the `jit` line shows few compiled methods. |
| `xaml-resources` | WPF-UI's themes + controls dictionaries | The one genuinely cuttable cost. Merge fewer dictionaries, or build a trimmed theme. |
| `window-constructed` → `interactive` | Window layout, AvalonEdit construction, first render | Simplify the visual tree; check the status bar is not forcing layout twice. |

`jit` is the tell for ReadyToRun. A large compiled-method count on an R2R build
means the pre-compiled code is being rejected - usually a RID mismatch or a
framework version the R2R images were not built against.

### Truly cold

The 500 ms budget is for a cold file cache. The only honest way to produce one is
to reboot and measure the first launch. Do that once; it is a sanity check, not a
number to average.

## Large file

```powershell
artifacts\win-x64\Etch.exe --gen-sample samples\big.ndjson --size 50 --force
artifacts\win-x64\Etch.exe --diag samples\big.ndjson
```

The fixture is generated from a fixed seed, so the file is byte-identical on every
machine and across every run. `--gen-sample` refuses to replace an existing file
unless `--force` is passed. Repeat at `--size 1`, `5`, `50`, and `120` to walk each
degradation tier.

The log splits the load into three numbers, because they are fixed in completely
different ways:

- **read** - off the UI thread. Bounded by disk and decoding. The UI stays live.
- **constructed** - UI thread. Rope build plus the property assignments. This is
  cheap and not the interesting number.
- **first frame** - UI thread, measured through to the first idle *after* the
  render pass. Includes AvalonEdit's first layout and line-number margin sizing.
  **This is the number that decides whether AvalonEdit is viable at 50 MB.**

Two memory figures are reported. **working set** is the process footprint at that
instant and will look alarming: the decoded string, the rope, and whatever the read
buffer left behind are all still live. **retained** is taken after a forced full
collection and is the one to compare against the 120 MB budget. A 50 MB ASCII file
is ~100 MB of UTF-16 by definition, so retained should land near that plus
AvalonEdit's overhead; wildly above means a second copy is being held.

At `--size 120` the file should be refused outright, with an explanation in the
status bar and no partial load. Etch also re-checks the size against the handle it
actually opened, so a file that grows past the ceiling mid-read is truncated rather
than allowed to exhaust memory - if that happens the status bar says **TRUNCATED**
in as many words.

## Idle CPU

Launch, wait for the startup message in the status bar to clear (a one-shot timer
is live until it does, by design), then leave the window focused and untouched for
60 seconds and watch it in Task Manager. Anything other than a flat 0% after that
means a timer is running that should not be. Repeat unfocused - a background poll
is just as disqualifying.

## Recording the outcome

Write the result into `../Linda/Etch.md` under a new "M0 results" section: the
median and p95 startup, the phase split, the 50 MB read/render split, peak working
set, and the go/no-go call with its reasoning.

If the answer is no-go, the useful output is *which phase* blew the budget. That
determines whether the fix is cutting XAML, or abandoning WPF.
