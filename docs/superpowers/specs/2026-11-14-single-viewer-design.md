# Single viewer for C盘空间账本

The user explicitly requests preventing simultaneous viewer instances after seeing a test preview and an installed viewer. This is a small, fully specified behavior change; proceed without another approval round.

Only one new-version production GUI may run per Windows account and login session, regardless of executable directory or data-directory argument. A subsequent launch restores/shows and activates that GUI, then exits before constructing another window or tray icon. Closing the window still hides it to the tray; explicitly exiting releases ownership, and process death must not leave a persistent lock. Other accounts/sessions remain independent. Service, collection, diagnosis, install and uninstall modes remain independent. Explicit fixture viewers have separate deterministic scopes so tests cannot signal the user's production GUI.

Use an OS named mutex for atomic ownership and a bounded asynchronous local named pipe to notify the owner. The pipe is CurrentUserOnly, carries one fixed activation byte and acknowledgment, and grants foreground permission to its kernel-reported server PID before activation. Windows foreground policy may still limit focus; restoring/showing the existing window remains the intended action. Ordinary GUI runs asInvoker. Older versions do not implement this protocol and must be exited once before upgrading.

Alternatives: detecting process names is racy and confuses the service/other sessions; executable-path locks allow installed and unpacked copies together. Chosen scope is product/account/session. No database schema, collector, accounting, installation trust, or cleanup behavior changes.

Sources: Microsoft [AllowSetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow), [PipeOptions.CurrentUserOnly](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0). CurrentUserOnly also distinguishes elevation; normal viewing remains unelevated. A cross-elevation activation failure must never start a second GUI.
