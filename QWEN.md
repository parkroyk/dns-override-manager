# QWEN.md

This file provides guidance to Qwen Code when working with code in this repository.

## Project Overview

**DNS Override Manager** is a Windows system-tray application (C#, .NET 9, WinForms) that overrides the active DNS servers on all IP-enabled network adapters with one of several saved **DNS profiles**, then restores the original configuration when deactivated, on timeout expiry, or on exit.

Key behaviors:
- Self-elevates via UAC on launch (`runas` verb); refuses to run non-admin.
- Single instance enforced with a named Windows event (`Local\DNSOverrideManager_AlreadyRunning`).
- Uses **WMI** (`Win32_NetworkAdapterConfiguration`) to capture/apply/restore DNS, plus **netsh** for switching DHCP adapters back to "Automatic" (WMI `EnableDHCP` is unreliable on some adapters).
- New profile servers are validated as IP addresses and reachability-tested (TCP port 53) before saving; unreachable servers are rejected.
- Settings persist in the registry: `HKCU\Software\DNSOverrideManager` (JSON profile list, active profile name, timeout minutes).
- Tray icon is generated programmatically (16×16 colored circle: gray = inactive, green = active); no `.ico` assets.

## Build & Run Commands

There is **no test project and no CI**. Verification is `dotnet build` plus manual run as administrator.

```powershell
# Build (debug)
dotnet build

# Release build
dotnet build -c Release
# Output: bin\Release\net9.0-windows\DNSOverrideManager.exe

# Framework-dependent single-file publish (~0.5 MB, target needs .NET Desktop Runtime 9)
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist

# Self-contained single-file publish (~110 MB, no runtime needed)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

Requires .NET SDK 9.0+ and Windows (project targets `net9.0-windows`). The only NuGet dependency is `System.Management` 9.0.0 (WMI).

## Architecture Map

Flat project — one file per concern, no subdirectories:

| File | Responsibility |
|------|----------------|
| `Program.cs` | Entry point: UAC self-elevation, single-instance acquisition, global exception handlers |
| `MainForm.cs` | Hidden off-screen host form owning the tray icon/menu; activate/deactivate logic, timeout timer, exit-with-restore, balloon notifications, programmatic icon generation |
| `SettingsForm.cs` | Modal profile management (add/edit/delete) + global timeout (0–1440 min) |
| `ProfileEditForm.cs` | Modal add/edit of one profile; IP validation + parallel reachability test on save |
| `DnsService.cs` | Static WMI+netsh operations: `Capture()` (backup), `ApplyOverride()`, `Restore()`, `IsElevated()`; defines `DnsOperationException` and the `AdapterBackup`/`DnsBackup` snapshot types |
| `DnsReachability.cs` | Static TCP port-53 probe, parallel via `Task.WhenAll` |
| `AppSettings.cs` | `DnsProfile` model + registry persistence (JSON); limits as constants: name ≤ 30 chars, server field ≤ 15 chars, ≤ 4 servers/profile |
| `StartupManager.cs` | Per-user `shell:startup` shortcut via WScript.Shell COM (`DNS Override Manager.lnk`) |

### Key flows to know before editing

- **Activation** (`MainForm.ActivateProfile`): capture backup once → apply override. Switching profiles *while active* reuses the existing backup (original state, not the previous profile's). If `ApplyOverride` fails on a fresh capture, it best-effort restores before showing the error.
- **Restore** (`DnsService.Restore`): DHCP adapters → `netsh interface ip set dns name=<NetConnectionID> source=dhcp` (falls back to WMI `EnableDHCP` if the netsh name wasn't resolved at capture); static adapters → write back exact original servers via `SetDNSServerSearchOrder`.
- **Capture** (`DnsService.Capture`): enumerates IP-enabled adapters that have ≥1 DNS server configured; adapters without DNS (virtual/disabled) are intentionally skipped. Resolves `NetConnectionID` in a separate best-effort WMI query keyed by adapter Description.
- **Exit**: if active, restores DNS, shows balloon, then delays ~3 s (`_exitTimer`) so the user sees it before `Application.Exit()`.
- **Settings while active**: saving from `SettingsForm` re-applies the (possibly edited) default profile to the live backup and restarts the timeout timer; deleting the default profile deactivates.

## Development Conventions

- **Nullable reference types enabled**, implicit usings enabled, root namespace `DNSOverrideManager`.
- **UI is 100% code-built** — no `.Designer.cs` files, no XAML/resource files. New forms follow the same pattern: constructor sets form properties, a private `BuildUi()` (or inline layout) wires controls and events.
- **Service classes are `static`** (`DnsService`, `DnsReachability`, `StartupManager`); UI classes are `sealed class ... : Form`.
- **Field naming**: underscore-prefixed camelCase for instance fields (`_settings`, `_notifyIcon`); PascalCase for locals/parameters; XML doc comments on public members and non-obvious private ones.
- **Error handling pattern**: low-level failures (WMI, COM, process) are wrapped into `DnsOperationException` with a user-facing message; forms catch it and show `MessageBox.Show(this, ex.Message, "DNS Override Manager", ...)`. Unhandled errors surface via the global handlers in `Program.cs`, never the JIT-debug window.
- **Limits are constants** in `AppSettings` (`MaxProfileNameLength`, `MaxServerFieldLength`, `MaxServersPerProfile`, `NoTimeout`) — reference them rather than hardcoding numbers in UI code.
- **WMI method calls**: pass array parameters wrapped in `object[] { serverArray }` (one parameter, not fanned-out strings); check the returned status code and throw on non-zero.
- **Process launches** use `ProcessStartInfo.ArgumentList` (no string quoting), `CreateNoWindow = true`, with a timeout + kill for restore operations.
- Commit style: short imperative messages (see git log).

## Gotchas

- The app must run elevated; WMI DNS changes fail silently-ish otherwise — errors are wrapped with an "try running as Administrator" hint when not elevated.
- `MainForm` is a real 16×16 window parked at (-32000, -32000), not hidden: it needs to gain/lose activation so clicking elsewhere dismisses the tray menu (`Deactivate` handler). Don't "fix" this by hiding the form.
- Tray menu is shown via `BeginInvoke` from the click handler and `_trayMenu.Show(Cursor.Position)` — both left- and right-click open the same unified menu.
- Icon handle leak avoidance: `bmp.GetHicon()` → clone → `DestroyIcon` (P/Invoke) in `finally`. Follow this pattern for any new icon creation.
- Registry read tolerates corrupt JSON (falls back to empty profile list); `Save()` always normalizes the active profile name to an existing one.
- `bin/`, `obj/`, `dist/` are gitignored build outputs; `dist\DNSOverrideManager.exe` is the publish artifact.
