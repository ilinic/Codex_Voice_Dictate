namespace CodexVoiceDictate;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _apiKey;
    private readonly CheckedListBox _languages;
    private readonly CheckBox _correction;
    private readonly ComboBox _correctionModel;
    private readonly ShortcutCaptureBox _insertShortcut;
    private readonly ShortcutCaptureBox _sendShortcut;
    private readonly TextBox _keywords;
    private readonly CheckBox _notifications;
    private readonly AppSettings _settings;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Codex Voice Dictate — Settings";
        StartPosition = FormStartPosition.CenterScreen;
        // The layout below is specified in logical 96-DPI, Segoe UI 9pt units.
        // Without design-time dimensions, programmatic forms keep 590x610 physical
        // pixels even when Windows is set to 150–250% scaling.
        Font = SystemFonts.MessageBoxFont;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(590, 610);
        MinimumSize = new Size(550, 570);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(16), AutoScroll = true,
            ColumnCount = 2, RowCount = 9
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        _apiKey = new TextBox { Dock = DockStyle.Top, UseSystemPasswordChar = true, Text = settings.GetApiKey() };
        AddRow(layout, 0, "OpenAI API key", _apiKey, 34);
        var hint = new Label
        {
            AutoSize = true, Text = "Stored encrypted for this Windows user (DPAPI). Empty = use OPENAI_API_KEY, if set.",
            ForeColor = Color.DimGray, Dock = DockStyle.Fill
        };
        layout.Controls.Add(hint, 1, 1);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        _languages = new CheckedListBox { CheckOnClick = true, Height = 140, Dock = DockStyle.Fill };
        var selected = settings.SelectedLanguages.ToHashSet();
        foreach (var language in AppSettings.Languages)
        {
            int index = _languages.Items.Add($"{language.English} ({language.Code})");
            _languages.SetItemChecked(index, selected.Contains(language.Code));
        }
        AddRow(layout, 2, "Recognition languages\n(none = auto-detect)", _languages, 154);

        _correction = new CheckBox { Text = "Correct technical terms and punctuation", Checked = settings.CorrectText, AutoSize = true };
        AddRow(layout, 3, "Text correction", _correction, 34);

        _correctionModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        _correctionModel.Items.AddRange(AppSettings.CorrectionModels.Select(m => m.Label).ToArray());
        int modelIndex = Array.FindIndex(AppSettings.CorrectionModels, m => m.Id == settings.CorrectionModel);
        _correctionModel.SelectedIndex = modelIndex < 0 ? 0 : modelIndex;
        _correctionModel.Enabled = _correction.Checked;
        _correction.CheckedChanged += (_, _) => _correctionModel.Enabled = _correction.Checked;
        AddRow(layout, 4, "Correction model", _correctionModel, 38);

        _insertShortcut = new ShortcutCaptureBox(settings.InsertShortcut) { Dock = DockStyle.Top };
        AddRow(layout, 5, "Start / stop, insert only", _insertShortcut, 38);
        _sendShortcut = new ShortcutCaptureBox(settings.SendShortcut) { Dock = DockStyle.Top };
        AddRow(layout, 6, "Stop and send", _sendShortcut, 38);

        _keywords = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = settings.TechnicalKeywords };
        AddRow(layout, 7, "Technical words (optional)\ncomma or newline separated", _keywords, 72);

        _notifications = new CheckBox { Text = "Show tray notifications", Checked = settings.ShowNotifications, AutoSize = true };
        AddRow(layout, 8, "Notifications", _notifications, 34);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(0, 8, 16, 8) };
        var save = new Button { Text = "Save", Width = 90, DialogResult = DialogResult.None };
        var cancel = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        save.Click += SaveClicked;
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddRow(TableLayoutPanel grid, int row, string label, Control control, int height)
    {
        while (grid.RowStyles.Count <= row) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        grid.RowStyles[row].Height = height;
        var text = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoSize = false };
        grid.Controls.Add(text, 0, row);
        grid.Controls.Add(control, 1, row);
    }

    private void SaveClicked(object? sender, EventArgs e)
    {
        if (!Shortcut.TryParse(_insertShortcut.Text, out var insert) ||
            !Shortcut.TryParse(_sendShortcut.Text, out var send) || insert == send)
        {
            MessageBox.Show(this, "Choose two distinct shortcuts. F1–F12 are allowed alone; letters require Ctrl or Alt.",
                "Invalid hotkeys", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _settings.SetApiKey(_apiKey.Text);
        _settings.RecognitionLanguages = string.Join(",", Enumerable.Range(0, _languages.Items.Count)
            .Where(_languages.GetItemChecked).Select(index => AppSettings.Languages[index].Code));
        _settings.CorrectText = _correction.Checked;
        _settings.CorrectionModel = AppSettings.CorrectionModels[_correctionModel.SelectedIndex].Id;
        _settings.InsertShortcut = insert.ToString();
        _settings.SendShortcut = send.ToString();
        _settings.TechnicalKeywords = _keywords.Text;
        _settings.ShowNotifications = _notifications.Checked;
        try { _settings.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, error.Message, "Cannot save settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
