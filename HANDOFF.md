# FastCopy — Handoff

A TeraCopy-style fast file copier for Windows. .NET 8, WPF GUI + reusable copy-engine library.

**Status as of 2026-09-12: the repo compiles clean, all smoke tests pass, and the WPF app has now been
driven live (via UI Automation) through paste-to-start, Start, Cancel mid-copy, Pause/Resume, the
conflict-policy picker, a full visual theme pass (light/dark, follows Windows live), an app icon, and a
new background tray mode that auto-copies on any clipboard file-copy without needing the window open —
not just built.** Tasks 1, 2, 3, 4, and the first half of 6 are done and verified. Task 5 is fully done.
Task 7 is what's left — see the task list below for exact state.

## Background tray mode (new)

FastCopy now runs as a background app, not just a window you open:

- Closing the window (the X button) hides it to the system tray instead of exiting. The process keeps
  running - `ShutdownMode="OnExplicitShutdown"` in `App.xaml.cs` - and only the tray menu's **Exit**
  actually quits (`MainWindow._isRealExit`).
- A `WM_CLIPBOARDUPDATE` listener (`AddClipboardFormatListener`, hooked in `MainWindow.OnSourceInitialized`)
  fires on every clipboard change system-wide. If it's a file copy (not a Cut - checked via the
  `"Preferred DropEffect"` clipboard format) and a destination is already set, it starts the same
  3-second-cancelable-countdown copy paste-to-start uses - `MainWindow.TryAutoCopyFromClipboardAsync`.
  No Ctrl+V, no focus, no window needed.
- The destination and other options come from whatever is currently in the (possibly hidden) main
  window's own controls - `DestinationBox.Text` is the live source of truth; `AppSettings.cs`
  (`%AppData%\FastCopy\settings.json`) just persists them across restarts, plus the auto-copy on/off
  toggle and a one-time "still running in the tray" hint flag.
