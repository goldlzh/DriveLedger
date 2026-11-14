# C盘空间账本 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Deliver a working Windows x64 application that records C drive growth, explains directory and file changes, and installs an automatic background service.

**Architecture:** A C# core owns SQLite accounting and queries. A Windows layer reads volume metadata and USN events, maintaining the index through a resilient worker. A Chinese WPF tray application provides reports and authenticated local service controls. A self-contained distribution includes installation and removal commands.

**Tech Stack:** .NET 10 LTS, C#, Microsoft.Data.Sqlite 10.0.x, WPF and Windows Forms NotifyIcon, Windows ServiceBase, authenticated named pipes.

**Spec:** `docs/superpowers/specs/2026-11-14-drive-ledger-design.md`

## Global Constraints

- Monitor C NTFS only by default; do not read file contents or modify monitored files or their ACLs.
- Record whole-volume free space every 60 seconds; poll USN every 5 seconds; refresh continuously changing files within 60 seconds.
- First baseline must not count existing files as new growth. Indexing must not block disk sampling.
- File allocation is the accounting measure. Hard links count once. Same-volume moves are migrations and add zero whole-volume usage.
- Reparse points are recorded without traversing their targets. Unknown sizes, denied access, and historical gaps remain visible rather than estimated.
- Preserve real observation times, UTC and offset; never apportion a multi-day gap across missing dates.
- Store current index plus 30 days of file events and 365 days of disk samples and daily directory aggregates. Old directory reports must survive file event pruning.
- Default data directory is D:\CDriveSpaceMonitor\Data; failure there never silently falls back to C.
- Service controls permit only the installation account, SYSTEM, and local administrators. GUI runs without elevation.
- Ship a self-contained Windows x64 build, source archive, Chinese instructions, and measured verification results. Do not install a persistent production service merely to package the app.

## Review Focus

- A directory rename with descendants must not masquerade as newly allocated space; accounting and scanner tests cover it.
- An interrupted or incomplete scan must preserve unseen records unless deletion is positively known; core reconciliation tests cover it.
- A replayed USN batch must not duplicate growth and its checkpoint must commit with accounting; SQLite tests cover it.
- A stopped service and a missing D drive must produce an understandable GUI state without creating a replacement database on C; app smoke tests cover it.
- Full-day and long-gap reports must show actual observation windows, and 31-day-old folder totals must survive pruning; query tests cover them.

## Files and public boundaries

- `src/DriveLedger.Core/Models.cs`: typed observations, samples, checkpoints, status and report DTOs.
- `src/DriveLedger.Core/MonitorStore.cs`: schema, transactional observations, baseline and reconciliation, retention, queries.
- `src/DriveLedger.Core/Accounting.cs`: normalization, canonical link path, folder ancestry, display units and safe CSV.
- `src/DriveLedger.Windows/NativeMethods.cs`, `NtfsReader.cs`, `UsnJournal.cs`: read-only Win32 metadata, identities, allocation and journal parsing.
- `src/DriveLedger.Windows/Collector.cs`: initial scan, journal consumption, reconciliation, periodic samples and status.
- `src/DriveLedger.Windows/ControlPipe.cs`: bounded authenticated local pause/resume/rescan commands.
- `src/DriveLedger.App/App.xaml`, `MainWindow.xaml`, `MainWindow.xaml.cs`: Chinese reports, date range, drilldown, export and tray.
- `src/DriveLedger.App/Program.cs`, `ServiceHost.cs`, `Installer.cs`: mode routing, ServiceBase host, elevated installation and removal.
- `tests/DriveLedger.Tests/`: executable test harness with independently derived assertions, SQLite and controlled NTFS fixtures.
- `scripts/Build.ps1`, `scripts/Smoke.ps1`: repeatable package and application smoke checks.
- `README.md`, `docs/verification.md`: Chinese usage and measured results.

