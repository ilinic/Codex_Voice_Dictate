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
        Font = SystemFonts.MessageBoxFont;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(640, 630);
        MinimumSize = new Size(520, 450);
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;

        // Only the content area scrolls. The Save/Cancel buttons remain accessible
        // even with large Windows text sizes or on a smaller secondary monitor.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(18)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        root.Controls.Add(scroller, 0, 0);

        // AutoSize rows use each control's preferred height. Fixed 34/38-pixel
        // rows clipped labels, checkboxes and text at 150–250% display scaling.
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 0,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Padding = new Padding(0, 0, 12, 0)
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroller.Controls.Add(fields);
        fields.SuspendLayout();

        _apiKey = new TextBox { UseSystemPasswordChar = true, Text = settings.GetApiKey() };
        AddField(fields, "OpenAI API key", _apiKey);
        AddRow(fields, new Label
        {
            Text = "Encrypted for this Windows user. Blank = OPENAI_API_KEY.",
            AutoSize = true,
            Dock = DockStyle.Top,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 12)
        });

        _languages = new CheckedListBox
        {
            CheckOnClick = true,
            IntegralHeight = false,
            Height = Font.Height * 7 + 12,
            MinimumSize = new Size(0, Font.Height * 7 + 12)
        };
        var selected = settings.SelectedLanguages.ToHashSet();
        foreach (var language in AppSettings.Languages)
        {
            int index = _languages.Items.Add($"{language.English} ({language.Code})");
            _languages.SetItemChecked(index, selected.Contains(language.Code));
        }
        AddField(fields, "Recognition languages (none = auto-detect)", _languages);

        _correction = new CheckBox
        {
            Text = "Correct technical terms and punctuation",
            Checked = settings.CorrectText,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 10, 0, 10)
        };
        AddRow(fields, _correction);

        _correctionModel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _correctionModel.Items.AddRange(AppSettings.CorrectionModels.Select(m => m.Label).ToArray());
        int modelIndex = Array.FindIndex(AppSettings.CorrectionModels, m => m.Id == settings.CorrectionModel);
        _correctionModel.SelectedIndex = modelIndex < 0 ? 0 : modelIndex;
        _correctionModel.Enabled = _correction.Checked;
        _correction.CheckedChanged += (_, _) => _correctionModel.Enabled = _correction.Checked;
        AddField(fields, "Correction model", _correctionModel);

        _insertShortcut = new ShortcutCaptureBox(settings.InsertShortcut);
        AddField(fields, "Start / stop (insert only)", _insertShortcut);
        _sendShortcut = new ShortcutCaptureBox(settings.SendShortcut);
        AddField(fields, "Stop and send", _sendShortcut);

        _keywords = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Text = settings.TechnicalKeywords,
            Height = Font.Height * 4 + 8,
            MinimumSize = new Size(0, Font.Height * 4 + 8)
        };
        AddField(fields, "Technical words (optional, comma or newline separated)", _keywords);

        _notifications = new CheckBox
        {
            Text = "Show tray notifications",
            Checked = settings.ShowNotifications,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 10, 0, 8)
        };
        AddRow(fields, _notifications);
        fields.ResumeLayout(true);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0)
        };
        var save = new Button
        {
            Text = "Save",
            AutoSize = true,
            MinimumSize = new Size(96, 0),
            Padding = new Padding(10, 6, 10, 6),
            DialogResult = DialogResult.None
        };
        var cancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            MinimumSize = new Size(96, 0),
            Padding = new Padding(10, 6, 10, 6),
            DialogResult = DialogResult.Cancel
        };
        save.Click += SaveClicked;
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 1);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddField(TableLayoutPanel grid, string caption, Control control)
    {
        AddRow(grid, new Label
        {
            Text = caption,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 8, 0, 3)
        });
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 0, 0, 8);
        AddRow(grid, control);
    }

    private static void AddRow(TableLayoutPanel grid, Control control)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(control, 0, row);
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
