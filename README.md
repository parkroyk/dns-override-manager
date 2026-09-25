# DNS Override Manager

A Windows desktop application that lets you override your current DNS settings with one of several saved **DNS profiles**, then restores your original configuration when you're done. Built with C# and .NET 9 (Windows Forms).

---

## ✨ Features

### 🎯 System Tray Integration
- Resides in the taskbar system tray area
- **Left- or right-click** the icon to open the same menu, which lists: your DNS profiles, a **Deactivate** entry (grayed out while already inactive), **Settings…**, **Add/Remove to Startup**, and **Exit**
- Selecting a profile makes it the default and activates it immediately; there is no built-in default profile, so create one via **Settings…** first
- Icon **changes color** to reflect the current state:
  - 🔘 **Gray** = Inactive (using original DNS)
  - 🟢 **Green** = Active (a profile's DNS override is in effect)
- **Balloon notifications** and the tooltip always show which profile is active, e.g. **"Active (Google)"** or **"Inactive (DHCP)"**
- On **Exit**, if a profile is active, the DNS is restored and a balloon confirms **"Inactive (DHCP)"** before the app closes

### 🗂️ DNS Profiles
- Create as many named profiles as you like (e.g. "Google", "Cloudflare", "Work")
- Each profile holds a **name** (up to 30 characters) and **1 to 4 DNS server addresses** (each up to 15 characters)
- Add, edit, and delete profiles from the Settings window
- There is no built-in default profile — add at least one. Once profiles exist, the first one is marked as the active choice, and selecting any profile from the tray menu makes it the new default

### ✅ Reachability Testing
- When you add or edit a profile, every DNS server address is validated as an IPv4/IPv6 address and then **tested for reachability** (a TCP connection to port 53)
- Servers that can't be reached are **rejected** — only reachable servers can be saved into a profile

### 🔐 Runs as Administrator
- Modifying DNS requires elevated privileges, so the app **requests administrator rights automatically on launch** (a UAC prompt appears the first time you start it)
- If elevation is declined, the app cannot run — accept the prompt to continue

### 🔄 Automatic DNS Management
- Detects **every** active, IP-enabled network interface that has DNS configured (multiple adapters are handled at once)
- Backs up each adapter's current DNS servers and DHCP state before overriding
- Restores the original configuration when you turn the override off, when the timeout expires, or on exit — switching DHCP adapters back to "Automatic" and writing static adapters' original servers back

### 🚫 Single Instance
- Only one copy runs at a time. Launching a second copy notifies the running instance (a balloon points at its tray icon) instead of starting a duplicate

### 💾 Persistent Settings
- All profiles, the default profile, and the timeout are saved to the Windows Registry
- No need to reconfigure after closing the app

### 🚀 Easy Startup Deployment
- Add **or remove** the app from Windows Startup with a single click (the menu item toggles between "Add to Startup" and "Remove from Startup")
- When enabled, it launches automatically at sign-in

---

## 📋 Requirements

- **Windows 10 or Windows 11**
- **Administrator rights** — the app prompts for elevation (UAC) on launch
- **.NET SDK 9.0 or later** (only needed for building from source)
  - Download from [.NET](https://dotnet.microsoft.com/download)

> **Note:** The default build is **framework-dependent** — the target machine needs a one-time install of the [.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet). A self-contained option that bundles the runtime (no install needed, but a ~110 MB EXE) is also provided below.

---

## 🛠️ Building the Application

### Option 1: Build from Source

Open PowerShell in the `DNSOverrideManager` folder and run:

```powershell
dotnet build -c Release
```

The compiled executable will be located in:
```
bin\Release\net9.0-windows\DNSOverrideManager.exe
```

### Option 2: Create a Single-File Executable (Recommended)

Produces a small (~0.5 MB) single-file EXE. The target machine must have the [.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet) installed:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

The single-file executable will be in the `dist` folder:
```
dist\DNSOverrideManager.exe
```

### Option 3: Create a Self-Contained Executable (no runtime install needed)

Bundles the full .NET runtime into a large (~110 MB) EXE that runs on any machine with no .NET installed. Use this if you cannot guarantee the target has the runtime:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

The single-file executable will be in the `dist` folder:
```
dist\DNSOverrideManager.exe
```

---

## 📦 Installation & Usage

### 1. Run the Application
- If you used the default (framework-dependent) build, install the [.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet) once first
- Double-click `DNSOverrideManager.exe`
- **Accept the UAC prompt** when it appears (the app needs administrator rights to change DNS)
- The app launches and the icon appears in your system tray

### 2. Create a DNS Profile
1. Left- or right-click the tray icon → Select **"Settings..."**
2. Click **"Add..."**
3. Enter a **profile name** (up to 30 characters) and **1 to 4 DNS server addresses** (each up to 15 characters), e.g. `8.8.8.8` and `8.8.4.4` for "Google"
4. Click **"OK"** — each server is tested for reachability (TCP port 53); unreachable servers are rejected
5. Back in the profile list, set the **Timeout (minutes)** if desired (`0` = no timeout) and click **"Save"**

### 3. Activate a Profile
- **Left- or right-click** the tray icon to open the menu
- Select a profile (e.g. "Google") — it becomes the default and its DNS servers are applied immediately
- The icon turns green and a balloon reads **"Active (Google)"**

### 4. Turn It Off
- Left- or right-click the tray icon → Select **"Deactivate"**
- The original DNS settings are restored, the icon turns gray, and a balloon reads **"Inactive (DHCP)"**

### 5. Automatic Timeout (Optional)
- If you set a timeout > 0, the override automatically reverts after that many minutes
- The icon changes back to gray and a balloon appears when it expires

---

## 🚀 Add to / Remove from Windows Startup

To make the app launch automatically on Windows startup:

### Method 1: Use the In-App Menu (recommended)
- Right-click the tray icon → Select **"Add to Startup"**
- The same menu item becomes **"Remove from Startup"** once enabled, so you can undo it with one click

### Method 2: Manual Startup Folder
1. Press `Win + R`, type `shell:startup`, press Enter
2. Create a shortcut to `DNSOverrideManager.exe` in this folder
3. Delete the shortcut to remove it from startup

---

## 🖥️ How It Works

### DNS Override Mechanism
The application uses **WMI** (`Win32_NetworkAdapterConfiguration`) together with **netsh** to:
1. Enumerate every IP-enabled network interface that has at least one DNS server configured (adapters without DNS, such as virtual or disabled ones, are skipped)
2. Back up each adapter's current DNS servers, whether it uses DHCP, and its netsh interface name
3. Apply the selected profile's DNS servers (one to four) with `SetDNSServerSearchOrder()` on every captured adapter
4. Restore the original configuration when you turn the override off, when the timeout expires, or on exit:
   - **DHCP adapters** are switched back to "Automatic" with `netsh interface ip set dns ... source=dhcp` (which reliably changes the DNS source; it falls back to WMI `EnableDHCP` if the interface name can't be resolved)
   - **Static adapters** get their exact original servers written back with `SetDNSServerSearchOrder()`

### Reachability Testing
When a profile is saved, each non-empty server address is parsed as an IP address and then probed by opening a **TCP connection to port 53** (with a short timeout). All entered servers are tested in parallel. Any server that fails the probe is reported and the profile is not saved until only reachable servers remain — this prevents saving a profile that would break connectivity.

### Single Instance
On startup (after elevation) the app tries to open a named Windows event (`Local\DNSOverrideManager_AlreadyRunning`). If another instance already owns it, the duplicate signals it and exits; the running instance shows a balloon pointing at its tray icon. Otherwise the new instance claims the event.

### Registry Storage
Settings are stored under:
```
HKEY_CURRENT_USER\Software\DNSOverrideManager
  ├── Profiles        (String/JSON) - list of { Name, Servers[] }
  ├── ActiveProfile   (String)      - name of the default profile
  └── TimeoutMinutes  (DWORD)       - timeout in minutes (0 = no timeout)
```

---

## 📁 Project Structure

```
DNSOverrideManager/
├── DNSOverrideManager.csproj    # Project file (.NET 9 WinForms + System.Management WMI package)
├── Program.cs                   # Entry point — self-elevates via UAC, enforces single instance
├── MainForm.cs                  # Tray icon, profile menu, activate/deactivate, timeout, balloons
├── SettingsForm.cs              # Profile management window (add / edit / delete + timeout)
├── ProfileEditForm.cs           # Add/edit a single profile (name + up to 4 servers) with validation
├── DnsService.cs                # WMI + netsh DNS detect / backup / apply / restore
├── DnsReachability.cs           # TCP port-53 reachability probe for server addresses
├── AppSettings.cs               # Profile model + registry persistence (JSON)
├── StartupManager.cs            # Windows Startup shortcut (shell:startup)
├── README.md                    # This file
├── dist/                        # Compiled single-file EXE (after publish)
└── bin/                         # Build output
```

### Source Files
| File | Description |
|------|-------------|
| `Program.cs` | Application entry point — self-elevates via UAC and enforces a single running instance |
| `MainForm.cs` | System-tray icon, unified tray menu, activate/deactivate, timeout timer, exit notification, balloons |
| `SettingsForm.cs` | Profile management: list, add / edit / delete profiles, global timeout |
| `ProfileEditForm.cs` | Add/edit one profile — name + up to 4 servers, IP validation + reachability test |
| `DnsService.cs` | WMI + netsh operations — detect adapters, backup, apply override, restore |
| `DnsReachability.cs` | Tests a server address by opening a TCP connection to port 53 |
| `AppSettings.cs` | Profile model + registry persistence (JSON) |
| `StartupManager.cs` | Adds/removes the `shell:startup` shortcut (WScript.Shell) |

---

## 🛠️ Troubleshooting

### App won't start — "A .NET runtime or compatible framework was not found"
- The default build is **framework-dependent**. Install the [.NET Desktop Runtime](https://dotnet.microsoft.com/download/dotnet) on that machine, or rebuild with the self-contained option (Option 3 above).

### UAC prompt appears on launch / app won't start after declining it
- The app requires administrator rights to modify DNS. **Accept** the elevation prompt. If you decline it, the elevated instance is not started and the app exits.

### A server is reported as unreachable when adding a profile
- Reachability is tested with a TCP connection to port 53. Some networks or firewalls block this even for valid servers — verify the address is correct and reachable from this machine (e.g. it responds on port 53). Only reachable servers can be saved.

### Tray icon doesn't appear
- Ensure the application is running (check Task Manager)
- Windows may hide icons in the "hidden icons" menu (↑ arrow in tray)

### DNS override fails
- The app self-elevates, so it should already be running as Administrator — if a WMI error still appears, ensure a network interface is active and IP-enabled
- Adapters without a configured DNS (virtual/disabled interfaces) are intentionally skipped

### App doesn't start automatically
- Verify the shortcut exists in `shell:startup`
- For EXE files, ensure the path in the shortcut is correct

### Icon doesn't change
- The icon is generated programmatically; ensure the build succeeded

---

## 🔒 Security Notes

- The app **runs with administrator privileges** (it elevates itself on launch) — only run it from a trusted location
- Modifying DNS settings affects network connectivity
- The app **backs up and restores** original settings automatically, including on exit, so it never leaves an override in place
- Use trusted DNS servers (e.g., Google `8.8.8.8`, Cloudflare `1.1.1.1`, or your own)

---

## 🔮 Future Enhancements

- [ ] Custom icon support (load `.ico` files)
- [ ] Preset DNS profiles shipped out of the box (e.g., "Work", "Home", "Public WiFi")
- [ ] Per-profile timeout
- [ ] Logs/history of DNS changes
- [ ] Dark mode tray icon variants

---

## 📄 License

MIT License — Feel free to modify and distribute.

---

## 🤝 Support

For issues or questions, please open an issue in the repository.
