# Build/Startup Fix 0.2.4

- App startup no longer relies on `StartupUri`.
- `MainWindow` is created inside a guarded `OnStartup` block.
- Startup, Dispatcher, AppDomain and unobserved Task exceptions are logged.
- Log path: `%LOCALAPPDATA%\NextGenFiestaServerManager\startup.log`.
- Fatal startup exceptions are also displayed in a MessageBox.
- Added `scripts\Run-Startup-Diagnostics.ps1` to launch the published EXE and report early process exits / relevant Windows Application events.
