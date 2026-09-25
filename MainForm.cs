using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace DNSOverrideManager;

/// <summary>
/// Hidden host form that owns the system-tray icon and all override logic.
/// Both left- and right-click open the same menu: the list of profiles, a Deactivate entry
/// (disabled while inactive), Settings..., Add/Remove to Startup, and Exit. Selecting a
/// profile makes it the default and activates it immediately. The balloon/tooltip always
/// reflect the active profile, or "Inactive (DHCP)".
/// </summary>
public sealed class MainForm : Form
{
    /// <summary>Named event a duplicate launch signals to make the running instance show a balloon.</summary>
    public const string AlreadyRunningEventName = @"Local\DNSOverrideManager_AlreadyRunning";

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _startupItem;
    private readonly ContextMenuStrip _trayMenu = new();

    private readonly Icon _inactiveIcon = CreateStateIcon(Color.Gray);
    private readonly Icon _activeIcon = CreateStateIcon(Color.LimeGreen);

    private DnsBackup? _backup;
    private System.Timers.Timer? _timeoutTimer;
    private System.Timers.Timer? _exitTimer;
    private bool _isActive;
    private bool _isExiting;
    private bool _exitFinalized;
    private string _activeProfileName = "";

    private readonly EventWaitHandle _alreadyRunningEvent;
    private Thread? _alreadyRunningListener;

    public MainForm(EventWaitHandle alreadyRunningEvent)
    {
        _alreadyRunningEvent = alreadyRunningEvent;

        // Keep the window off-screen and out of the taskbar: this is a tray-only app. It stays a
        // real (tiny, off-screen) window so it can gain/lose activation — losing activation when
        // you click elsewhere is what dismisses the tray menu, like a normal context menu.
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(16, 16);

        _startupItem = new ToolStripMenuItem("Add to Startup");
        _startupItem.Click += OnStartupToggled;

        _notifyIcon = new NotifyIcon
        {
            Icon = _inactiveIcon,
            Text = "DNS Override Manager — Inactive (DHCP)",
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            // Left- and right-click both open the same unified menu. The Show call is deferred so
            // it runs after this click fully completes.
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
                BeginInvoke(new Action(ShowTrayMenu));
        };

        // If a second copy of the app is launched it signals this event; show a balloon
        // pointing at the existing tray icon instead of starting another instance.
        _alreadyRunningListener = new Thread(() =>
        {
            while (true)
            {
                bool signaled;
                try { signaled = _alreadyRunningEvent.WaitOne(); }
                catch (ObjectDisposedException) { break; }   // handle disposed → shutting down
                if (!signaled) continue;
                try { BeginInvoke(new Action(ShowAlreadyRunningBalloon)); }
                catch (Exception) { break; }                 // form is closing
            }
        })
        {
            IsBackground = true,
            Name = "AlreadyRunningListener"
        };
        _alreadyRunningListener.Start();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        CreateControl(); // ensure the handle exists so BeginInvoke works from the timer thread

        // This off-screen window is activated whenever the menu opens; clicking anywhere else
        // deactivates it, which dismisses the menu — the standard context-menu behavior.
        Deactivate += (_, _) =>
        {
            if (_trayMenu.Visible)
                _trayMenu.Close();
        };
    }

    // ---- Tray menu (left- and right-click) ----

    private void ShowTrayMenu()
    {
        // Bring this off-screen window to the foreground so a subsequent click elsewhere deactivates
        // it (see the Deactivate handler) — that's what dismisses the menu. Without activation, a
        // tray-only app has no "click outside" signal to observe.
        Activate();
        RefreshTrayMenu();
        _trayMenu.Show(Cursor.Position);
    }

