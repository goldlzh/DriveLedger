# Single Viewer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Reopening the app restores the existing viewer rather than adding windows/tray icons.

**Architecture:** A per-account/session named mutex owns GUI lifetime; a bounded local activation pipe informs the existing GUI. Only the GUI case enters this lifetime before creating WPF Application/MainWindow.

**Tech Stack:** Existing C#/.NET10, WPF/WinForms tray, native foreground permission and .NET local pipes; no additional dependency.

**Spec:** docs/superpowers/specs/2026-11-14-single-viewer-design.md

## Global Constraints

- Keep service/collector/diagnostic/install modes independent and data read-only for viewing.
- New-version production copies share one key across locations; fixture scopes isolated.
- Do not control or terminate existing user GUI processes while testing. Test-only helper modes belong in the test executable.
- Preserve D installation/data and history. Build a self-contained 1.0.1 upgrade payload and synchronize GitHub source; normal installer remains the upgrade route.

## Review Focus

- Simultaneous launches elect exactly one primary, including startup before the listener is ready.
- Hidden viewer receives activation through WPF Dispatcher and Show/Normal/Activate.
- Explicit exit/crash releases ownership; partial or idle pipe clients cannot block shutdown/future requests indefinitely.
- A failed activation or elevation mismatch does not create another GUI.
- Service startup/diagnostics remain outside the viewer mutex, with existing fixture smoke checks.

### Task 1: Single-viewer lifetime and WPF integration

Files: new src/DriveLedger.App/ViewerInstance.cs; Program.cs; MainWindow.xaml.cs; new tests/DriveLedger.AppTests/SingleInstanceTests.cs; test Program.cs.

- [x] Restore pinned SDK and run baseline App23/23.
- [x] Add real cross-process test-host cases: one owner and secondary callback; many simultaneous clients; delayed listener startup; graceful exit/reopen; owner termination/reopen; scope independence and production-key independence from data directory. Run against a minimal unimplemented shape and observe intended RED failures.
- [x] Implement mutex/pipe lifetime, bounded requests, foreground grant and disposal. Wire only GUI startup, before Application/MainWindow. Reuse window Open on Dispatcher.
- [x] Run App suite GREEN and publish/build; run all existing Core/Native suites once on changed product and fixture smoke. No test UI manipulation of the user's running windows.
- [x] Fresh read-only review of this feature diff, address material findings with regressions.

### Task 2: Release and source synchronization

- [ ] Document new behavior and old-viewer exit requirement; set version1.0.1.
- [ ] Package final self-contained software/source to the original chat outputs directory; verify CRC/entrypoints/critical DLLs.
- [ ] Push reviewed source to existing private goldlzh/DriveLedger, verify main remote tree. Preserve local original development history.
- [ ] Remove only owned temporary SDK/cache after all build verification, preserve output/runtime/data and any user process.
- [ ] Deliver upgrade package and clear install/usage instructions; do not claim installed processes have new behavior before upgrade.