The core defines these stable entry points in Task 1. Later tasks must read their actual DTO definitions and keep the names/signatures intact:

```csharp
MonitorStore(string databasePath, bool readOnly = false)
void Initialize()
void ApplyBatch(IReadOnlyList<FileObservation> observations, IReadOnlyList<string> deletedIds,
    DateTimeOffset at, JournalCheckpoint? checkpoint = null, bool baseline = false, string? scanId = null)
void CompleteScan(string scanId, DateTimeOffset at, bool canInferDeletions, bool baseline)
JournalCheckpoint? ReadCheckpoint()
void RecordDiskSample(DiskSample sample)
void AddCoverageGap(string code, string message, DateTimeOffset start, DateTimeOffset? end = null)
void SetCollectorStatus(CollectorStatus status)
CollectorStatus ReadCollectorStatus()
ReportSnapshot QueryReport(DateTimeOffset from, DateTimeOffset to, string? folder = null)
void Prune(DateTimeOffset now)
```

`FileObservation` includes stable full file identity, all known paths, allocated and logical bytes, directory/reparse flags; unavailable allocation must be representable without becoming zero. `JournalCheckpoint` includes volume identity, journal ID, and next USN. `ReportSnapshot` includes actual disk observation endpoints, signed disk delta when two samples exist, attributed additions/releases/net, folder and file changes, samples, coverage notes and detail retention status. DTO field details are decided in Task 1 and documented in its report.

### Task 1: Core accounting and durable reports

**Files:** Core project and files, common build configuration, tests project and core tests.
**Consumes:** Design and global constraints.
**Produces:** The core public boundary above, compiling DTOs, SQLite schema, and a passing executable test suite.

- [x] Write minimal typed contracts and executable failing tests before implementing behavior. Independently checked fixtures: baseline A=100 bytes gives zero additions; then A=150 and new B=20 gives 70 added bytes; replaying the checkpoint still gives 70; deletion releases last known size; moving an unchanged allocation gives net zero; two hard links give one allocation; null allocation preserves prior known size.
- [x] Run `dotnet run --project tests/DriveLedger.Tests -c Release`; expect explicit failed assertions or NotImplemented behavior for those missing features, not syntax errors. Save RED output in the task report.
- [x] Implement transactional index changes, baseline marking, canonical link attribution and migration, folder rollups, disk reports, real observation windows, CSV escaping, scan marking, coverage notes and retention. Reconciliation tests must show incomplete scans do not fabricate deletions. Transactions commit checkpoint and index together.
- [x] Add and run query tests: a multi-day observation gap is visible; fewer than two samples gives unknown disk delta; 31-day-old directory totals survive file pruning; directory renames change attribution but never increase whole-volume allocation. Use a real temporary SQLite database.
- [x] Run the whole executable suite; expect all tests passing with exit code 0. Self-review and document public DTOs and any constraints for Task 2 and Task 3.
- [x] Commit and write the task report with RED and GREEN evidence.

### Task 2: Read-only Windows collector and authenticated controls

**Files:** Windows project and files, native and collector tests in test project.
**Consumes:** Task 1 public boundary and DTOs.
**Produces:** `Collector(MonitorStore store, CollectorOptions options)` with `Task RunAsync(CancellationToken)`, `Pause()`, `Resume()`, `RequestScan()`; `CollectorOptions` includes volume, intervals, optional fixture scan root and install account SID. `ControlPipeServer` and `ControlPipeClient` expose only pause/resume/rescan/status. Native public helpers support real file metadata and disk samples.