    private void RefreshTrayMenu()
    {
        _trayMenu.Items.Clear();
        UpdateStartupMenuText();

        // Profiles (checked on the active one). Omitted entirely when none exist yet.
        if (_settings.Profiles.Count > 0)
        {
            foreach (var p in _settings.Profiles)
            {
                var item = new ToolStripMenuItem(p.Name);
                item.Checked = _isActive && string.Equals(p.Name, _activeProfileName, StringComparison.OrdinalIgnoreCase);
                var captured = p;
                item.Click += (_, _) => ActivateProfile(captured);
                _trayMenu.Items.Add(item);
            }

            _trayMenu.Items.Add(new ToolStripSeparator());
        }

        // Deactivate — only meaningful while an override is active.
        var deactivateItem = new ToolStripMenuItem("Deactivate");
        deactivateItem.Enabled = _isActive;
        deactivateItem.Click += (_, _) => DeactivateDns(byTimeout: false);
        _trayMenu.Items.Add(deactivateItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += OnSettingsClicked;
        _trayMenu.Items.Add(settingsItem);
        _trayMenu.Items.Add(_startupItem);
        _trayMenu.Items.Add("Exit", null, OnExitClicked);
    }

    // ---- Activate / deactivate ----

    private void ActivateProfile(DnsProfile profile)
    {
        // Selecting a profile makes it the default and persists that choice.
        _settings.ActiveProfileName = profile.Name;
        _settings.Save();

        if (_isActive && string.Equals(_activeProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            ShowBalloon($"Active ({profile.Name})", "Already active.");
            return;
        }

        DnsBackup backup;
        bool reusingBackup = _isActive && _backup is not null && _backup.HasData;
        if (reusingBackup)
        {
            // Switching profiles while active: reuse the existing original-state backup.
            backup = _backup!;
        }
        else
        {
            try
            {
                backup = DnsService.Capture();
            }
            catch (DnsOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "DNS Override Manager",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!backup.HasData)
            {
                MessageBox.Show(this,
                    "No active network adapter with a DNS configuration was found.\n\n" +
                    "Make sure a network connection is active and IP-enabled, then try again.",
                    "DNS Override Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        try
        {
            DnsService.ApplyOverride(backup, profile.Servers);
        }
        catch (DnsOperationException ex)
        {
            // Undo any partially-applied changes before reporting — but only when we captured
            // a fresh backup this call; when switching profiles the previous override stays.
            if (!reusingBackup)
                try { DnsService.Restore(backup); } catch { /* best effort */ }

            MessageBox.Show(this, ex.Message, "DNS Override Manager",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _backup = backup;
        _isActive = true;
        _activeProfileName = profile.Name;
        UpdateTrayState();
        StartTimeoutIfConfigured();
        ShowBalloon($"Active ({profile.Name})", $"Now using {string.Join(", ", profile.Servers)}.");
    }

    private void DeactivateDns(bool byTimeout = false)
    {
        RestoreOriginalDns();

        if (!_isExiting)
            ShowBalloon("Inactive (DHCP)",
                byTimeout
                    ? $"Reverted to original DNS after {_settings.TimeoutMinutes} minute(s)."
                    : "Original DNS settings restored.");
    }

    /// <summary>Restores the captured original DNS state and resets the active flag (no balloon).</summary>
    private void RestoreOriginalDns()
    {
        StopTimeoutTimer();

        if (_backup is not null && _backup.HasData)
        {
            try
            {
                DnsService.Restore(_backup);
            }
            catch (DnsOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "DNS Override Manager",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        _backup = null;
        _isActive = false;
        _activeProfileName = "";
        UpdateTrayState();
    }

    // ---- Timeout ----

    private void StartTimeoutIfConfigured()
    {
        StopTimeoutTimer();
        if (_settings.TimeoutMinutes <= AppSettings.NoTimeout)
            return; // 0 = no timeout

        _timeoutTimer = new System.Timers.Timer(_settings.TimeoutMinutes * 60_000.0)
        {
            AutoReset = false
        };
        _timeoutTimer.Elapsed += (_, _) => BeginInvoke(() => DeactivateDns(byTimeout: true));
        _timeoutTimer.Start();
    }

    private void StopTimeoutTimer()
    {
        if (_timeoutTimer is null)
            return;

        _timeoutTimer.Stop();
        _timeoutTimer.Dispose();
        _timeoutTimer = null;
    }

    // ---- Tray state ----

    private void UpdateTrayState()
    {
        if (_isActive)
        {
            _notifyIcon.Icon = _activeIcon;
            _notifyIcon.Text = $"DNS Override Manager — Active ({_activeProfileName})";
        }
        else
        {
            _notifyIcon.Icon = _inactiveIcon;
            _notifyIcon.Text = "DNS Override Manager — Inactive (DHCP)";
        }
    }

    private void ShowBalloon(string title, string message) =>
        _notifyIcon.ShowBalloonTip(3000, title, message, ToolTipIcon.Info);

    private void ShowAlreadyRunningBalloon() =>
        _notifyIcon.ShowBalloonTip(2500, "DNS Override Manager",
            "It's already running — look for its icon in the system tray.", ToolTipIcon.Info);

    // ---- Menu handlers ----

    private void OnSettingsClicked(object? sender, EventArgs e)
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog(this) != DialogResult.OK)
            return;

        // If the override is active, keep it in sync with the (possibly edited) default profile.
        if (_isActive && _backup is not null && _backup.HasData)
        {
            var profile = _settings.ActiveProfile;
            if (profile is null)
            {
                // The default profile was removed — fall back to original DNS.
                DeactivateDns();
                return;
            }

            try
            {
                DnsService.ApplyOverride(_backup, profile.Servers);
                _activeProfileName = profile.Name;
            }
            catch (DnsOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "DNS Override Manager",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            StartTimeoutIfConfigured(); // reflect a changed timeout immediately
            UpdateTrayState();          // refresh the tooltip with the current profile
        }
    }

    private void OnStartupToggled(object? sender, EventArgs e)
    {
        try
        {
            if (StartupManager.IsInStartup())
            {
                StartupManager.RemoveFromStartup();
                MessageBox.Show(this, "Removed from Windows Startup.",
                    "DNS Override Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                StartupManager.AddToStartup();
                MessageBox.Show(this, "Added to Windows Startup. The app will launch at sign-in.",
                    "DNS Override Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not update the Startup folder:\n{ex.Message}\n\n" +
                "You can add a shortcut manually via shell:startup.",
                "DNS Override Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UpdateStartupMenuText();
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        _isExiting = true;

        if (_isActive)
        {
            // Restore DNS and notify that we're going inactive, then exit once the balloon has shown.
            RestoreOriginalDns();
            ShowBalloon("Inactive (DHCP)", "DNS Override Manager is exiting — original DNS restored.");

            _exitTimer = new System.Timers.Timer(3000) { AutoReset = false };
            _exitTimer.Elapsed += (_, _) =>
            {
                try { BeginInvoke(new Action(FinalizeExit)); }
                catch (Exception) { /* form already closed */ }
            };
            _exitTimer.Start();
        }
        else
        {
            FinalizeExit();
        }
    }

    private void FinalizeExit()
    {
        if (_exitFinalized)
            return;
        _exitFinalized = true;

        StopExitTimer();
        _notifyIcon.Visible = false;
        Application.Exit();
    }

    private void StopExitTimer()
    {
        if (_exitTimer is null)
            return;

        _exitTimer.Stop();
        _exitTimer.Dispose();
        _exitTimer = null;
    }

    private void UpdateStartupMenuText()
    {
        _startupItem.Text = StartupManager.IsInStartup() ? "Remove from Startup" : "Add to Startup";
    }

    // ---- Icon generation ----

    /// <summary>Draws a small filled circle in the given state color.</summary>
    private static Icon CreateStateIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            using var border = new Pen(Color.White, 1.5f);
            g.FillEllipse(fill, 2, 2, 12, 12);
            g.DrawEllipse(border, 2, 2, 12, 12);
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone(); // clone so we own the data independently of the GDI handle
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    // ---- Cleanup ----

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopTimeoutTimer();
        StopExitTimer();

        // Disposing the event unblocks and stops the background "already running" listener.
        _alreadyRunningEvent.Dispose();

        _trayMenu.Dispose();
        _notifyIcon.Dispose();
        _inactiveIcon.Dispose();
        _activeIcon.Dispose();
        base.OnFormClosed(e);
    }
}
