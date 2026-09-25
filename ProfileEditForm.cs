using System.Net;

namespace DNSOverrideManager;

/// <summary>
/// Modal dialog for creating or editing a single DNS profile: a name plus up to four DNS
/// server addresses. Each non-empty server is validated as an IP address and tested for
/// reachability (TCP port 53) before the profile can be saved — unreachable servers are
/// rejected so they cannot be added.
/// </summary>
public sealed class ProfileEditForm : Form
{
    private readonly TextBox _nameBox = new();
    private readonly TextBox[] _serverBoxes;
    private readonly Button _okButton;

    /// <summary>The edited profile (Name + Servers). Valid only after DialogResult == OK.</summary>
    public DnsProfile? Result { get; private set; }

    public ProfileEditForm(DnsProfile? existing)
    {
        Text = existing is null ? "New DNS Profile" : "Edit DNS Profile";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _serverBoxes = new TextBox[AppSettings.MaxServersPerProfile];

        var layout = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _nameBox.MaxLength = AppSettings.MaxProfileNameLength;
        for (int i = 0; i < _serverBoxes.Length; i++)
            _serverBoxes[i] = new TextBox { MaxLength = AppSettings.MaxServerFieldLength, Width = 160 };

        var note = new Label
        {
            Text = "Servers are tested for reachability (TCP port 53) when you save.\nUnreachable servers cannot be added.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 0)
        };

        _okButton = new Button { Text = "OK", DialogResult = DialogResult.None, AutoSize = true };
        _okButton.Click += OnOkClicked;
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        buttonPanel.Controls.Add(cancelButton);
        buttonPanel.Controls.Add(_okButton);

        int row = 0;
        layout.Controls.Add(MakeLabel("Name:"), 0, row);
        layout.Controls.Add(_nameBox, 1, row++);
        for (int i = 0; i < _serverBoxes.Length; i++)
        {
            layout.Controls.Add(MakeLabel($"Server {i + 1}:"), 0, row);
            layout.Controls.Add(_serverBoxes[i], 1, row++);
        }
        layout.Controls.Add(note, 0, row);
        layout.SetColumnSpan(note, 2);
        row++;
        layout.Controls.Add(buttonPanel, 0, row);
        layout.SetColumnSpan(buttonPanel, 2);

        Controls.Add(layout);
        AcceptButton = _okButton;
        CancelButton = cancelButton;

        if (existing is not null)
        {
            _nameBox.Text = existing.Name;
            for (int i = 0; i < existing.Servers.Count && i < _serverBoxes.Length; i++)
                _serverBoxes[i].Text = existing.Servers[i];
        }
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = true,
        Margin = new Padding(0, 4, 8, 4)
    };

    private async void OnOkClicked(object? sender, EventArgs e)
    {
        string name = _nameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Please enter a profile name.", "Invalid input",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _nameBox.Focus();
            return;
        }

        var servers = new List<string>();
        for (int i = 0; i < _serverBoxes.Length; i++)
        {
            string value = _serverBoxes[i].Text.Trim();
            if (value.Length == 0)
                continue; // empty field = skip (a profile may have 1 to 4 servers)

            if (!IPAddress.TryParse(value, out _))
            {
                MessageBox.Show(this, $"Server {i + 1} ('{value}') is not a valid IPv4/IPv6 address.",
                    "Invalid input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _serverBoxes[i].Focus();
                return;
            }
            servers.Add(value);
        }

        if (servers.Count == 0)
        {
            MessageBox.Show(this, "Enter at least one DNS server address.", "Invalid input",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _serverBoxes[0].Focus();
            return;
        }

        // Test reachability for all non-empty servers in parallel.
        _okButton.Enabled = false;
        string originalTitle = Text;
        Text = "Testing server reachability…";
        var results = await DnsReachability.TestAllAsync(servers);
        Text = originalTitle;
        _okButton.Enabled = true;

        var unreachable = servers.Where(s => !results[s]).ToList();
        if (unreachable.Count > 0)
        {
            MessageBox.Show(this,
                "The following DNS server(s) could not be reached (TCP port 53):\n\n" +
                string.Join("\n", unreachable) +
                "\n\nOnly reachable servers can be added.",
                "Unreachable server", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            for (int i = 0; i < _serverBoxes.Length; i++)
            {
                if (unreachable.Contains(_serverBoxes[i].Text.Trim()))
                {
                    _serverBoxes[i].Focus();
                    break;
                }
            }
            return;
        }

        Result = new DnsProfile { Name = name, Servers = servers };
        DialogResult = DialogResult.OK;
        Close();
    }
}