- [x] Write failing tests for valid and truncated USN V2 records, unsupported record versions, hard-link allocation de-duplication, actual created/grown/truncated files, and directory traversal that skips junction targets. Use task-owned temporary directories only.
- [x] Run focused tests and save expected RED evidence before the native implementation.
- [x] Implement safe Win32 handle lifetimes, full file IDs and volume GUID, actual allocation queries, hard-link names, USN state/query/read and bounds-checked parsing. Journal unavailability degrades to scans without enabling or resetting a journal. A bounded scan feeds batches and catches up journal changes before declaring a baseline ready.
- [x] Implement independent disk sampling, 5-second journal polling, dirty-file coalescing within 60 seconds, daily limited scans, shutdown cancellation, error/status persistence, restart checkpoint verification and gap detection. Scan cancellations never imply deletions. Resolve deleted objects through known index data. Add authenticated, bounded local control pipe ACLs that allow SYSTEM, administrators and the installing account, and deny network callers.
- [x] Run collector against a controlled fixture: establish baseline, create/grow/rename/delete a file, then restart and query the actual database. Check for expected accounting, no duplicate growth, visible unsupported-journal or pause gaps, and no writes outside the fixture data directory except ordinary build artifacts.
- [x] Run the full suite, self-review, commit and write report with measured fixture results and public collector/control API details.

### Task 3: Chinese desktop reports and installer

**Files:** App project and files, app smoke script, README and build script.
**Consumes:** Core reports, collector/control APIs, approved defaults.
**Produces:** WPF executable modes `--service`, `--collect`, `--install`, `--uninstall`, `--diagnose` and normal GUI; setup distribution.

- [x] Write executable failing checks for read-only viewing with no database/service, rejecting an unavailable D data directory, safe installer paths and mode argument validation. Avoid tests for static label wording.
- [x] Run checks and save RED evidence before behavior implementation.
- [x] Implement a usable Chinese window with date selection, actual observation window, disk usage trend, disk/attributed/unattributed totals, folder ranking and drilldown, 30-day file details, gaps/status, safe CSV export, pause/resume/reconcile and system tray. Database queries are read-only, asynchronous and bounded; service absence or uninitialized baseline explains the next action. Normal launch must not trigger elevation.
- [x] Implement ServiceBase hosting and cancellation. Installation elevates once, copies the complete payload to D:\CDriveSpaceMonitor\App, configures automatic service and installation-account tray startup, protects executable/data ACLs, and starts collection. Removal stops/removes only this named service and startup entry, leaving collected data by default. Validate source and destination paths and handle an upgrade by stopping before replacing files. No deletion of user data or broad recursive cleanup.
- [x] Provide a self-contained win-x64 publish script plus Chinese install/uninstall command launchers. Include command-line diagnostics and a fixture-only collection mode so core/native integration can be exercised without a production service installation.
- [x] Build the entire app with zero errors. Run diagnostics and GUI smoke against a fixture database. Use UI inspection to verify window readability and drilldown. Run all tests, self-review, commit and report.

### Task 4: Whole-application verification and delivery

**Files:** Verification report, build/smoke corrections as needed, final ZIPs and Chinese quick-start in outputs.
**Consumes:** Tasks 1–3 and reviewer evidence.
**Produces:** Self-contained software ZIP, source ZIP and quick-start.

- [x] Review the complete implementation against all design sections and run the full tests in Release. Fix material defects through a reproducing failing test, then verify the suite again.
- [x] Publish self-contained win-x64 and exercise the published executable in diagnostic, fixture collector and GUI modes. Verify startup/service arguments without registering a permanent production service. Record any privilege-dependent test that could not run, rather than reporting it as passed.
- [x] Obtain a fresh whole-branch code review, address material findings and re-run affected tests plus the full suite.
- [x] Package only published runtime files in the software ZIP; include source, build scripts, tests, plan and spec in the source ZIP, excluding SDK/cache/bin/obj/database/test fixtures. Verify archive contents, hashes and entry points.
- [x] Record actual validation counts and resource observations in Chinese instructions. State that full C-drive baseline and production service behavior begin after the user runs installation with UAC; do not claim a full-day observation before one exists.
- [x] Mark ledger and plan complete, commit source, and deliver links from outputs only.