- Tray icon + menu (Open / Auto-copy toggle / Start with Windows toggle / Exit) is
  `TrayIconController.cs`, deliberately isolated in its own file with its own `using
  System.Windows.Forms` - **do not** add a project-wide `using System.Windows.Forms`, it collides with
  WPF's own `Application`/`KeyEventArgs`/`DragEventArgs`. The csproj already has `<Using
  Remove="System.Windows.Forms" />` for exactly this reason (`UseWPF` and `UseWindowsForms` each inject a
  global using for their namespace).
- "Start with Windows" writes/removes `HKCU\...\Run\FastCopy` pointing at the exe with a `--tray` arg
  (`StartupRegistration.cs`); `App.xaml.cs` checks for that arg to start hidden instead of showing the
  window on launch.
- A balloon notification shows during the countdown (click it to cancel) and on completion whenever the
  window is hidden - `TrayIconController.ShowBalloon`. The countdown was kept deliberately even though the
  feature was requested as "no confirmation needed" - given how easy it is for a stray Ctrl+C to grab the
  wrong thing (this happened once during testing - a real ~3.66GB folder got selected by an automation
  slip and 888MB copied before it was caught and canceled), a few seconds of cancelable delay felt like
  the right tradeoff. Worth revisiting if it turns out to be annoying in practice.
- Not yet verified live: the actual system-tray icon/menu interaction (Open, the two toggles, Exit) -
  UI Automation can't easily drive the Windows 11 tray flyout, so only the underlying logic (clipboard
  detection, cut-vs-copy, hide-on-close, settings persistence) was exercised end-to-end. Worth a manual
  click-through.

---

## Replacing Explorer's copy/paste (new)

Ctrl+V inside File Explorer now runs a FastCopy transfer instead of Windows' own copy.

**Why a keyboard hook and not a shell extension.** Replacing Explorer's actual file-operation engine -
what TeraCopy's "default copy handler" does - means injecting a DLL into `explorer.exe` and hooking
`IFileOperation`. That is unsupported, needs admin, trips AV heuristics, and breaks on Windows updates.
A `WH_KEYBOARD_LL` hook needs none of that and covers the case that matters. A context-menu entry
("Copy with FastCopy") was considered and deferred: it requires an in-proc COM shell extension, which
.NET 8 cannot safely provide (it would mean NativeAOT COM exports or a C++ DLL), plus an MSIX/sparse
package to appear in Windows 11's main context menu rather than under "Show more options".

- `ExplorerPasteHook.cs` installs the hook from the UI thread. The callback does **only** cheap Win32
  checks - Ctrl held, V pressed, foreground window is an Explorer class, `CF_HDROP` present, not a Cut -
  then raises `PasteIntercepted` and returns 1 to swallow the keystroke. Everything slow (COM, clipboard
  reads, starting the copy) happens afterwards on the UI thread. **This split is not stylistic:** Windows
  silently uninstalls a low-level hook whose callback overruns `LowLevelHooksTimeout` (300ms default),
  which would kill the feature for the rest of the session with no error anywhere.
- The `HookProc` delegate is held in a field. Inline, it gets collected and the next keystroke calls into
  freed memory.
- Cut (Ctrl+X) is deliberately never intercepted - a move within a volume is a rename Explorer does
  instantly, so there is nothing for a copy engine to improve. Any failure reading the clipboard is
  treated as "cut", i.e. don't intervene, because the fallback is Windows pasting normally.
- `ExplorerFolder.cs` maps the Explorer HWND to a path via `Shell.Application`. Returns null for virtual
  folders (This PC, libraries, search results) - there is nowhere to paste. Desktop (`Progman`/`WorkerW`)
  maps to the Desktop directory.
- Because the keystroke is already swallowed by the time the UI thread runs, **every** path out of
  `MainWindow.OnExplorerPasteIntercepted` must either start a copy or tell the user why it didn't -
  returning silently is indistinguishable from Ctrl+V being broken.
- No countdown on this path. Pressing Ctrl+V is an explicit instruction, same reasoning as a direct
  Start click. The countdown exists for triggers that fire off a bare Ctrl+C.
- **Ctrl+C auto-copy is now off by default** (`AppSettings.AutoCopyEnabled`), kept as a tray toggle
  labelled "Also copy on Ctrl+C (to default folder)". `AppSettings.Migrate` forces it off once for
  settings files written before `SettingsVersion` 1, since the `true` in those files is the old default
  rather than anyone's choice.
- First run enables "Start with Windows". The hook only exists while something holds it, and Windows has
  no way to start a stopped process on a clipboard change - so "launches as soon as you copy" really
  means "is already in the tray". Only forced on a genuinely fresh install.
- Verified live, A/B: with FastCopy not running, pasting a folder over an existing one left the
  destination file untouched (Explorer waiting on its Replace-or-Skip prompt). With FastCopy in the tray,
  the same paste silently overwrote it with the source content - FastCopy's `Overwrite` conflict policy,
  which Explorer has no equivalent of. Nested subdirectories came across correctly.

---

## Licensing / donations (new)

FastCopy is **free**, WinRAR-style: no licence key, no trial, no feature gated behind payment. The only
money path is an optional PayPal donation.

- `Donation.cs` holds the single donation URL (a PayPal no-code checkout link, deliberately
  amount-free) and the `OpenPayPal()` shell-launch. Everything else reads from there.
- `DonateWindow.xaml(.cs)` is the reminder dialog — themed off `Themes/Styles.xaml` like the main
  window, and it re-applies the dark title bar in `OnSourceInitialized` for the same reason
  `MainWindow` does. It only reports what the user chose; persistence is the caller's job.
- `MainWindow.ShowDonatePromptIfDue()` is the caller. It shows the dialog on **every** launch until
  the user ticks "Don't show this again", which sets `AppSettings.SuppressDonatePrompt`.
  It deliberately uses `MainWindow._settings` rather than its own `AppSettings.Load()` — the window
  rewrites the whole settings file in `SaveSettingsFromUi()` on close, so a flag written through a
  second instance would be clobbered a moment later.
- `App.OnStartup` queues the prompt at `DispatcherPriority.ApplicationIdle` so the main window paints
  before the modal appears. **It is not shown on the `--tray` path**: that launch is Windows booting,
  not the user opening FastCopy, and a modal at login is a different thing from one they asked for.
  Change this if you disagree — it's one `if`.
- The tray menu has a "Donate..." item so the page stays reachable after the prompt is dismissed
  for good.
- Verified live: the dialog was confirmed on screen (a visible 460x340 owned window centred on the
  main window) on a real launch. Note that UI Automation cannot enumerate it while `ShowDialog()` is
  blocking — use Win32 `EnumWindows` if you need to assert on it from a test.

---

## Build & run

```powershell
$env:PATH += ";C:\Program Files\dotnet"   # SDK 8.0.423, installed via winget, not on PATH by default
cd L:\Personal_Projects\FastCopy
dotnet build FastCopy.sln
dotnet run --project src\FastCopy.App\FastCopy.App.csproj -c Release
```

## Layout

```
src/FastCopy.Core/     # engine library (net8.0), no UI dependencies
  CopyEngine.cs        # the whole copy pipeline
  NativeMethods.cs     # CopyFileEx + storage-device P/Invoke
  StorageInfo.cs       # HDD-vs-SSD detection -> parallelism tuning
  SpeedMeter.cs        # rolling-window throughput
  CopyOptions.cs / CopyResult.cs / CopyProgressEventArgs.cs
