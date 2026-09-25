namespace DNSOverrideManager;

/// <summary>
/// Modal dialog for managing DNS profiles (add / edit / delete) and the global override
/// timeout. Reads from / writes to the supplied <see cref="AppSettings"/> instance. The
/// default profile itself is chosen by selecting a profile from the tray icon's menu.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly ListView _profileList = new();
    private readonly NumericUpDown _timeoutBox = new();

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        BuildUi();
        RefreshProfileList();
        LoadValues();
    }

    private void BuildUi()
    {
        Text = "DNS Override Manager — Profiles";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 470);

        _profileList.View = View.Details;
        _profileList.FullRowSelect = true;
        _profileList.MultiSelect = false;
        _profileList.HideSelection = false;
        _profileList.Dock = DockStyle.Fill;
        _profileList.MinimumSize = new Size(0, 160); // keep enough room for ~5 profiles
        _profileList.Columns.Add("Name", 170);
        _profileList.Columns.Add("DNS Servers", 250);

        var addButton = new Button { Text = "Add...", AutoSize = true };
        addButton.Click += (_, _) => OnAddClicked();
        var editButton = new Button { Text = "Edit...", AutoSize = true, Enabled = false };
        editButton.Click += (_, _) => OnEditClicked();
        var deleteButton = new Button { Text = "Delete", AutoSize = true, Enabled = false };
        deleteButton.Click += (_, _) => OnDeleteClicked();

        // Edit/Delete are only meaningful with a profile selected.
        void UpdateSelectionButtons()
        {
            bool hasSelection = _profileList.SelectedItems.Count > 0;
            editButton.Enabled = hasSelection;
            deleteButton.Enabled = hasSelection;
        }
        _profileList.SelectedIndexChanged += (_, _) => UpdateSelectionButtons();

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        buttonPanel.Controls.Add(addButton);
        buttonPanel.Controls.Add(editButton);
        buttonPanel.Controls.Add(deleteButton);

        var timeoutLabel = new Label { Text = "Timeout (minutes):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 4, 0) };
        _timeoutBox.Minimum = 0;
        _timeoutBox.Maximum = 1440; // cap at 24 hours
        _timeoutBox.Width = 70;
        _timeoutBox.Anchor = AnchorStyles.Left;

        var timeoutRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, Dock = DockStyle.Fill, Margin = new Padding(0) };
        timeoutRow.Controls.Add(timeoutLabel);
        timeoutRow.Controls.Add(_timeoutBox);

        var note = new Label
        {
            Text = "Timeout of 0 means the override stays active until you turn it off. Select a profile from the tray icon to activate it.",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 6, 0, 0)
        };

        var saveButton = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true };
        saveButton.Click += OnSaveClicked;
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var okPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        okPanel.Controls.Add(cancelButton);
        okPanel.Controls.Add(saveButton);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // profile buttons
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // profile list
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // timeout
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // note
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // save / cancel

        root.Controls.Add(buttonPanel, 0, 0);
        root.Controls.Add(_profileList, 0, 1);
        root.Controls.Add(timeoutRow, 0, 2);
        root.Controls.Add(note, 0, 3);
        root.Controls.Add(okPanel, 0, 4);

        Controls.Add(root);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private void RefreshProfileList()
    {
        _profileList.Items.Clear();
        foreach (var p in _settings.Profiles)
        {
            var item = new ListViewItem(p.Name);
            item.SubItems.Add(string.Join(", ", p.Servers));
            item.Tag = p;
            _profileList.Items.Add(item);
        }
    }

    private void OnAddClicked()
    {
        using var dlg = new ProfileEditForm(null);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is null)
            return;

        if (_settings.Profiles.Any(p => string.Equals(p.Name, dlg.Result!.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"A profile named '{dlg.Result.Name}' already exists.",
                "Duplicate name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.Profiles.Add(dlg.Result);
        RefreshProfileList();
    }

    private void OnEditClicked()
    {
        if (_profileList.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Select a profile to edit.", "DNS Override Manager",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var current = (DnsProfile)_profileList.SelectedItems[0].Tag!;
        using var dlg = new ProfileEditForm(current);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result is null)
            return;

        if (_settings.Profiles.Any(p => !ReferenceEquals(p, current) &&
                                        string.Equals(p.Name, dlg.Result!.Name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, $"A profile named '{dlg.Result.Name}' already exists.",
                "Duplicate name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // If the edited profile was the default, keep it default under its (possibly new) name.
        if (string.Equals(current.Name, _settings.ActiveProfileName, StringComparison.OrdinalIgnoreCase))
            _settings.ActiveProfileName = dlg.Result.Name;

        int idx = _settings.Profiles.IndexOf(current);
        if (idx >= 0)
            _settings.Profiles[idx] = dlg.Result;

        RefreshProfileList();
    }

    private void OnDeleteClicked()
    {
        if (_profileList.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Select a profile to delete.", "DNS Override Manager",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var current = (DnsProfile)_profileList.SelectedItems[0].Tag!;
        var confirm = MessageBox.Show(this, $"Delete profile '{current.Name}'?", "Confirm delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
            return;

        _settings.Profiles.Remove(current);
        RefreshProfileList();
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        _settings.TimeoutMinutes = (int)_timeoutBox.Value;
        _settings.Save();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void LoadValues()
    {
        _timeoutBox.Value = Math.Clamp(_settings.TimeoutMinutes, 0, (int)_timeoutBox.Maximum);
    }
}
