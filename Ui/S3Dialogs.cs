namespace FeedForge;

public enum S3BrowseMode
{
    /// <summary>Выбрать папки и файлы как источники сети.</summary>
    Sources,
    /// <summary>Выбрать папки: каждая станет отдельной сетью.</summary>
    Networks
}

/// <summary>Обзор бакета S3: переход по папкам, отметка папок и файлов. Результат — адреса вида s3://бакет/папка/ и s3://бакет/файл.json.</summary>
public sealed class S3BrowserDialog : Form
{
    private sealed record ConnItem(S3Connection Conn)
    {
        public override string ToString() => $"{Conn.Bucket}   ({Conn.Endpoint})";
    }

    private readonly S3BrowseMode mode;
    private readonly ComboBox cmbConn = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox txtPath = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Button btnUp = new() { Text = "Вверх", AutoSize = true };
    private readonly Button btnRefresh = new() { Text = "Обновить", AutoSize = true };
    private readonly ListView lv = new()
    {
        View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, Dock = DockStyle.Fill
    };
    private readonly Label lblStatus = new() { AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button btnAddChecked = new() { AutoSize = true };
    private readonly Button btnAddCurrent = new() { Text = "Добавить текущую папку", AutoSize = true };
    private readonly Button btnCancel = new() { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };

    private string prefix = "";
    private int loadId;

    public List<string> Result { get; } = new();

    public S3BrowserDialog(AppSettings settings, S3BrowseMode mode)
    {
        this.mode = mode;
        Text = mode == S3BrowseMode.Networks ? "Сети из S3" : "Добавить из S3";
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(620, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 560);
        CancelButton = btnCancel;

        foreach (var c in settings.ConfiguredInputs) cmbConn.Items.Add(new ConnItem(c));

        lv.Columns.Add("Имя", 330);
        lv.Columns.Add("Размер", 90);
        lv.Columns.Add("Изменён", 120);

        btnAddChecked.Text = mode == S3BrowseMode.Networks ? "Создать сети из отмеченных папок" : "Добавить отмеченное";

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10, 10, 10, 0) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(new Label { Text = "Бакет:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        top.Controls.Add(cmbConn, 1, 0);
        top.SetColumnSpan(cmbConn, 2);
        top.Controls.Add(new Label { Text = "Путь:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        top.Controls.Add(txtPath, 1, 1);
        var nav = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        nav.Controls.Add(btnUp);
        nav.Controls.Add(btnRefresh);
        top.Controls.Add(nav, 2, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(10) };
        bottom.Controls.Add(lblStatus, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnAddChecked);
        if (mode == S3BrowseMode.Sources) buttons.Controls.Add(btnAddCurrent);
        bottom.Controls.Add(buttons, 0, 1);

        var mid = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 0) };
        mid.Controls.Add(lv);

        Controls.Add(mid);
        Controls.Add(bottom);
        Controls.Add(top);

        cmbConn.SelectedIndexChanged += async (_, _) => { prefix = ""; await LoadAsync(); };
        btnUp.Click += async (_, _) => { GoUp(); await LoadAsync(); };
        btnRefresh.Click += async (_, _) => await LoadAsync();
        lv.MouseDoubleClick += async (_, e) =>
        {
            var hit = lv.GetItemAt(e.X, e.Y);
            if (hit?.Tag is ValueTuple<string, string> t && t.Item1 == "D")
            {
                prefix = t.Item2;
                await LoadAsync();
            }
        };
        btnAddChecked.Click += (_, _) => AddChecked();
        btnAddCurrent.Click += (_, _) => AddCurrent();

        Shown += async (_, _) =>
        {
            if (cmbConn.Items.Count == 0)
            {
                lblStatus.Text = "Нет входящих подключений S3. Добавьте их в Настройки → S3.";
                return;
            }
            cmbConn.SelectedIndex = 0;
            await Task.CompletedTask;
        };
    }

    private S3Connection? Conn => (cmbConn.SelectedItem as ConnItem)?.Conn;

    private void GoUp()
    {
        string p = prefix.TrimEnd('/');
        int i = p.LastIndexOf('/');
        prefix = i < 0 ? "" : p[..(i + 1)];
    }

    private async Task LoadAsync()
    {
        var conn = Conn;
        if (conn == null) return;
        int my = ++loadId;
        txtPath.Text = $"s3://{conn.Bucket}/{prefix}";
        lblStatus.Text = "Загружаю...";
        lv.Items.Clear();
        btnUp.Enabled = prefix.Length > 0;

        try
        {
            var client = conn.CreateClient();
            string bucket = conn.Bucket, pfx = prefix;
            var listing = await Task.Run(() => client.ListAsync(bucket, pfx, CancellationToken.None));
            if (my != loadId) return;

            lv.BeginUpdate();
            foreach (var folder in listing.Folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string name = folder[pfx.Length..].TrimEnd('/');
                var item = new ListViewItem("📁 " + name) { Tag = ("D", folder) };
                item.SubItems.Add("");
                item.SubItems.Add("");
                lv.Items.Add(item);
            }
            if (mode == S3BrowseMode.Sources)
            {
                foreach (var f in listing.Files.Where(f => S3Path.IsFeedKey(f.Key)).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var item = new ListViewItem(f.Key[pfx.Length..]) { Tag = ("F", f.Key) };
                    item.SubItems.Add(f.Size >= 1024 * 1024 ? $"{f.Size / 1024.0 / 1024.0:F1} МБ" : $"{f.Size / 1024.0:F0} КБ");
                    item.SubItems.Add(f.LastModified?.ToLocalTime().ToString("dd.MM.yy HH:mm") ?? "");
                    lv.Items.Add(item);
                }
            }
            lv.EndUpdate();

            int files = mode == S3BrowseMode.Sources ? listing.Files.Count(f => S3Path.IsFeedKey(f.Key)) : 0;
            lblStatus.Text = $"Папок: {listing.Folders.Count}" + (mode == S3BrowseMode.Sources ? $", файлов json/xml/yml: {files}" : "") +
                             ". Двойной щелчок — открыть папку, галочка — выбрать.";
        }
        catch (Exception ex)
        {
            if (my != loadId) return;
            lblStatus.Text = "Ошибка: " + ex.Message;
        }
    }

    private void AddChecked()
    {
        var conn = Conn;
        if (conn == null) return;
        Result.Clear();
        foreach (ListViewItem item in lv.CheckedItems)
        {
            if (item.Tag is not ValueTuple<string, string> t) continue;
            Result.Add($"s3://{conn.Bucket}/{t.Item2}");
        }
        if (Result.Count == 0)
        {
            MessageBox.Show(this, "Отметьте галочкой папки" + (mode == S3BrowseMode.Sources ? " или файлы." : "."), Text);
            return;
        }
        DialogResult = DialogResult.OK;
    }

    private void AddCurrent()
    {
        var conn = Conn;
        if (conn == null) return;
        Result.Clear();
        Result.Add($"s3://{conn.Bucket}/{prefix}");
        DialogResult = DialogResult.OK;
    }
}