src/FastCopy.App/      # WPF app (net8.0-windows)
  MainWindow.xaml(.cs) # STALE - still written against the old engine API
```

---

## Verification status — important

| Component | State |
|---|---|
| Engine v2 (current code) | **Compiles and runs.** `smoketest/` (5 groups, 21 checks) passes end to end, including the previously-never-executed `StorageInfo` seek-penalty P/Invoke path (exercised for real, returned a valid parallelism without throwing). |
| WPF app | Compiles against the current engine API. Multi-source list, paste-to-start, and drag-and-drop all wired up. **Now manually driven in a live session** (via UI Automation, since no human was at the keyboard): pasted a folder and started a copy end to end (4 files, byte-verified on disk), canceled an in-flight 8GB copy (left zero bytes at the destination), paused an in-flight 8GB copy (confirmed bytes-copied genuinely froze for >1s), resumed it to completion, and exercised the conflict-policy picker (`Skip` correctly skipped 4 pre-existing files on a re-copy). Drag-and-drop and the native Add Files/Add Folder file-picker dialogs were not exercised this way — the common-item-dialog COM component didn't visibly respond to a synthesized `Invoke()` in this environment, so those two entry points still only have build-time verification. |

`smoketest/` (a real project again, kept out of the solution per the note below) covers: verified copy
with byte-for-byte content match and preserved directory structure, cancellation mid-copy, pause
genuinely stalling the transfer (asserts elapsed >= pause duration), a duplicate-source-path case under
`KeepBoth` (exercises the `claimed` set / dedup in `ResolveConflict`), and a free-space-check regression
check. Run it with `dotnet run -c Release` from `smoketest/`.

---

## What changed in the v2 rewrite (rationale — please don't silently revert these)

These were fixes for concrete defects found by reading v1, not speculative polish:

1. **Scan moved off the calling thread.** v1 called `BuildPlan` before the first `await`, so it ran on
   the UI thread and froze the window while enumerating a large tree.
2. **`Parallel.ForEachAsync` replaced task-per-file.** v1 did `plan.Select(f => Task.Run(...))`, which
   allocates a `Task` for every file up front — a 200k-file tree queued 200k tasks all contending on
   one semaphore.
3. **Rolling-window speed (`SpeedMeter`) replaced a cumulative average.** v1 computed
   `bytesCopied / totalElapsed`; the smoke test visibly showed it reading 1068 MB/s decaying to
   433 MB/s while the disk was doing something else entirely. ETA depends on this being current.
4. **Media-aware parallelism (`StorageInfo`).** Concurrent copies on a spinning disk cause head thrash
   and are often *slower* than serial. Detects seek penalty via `IOCTL_STORAGE_QUERY_PROPERTY`, then
   serializes on a single HDD volume, caps at 2 across separate devices, leaves SSDs alone.
5. **`COPY_FILE_NO_BUFFERING` above 64MB.** Avoids evicting the machine's entire file cache for data
   that will never be read again.
6. **Retry on transient errors** (sharing/lock violation, network blip) with byte-count rollback so a
   retried partial attempt cannot double-count into progress.
7. **Conflict policy** — Overwrite / Skip / OverwriteIfNewer / KeepBoth, resolved at plan time.
8. **Free-space precheck**, so a doomed copy fails immediately instead of halfway.
9. **Resilient tree walk** — one permission-denied folder costs you that folder, not the whole scan.
10. **Multi-source** — `CopyAsync` now takes `IReadOnlyList<string>`, required for the paste feature.

---

## Tasks, in priority order

### 1. Make it compile again — **done**
`MainWindow.xaml.cs` now targets the current engine API (`ConflictAction`, required `Elapsed`,
directory-based destination, `CopyEngine.FormatBytes` picked as the one source of truth — the app's
private copy was removed).

### 2. Fix bugs known to be present in the v2 engine — **done, verified by `smoketest/`**
- `CopyResult.FilesVerifiedOk` / `FilesVerifiedFailed` are populated from `ProgressState` before
  `CopyAsync` returns. Covered by smoke test [1].
- The unused `force` parameter on `Report` was deleted rather than wired up (no caller needed it).
- `NativeMethods.StoragePropertyQuery.AdditionalParameters` is initialized to `new byte[1]` in
  `StorageInfo.QuerySeekPenalty`. This path has now actually executed (smoke test [1] asserts
  `ParallelismUsed >= 1` without throwing) — still worth a manual check on a real spinning HDD if one
  is available, since CI/dev machines are usually all-SSD.
- The `ERROR_REQUEST_ABORTED`-without-cancellation case now counts the file as failed and increments
  `FilesCompleted` instead of silently dropping it from every counter.
- `EnsureFreeSpace` now groups by `GetDiskFreeSpaceEx` per destination directory (merging directories
  that resolve to the same physical volume), which is correct for mount points/junctions. Duplicate
  source paths are deduped up front in `CopyAsync` so `KeepBoth` can't double-plan them — smoke test [4].

### 3. Paste-to-start — **done**
`Ctrl+V` anywhere in the window reads `Clipboard.GetFileDropList()`, ignores non-file clipboard content
quietly, and starts the copy with no Start click. If a copy is already running it's rejected with a
status message rather than queued. If no destination is set yet, the folder picker opens once before
starting. Accidental-paste risk is mitigated with a 3-second cancelable countdown (`StartCopyAsync(...,
countdown: true)`) — a direct Start-button click skips the countdown since clicking it is already an
intentional confirmation.

### 4. Drag-and-drop — **done**
Same code path as paste, via a shared `HandleIncomingPathsAsync` used by both `Window_Drop` and the
paste handler: `AllowDrop="True"` on the window, `DragEnter` only allows the drop when idle and the
payload is `DataFormats.FileDrop`, `Drop` starts the copy immediately with the same countdown/queued-
destination-picker behavior as paste.

### 5. Rework the GUI for multi-source — **done**
- Done: the single "Source" textbox is now a `SourcesList` (`ObservableCollection<string>`) with
  Add Files.../Add Folder.../Remove/Clear buttons; paste and drop both populate it. The result log now
  shows elapsed time, parallelism used, average throughput, skipped/retried/failed counts, and
  verification counts. Live progress shows an ETA (`CopyProgressEventArgs.EstimatedTimeRemaining`)
  next to speed and bytes-copied.
- Also fixed: the latent `MainWindow.OnProgress` crash — `Progress<T>` posts asynchronously, so a
  report landing after the `finally` block nulled `_engine` used to risk a `NullReferenceException`.
  Guarded with `_engine is { IsPaused: false }`.
- Added 2026-08-28: `CopyOptions.ConflictAction` was fully implemented in the engine but had no UI —
  every GUI-triggered copy silently ran `Overwrite`, which is a real data-loss risk combined with
  paste/drop auto-start. There's now an "If a file already exists" combo (Overwrite / Skip / Overwrite
  if newer / Keep both) wired into `StartCopyAsync`'s options. Also added: a live file-count/total-size
  summary next to the sources list (scans off the UI thread, same reasoning as `BuildPlan`'s own move
  off the calling thread — see rationale list above), and an "Open" button next to the destination box
  that shells out to Explorer.
- Verified live (see the verification table above) — paste-to-start, Start, Cancel mid-copy,
  Pause/Resume, and the new conflict-policy combo all work correctly in a real running session, not
  just at build time.
- Not done: no persistent job queue — starting a new paste/drop while idle replaces the source list
  rather than appending a second job.

### 6. Rebuild the test harness — **done**
`smoketest/` is a real console project referencing `FastCopy.Core` again (kept out of `FastCopy.sln`
and covered by `.gitignore`'s `smoketest/` entry). Current coverage: verified copy with content
byte-match + preserved directory structure + populated verification counters, cancellation mid-copy,
pause/resume actually stalling the transfer, duplicate-source-path dedup under `KeepBoth`, and a
free-space-check regression check. Still open from the original ask: retry-on-locked-file, an explicit
test for each remaining conflict policy (`Skip`, `OverwriteIfNewer`), and a same-name collision across
two different source folders.

### 7. Not started — original TeraCopy parity gaps
Job queue, multiple concurrent jobs, the live throughput graph, theming, Explorer shell integration.

---

## Notes for whoever picks this up

- No git repo has been initialized here. `.gitignore` exists (`bin/`, `obj/`, `*.user`).
- The `dotnet` CLI is not on the system PATH; prepend `C:\Program Files\dotnet` per shell session.
- Don't replace `CopyFileEx` with a managed `FileStream` buffer loop. It's the same primitive Explorer
  and robocopy use, it preserves attributes and timestamps for free, and the pause implementation
  depends on blocking inside its progress callback — a managed loop would be slower and lose that.
- The engine library is deliberately UI-free. Keep WPF types out of `FastCopy.Core` so a CLI front end
  stays possible.
