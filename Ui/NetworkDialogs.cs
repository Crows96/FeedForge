using System.Text;

namespace FeedForge;

/// <summary>Создание и изменение сети: название, имя папки, источники (ссылка, файл, список из txt, папка или файл из S3).</summary>
public sealed class NetworkDialog : Form
{
    private readonly TextBox txtName = new() { Dock = DockStyle.Fill };
    private readonly TextBox txtSlug = new() { Dock = DockStyle.Fill };
    private readonly TextBox txtOne = new() { Dock = DockStyle.Fill };
    private readonly Button btnAddOne = new() { Text = "Добавить", AutoSize = true };
    private readonly Button btnFile = new() { Text = "Файл...", AutoSize = true };
    private readonly TextBox txtSources = new()
    {
        Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9F)
    };
    private readonly Button btnTxt = new() { Text = "Список из txt...", AutoSize = true };
    private readonly Button btnS3 = new() { Text = "Из S3...", AutoSize = true };
    private readonly Button btnOk = new() { Text = "OK", Width = 90 };
    private readonly Button btnCancel = new() { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 90 };
    private readonly Func<string, string> uniqueSlug;
    private readonly AppSettings settings;
    private bool slugEdited;
    private bool settingSlug;

    public string NetworkName { get; private set; } = "";
    public string Slug { get; private set; } = "";
    public List<NetworkSource> Sources { get; private set; } = new();

    public NetworkDialog(NetworkConfig? existing, Func<string, string> uniqueSlug, AppSettings settings)
    {
        this.uniqueSlug = uniqueSlug;
        this.settings = settings;
        Text = existing == null ? "Новая сеть" : "Изменить сеть";
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 620);
        CancelButton = btnCancel;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Add(Control c, SizeType type = SizeType.AutoSize, float height = 0)
        {
            layout.RowStyles.Add(new RowStyle(type, height));
            layout.Controls.Add(c);
        }

        Add(new Label { Text = "Название сети:", AutoSize = true });
        Add(txtName);
        Add(new Label
        {
            Text = "Имя папки (латиницей; из него будут папки результата и на S3):", AutoSize = true,
            Margin = new Padding(3, 10, 3, 3)
        });
        Add(txtSlug);

        Add(new Label { Text = "Добавить источник — ссылка (http/https), путь к файлу или адрес s3://бакет/папка/:", AutoSize = true, Margin = new Padding(3, 14, 3, 3) });
        var one = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        one.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        one.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        one.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        one.Controls.Add(txtOne, 0, 0);
        one.Controls.Add(btnAddOne, 1, 0);
        one.Controls.Add(btnFile, 2, 0);
        Add(one);

        Add(new Label
        {
            Text = "Список источников: по одному в строке. Можно дописать через «;» название магазина и регион. " +
                   "Строки с # в начале пропускаются. Для большого количества ссылок вставьте их списком сюда или загрузите из txt.",
            AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 10, 3, 3)
        });
        Add(txtSources, SizeType.Percent, 100);

        var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        tools.Controls.Add(btnTxt);
        tools.Controls.Add(btnS3);
        Add(tools);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill,
            Margin = new Padding(0, 10, 0, 0)
        };
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnOk);
        Add(buttons);

        Controls.Add(layout);

        if (existing != null)
        {
            txtName.Text = existing.Name;
            txtSlug.Text = existing.Slug;
            slugEdited = true;
            txtSources.Text = string.Join(Environment.NewLine, existing.Sources.Select(FormatSource));
        }

        txtName.TextChanged += (_, _) =>
        {
            if (slugEdited) return;
            settingSlug = true;
            txtSlug.Text = Translit.Slug(txtName.Text);
            settingSlug = false;
        };
        txtSlug.TextChanged += (_, _) => { if (!settingSlug) slugEdited = true; };
        btnAddOne.Click += (_, _) => AddOne();
        txtOne.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            AddOne();
        };
        btnFile.Click += (_, _) => AddFiles();
        btnTxt.Click += (_, _) => AddFromTxt();
        btnS3.Click += (_, _) => AddFromS3();
        btnOk.Click += (_, _) => Accept();
    }

    private static string FormatSource(NetworkSource s)
    {
        if (s.Label == null && s.Region == null) return s.Source;
        return s.Source + ";" + s.Label + (s.Region != null ? ";" + s.Region : "");
    }

    private static bool IsValidSource(string src) =>
        Converter.IsUrl(src) || (S3Path.IsS3(src) && S3Path.TryParse(src, out _, out _)) || File.Exists(src);

    private void AppendLines(IEnumerable<string> lines)
    {
        var list = lines.ToList();
        if (list.Count == 0) return;
        if (txtSources.Text.Length > 0 && !txtSources.Text.EndsWith('\n')) txtSources.AppendText(Environment.NewLine);
        txtSources.AppendText(string.Join(Environment.NewLine, list) + Environment.NewLine);
    }

    private void AddOne()
    {
        string text = txtOne.Text.Trim().Trim('"');   // кавычки остаются при вставке пути через «Копировать как путь»
        if (text.Length == 0) return;
        if (!IsValidSource(text))
        {
            MessageBox.Show(this, "Укажите ссылку (http:// или https://), существующий файл или адрес s3://бакет/папка/.", Text);
            return;
        }
        AppendLines(new[] { text });
        txtOne.Clear();
        txtOne.Focus();
    }

    private void AddFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Фиды|*.json;*.xml;*.yml|Все файлы|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AppendLines(dlg.FileNames);
    }

    private void AddFromS3()
    {
        if (!settings.ConfiguredInputs.Any())
        {
            MessageBox.Show(this,
                "Сначала добавьте подключение к S3: главное окно → «Настройки...» → вкладка «S3: входящий 1».", Text);
            return;
        }
        using var dlg = new S3BrowserDialog(settings, S3BrowseMode.Sources);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AppendLines(dlg.Result);
    }

    private void AddFromTxt()
    {
        using var dlg = new OpenFileDialog { Filter = "Списки|*.txt;*.csv;*.tsv|Все файлы|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            byte[] bytes = File.ReadAllBytes(dlg.FileName);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { text = Encoding.GetEncoding(1251).GetString(bytes); }
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

            // относительные пути считаем от папки списка
            string folder = Path.GetDirectoryName(Path.GetFullPath(dlg.FileName)) ?? "";
            var lines = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = line.Split(';', '\t');
                string src = parts[0].Trim().Trim('"');
                if (!Converter.IsUrl(src) && !S3Path.IsS3(src) && !Path.IsPathRooted(src)) parts[0] = Path.Combine(folder, src);
                lines.Add(string.Join(";", parts));
            }
            AppendLines(lines);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось прочитать список: " + ex.Message, Text);
        }
    }

    private void Accept()
    {
        string name = txtName.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Введите название сети.", Text);
            return;
        }

        // то, что набрано в поле «добавить источник», но не добавлено кнопкой, тоже учитываем
        if (txtOne.Text.Trim().Length > 0)
        {
            AddOne();
            if (txtOne.Text.Trim().Length > 0) return;
        }

        var sources = new List<NetworkSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        foreach (var raw in txtSources.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(';', '\t');
            string src = parts[0].Trim().Trim('"');
            if (src.Length == 0) continue;
            if (!IsValidSource(src)) { bad.Add(line); continue; }
            if (!seen.Add(src)) continue;
            sources.Add(new NetworkSource
            {
                Source = src,
                Label = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : null,
                Region = parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : null
            });
        }

        if (bad.Count > 0)
        {
            MessageBox.Show(this,
                "Эти строки не ссылки (http/https), не адреса s3:// и не существующие файлы:\n\n" + string.Join("\n", bad.Take(8)) +
                (bad.Count > 8 ? $"\n… и ещё {bad.Count - 8}" : ""), Text);
            return;
        }

        NetworkName = name;
        Slug = uniqueSlug(txtSlug.Text.Trim().Length > 0 ? txtSlug.Text : name);
        Sources = sources;
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Настройки: общие параметры и подключения S3 (два входящих — только чтение, одно исходящее — для выгрузки результата).</summary>
public sealed class AppSettingsDialog : Form
{
    private readonly TextBox txtData = new() { Dock = DockStyle.Fill };
    private readonly TextBox txtCache = new() { Dock = DockStyle.Fill };
    private readonly TextBox txtOut = new() { Dock = DockStyle.Fill };
    private readonly CheckBox chkKeep = new()
    {
        Text = "Удалять скачанные исходные файлы после успешной сборки (иначе повторный запуск не скачивает файл заново, если он не менялся)", AutoSize = true
    };
    private readonly CheckBox chkKeepJson = new()
    {
        Text = "Оставлять локальную копию JSON после загрузки на S3", AutoSize = true
    };
    private readonly NumericUpDown numTz = new() { Minimum = -12, Maximum = 14, Width = 70 };
    private readonly NumericUpDown numStale = new() { Minimum = 1, Maximum = 365, Width = 70 };
    private readonly NumericUpDown numThreads = new() { Minimum = 1, Maximum = 64, Width = 70 };
    private readonly CheckBox chkEconomy = new()
    {
        Text = "Режим для слабого ПК (пониженный приоритет, чтобы компьютер не «ложился»)", AutoSize = true
    };
    private readonly Button btnOk = new() { Text = "OK", Width = 90 };
    private readonly Button btnCancel = new() { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 90 };
    private readonly S3Editor s3a;
    private readonly S3Editor s3b;
    private readonly S3Editor s3out;

    public AppSettings Result { get; private set; }

    public AppSettingsDialog(AppSettings current)
    {
        Result = current;
        Text = "Настройки";
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 560);
        CancelButton = btnCancel;

        txtData.Text = current.DataDir;
        txtCache.Text = current.CacheDir;
        txtOut.Text = current.OutputDir;
        chkKeep.Checked = !current.KeepSources;   // галочка «удалять» — обратная к хранимой «хранить»
        chkKeepJson.Checked = current.KeepLocalJson;
        numTz.Value = current.TimezoneHours;
        numStale.Value = current.StaleDays;
        numThreads.Value = current.Threads;
        chkEconomy.Checked = current.Economy;

        // ---- вкладка «Общие»
        var general = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new Padding(12) };
        general.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        general.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        int row = 0;
        void FolderRow(string caption, TextBox box)
        {
            var browse = new Button { Text = "Обзор...", AutoSize = true };
            browse.Click += (_, _) =>
            {
                using var dlg = new FolderBrowserDialog { SelectedPath = box.Text };
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
            };
            general.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) }, 0, row);
            general.Controls.Add(box, 1, row);
            general.Controls.Add(browse, 2, row);
            row++;
        }
        void Wide(Control c, Padding? margin = null)
        {
            c.Margin = margin ?? new Padding(3, 6, 3, 6);
            general.Controls.Add(c, 0, row);
            general.SetColumnSpan(c, 3);
            row++;
        }
        void NumRow(string caption, NumericUpDown num)
        {
            general.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) }, 0, row);
            general.Controls.Add(num, 1, row);
            num.Anchor = AnchorStyles.Left;
            row++;
        }

        FolderRow("Папка данных (сети, история):", txtData);
        FolderRow("Папка кэша (исходные файлы):", txtCache);
        FolderRow("Папка результатов:", txtOut);
        Wide(chkKeep, new Padding(3, 10, 3, 6));
        Wide(chkKeepJson);
        NumRow("Часовой пояс (часы от UTC):", numTz);
        NumRow("Фид «старый», если не обновлялся дней и больше:", numStale);
        NumRow("Потоков скачивания картинок:", numThreads);
        Wide(chkEconomy);
        Wide(new Label
        {
            Text = "Если сменить папку данных, сети читаются из новой папки. Старые файлы сетей можно скопировать в её подпапку networks.",
            AutoSize = true, MaximumSize = new Size(660, 0), ForeColor = SystemColors.GrayText
        }, new Padding(3, 14, 3, 3));

        var tabGeneral = new TabPage("Общие") { UseVisualStyleBackColor = true };
        tabGeneral.Controls.Add(general);

        // ---- вкладки S3
        var inputs = current.S3Inputs;
        s3a = new S3Editor(inputs.Count > 0 ? inputs[0] : null, null, null, false,
            "Откуда программа берёт сырые фиды (только чтение). Укажите сервер, бакет и ключи доступа.");
        s3b = new S3Editor(inputs.Count > 1 ? inputs[1] : null, inputs.Count > 0 ? inputs[0] : null, k => s3a.EffectiveSecret(k), false,
            "Второй источник данных (необязательно). Оставьте бакет пустым, если он не нужен. " +
            "Бакеты первого и второго подключения должны называться по-разному.");
        s3out = new S3Editor(current.S3Output, null, null, true,
            "Куда выгружается готовый результат. Результаты и картинки лягут в бакет по схеме: " +
            "бакет / корневая папка / сеть / ... Загрузка включается в настройках каждой сети.");

        var tabA = new TabPage("S3: входящий 1") { UseVisualStyleBackColor = true };
        tabA.Controls.Add(s3a.Panel);
        var tabB = new TabPage("S3: входящий 2") { UseVisualStyleBackColor = true };
        tabB.Controls.Add(s3b.Panel);
        var tabOut = new TabPage("S3: исходящий") { UseVisualStyleBackColor = true };
        tabOut.Controls.Add(s3out.Panel);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.AddRange(new[] { tabGeneral, tabA, tabB, tabOut });

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10)
        };
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnOk);

        Controls.Add(tabs);
        Controls.Add(buttons);

        btnOk.Click += (_, _) => Accept(tabs, tabA, tabB, tabOut);
    }

    private void Accept(TabControl tabs, TabPage tabA, TabPage tabB, TabPage tabOut)
    {
        foreach (var (editor, tab) in new[] { (s3a, tabA), (s3b, tabB), (s3out, tabOut) })
        {
            string? problem = editor.Validate();
            if (problem == null) continue;
            tabs.SelectedTab = tab;
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var a = s3a.ToConnection();
        var b = s3b.ToConnection();
        if (a != null && b != null && string.Equals(a.Bucket, b.Bucket, StringComparison.OrdinalIgnoreCase))
        {
            tabs.SelectedTab = tabB;
            MessageBox.Show(this,
                "Бакеты двух входящих подключений называются одинаково. Адреса s3://бакет/... различаются по имени бакета, поэтому они должны быть разными.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = new AppSettings
        {
            DataDir = txtData.Text.Trim(),
            CacheDir = txtCache.Text.Trim(),
            OutputDir = txtOut.Text.Trim(),
            KeepSources = !chkKeep.Checked,
            KeepLocalJson = chkKeepJson.Checked,
            TimezoneHours = (int)numTz.Value,
            StaleDays = (int)numStale.Value,
            Threads = (int)numThreads.Value,
            Economy = chkEconomy.Checked,
            S3Inputs = new[] { a, b }.Where(x => x != null).Select(x => x!).ToList(),
            S3Output = s3out.ToConnection()
        };
        Result.Normalize();
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Поля одного подключения к S3 (вкладка настроек) с кнопкой проверки.</summary>
internal sealed class S3Editor
{
    private readonly TextBox endpoint = new() { Dock = DockStyle.Fill };
    private readonly TextBox region = new() { Dock = DockStyle.Fill };
    private readonly TextBox bucket = new() { Dock = DockStyle.Fill };
    private readonly TextBox keyId = new() { Dock = DockStyle.Fill };
    private readonly TextBox secret = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox rootFolder = new() { Dock = DockStyle.Fill };
    private readonly TextBox publicBase = new() { Dock = DockStyle.Fill };
    private readonly Button test = new() { Text = "Проверить подключение", AutoSize = true };
    private readonly Label info = new() { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(3, 8, 3, 3) };
    private readonly S3Connection? orig;
    private readonly Func<string, string>? borrowSecret;
    private readonly bool isOutput;

    public TableLayoutPanel Panel { get; }

    public S3Editor(S3Connection? existing, S3Connection? prefillFrom, Func<string, string>? borrowSecret, bool isOutput, string description)
    {
        orig = existing;
        this.borrowSecret = borrowSecret;
        this.isOutput = isOutput;

        Panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        Panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        var desc = new Label
        {
            Text = description, AutoSize = true, MaximumSize = new Size(660, 0), ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 3, 3, 12)
        };
        Panel.Controls.Add(desc, 0, row);
        Panel.SetColumnSpan(desc, 2);
        row++;

        void Add(string caption, Control c)
        {
            Panel.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) }, 0, row);
            Panel.Controls.Add(c, 1, row);
            row++;
        }

        Add("Адрес сервиса:", endpoint);
        Add("Регион:", region);
        Add("Бакет:", bucket);
        Add("Идентификатор ключа:", keyId);
        Add("Секретный ключ:", secret);
        if (isOutput)
        {
            Add("Корневая папка в бакете:", rootFolder);
            Add("Базовый адрес ссылок:", publicBase);
        }

        Panel.Controls.Add(test, 1, row++);
        Panel.Controls.Add(info, 0, row);
        Panel.SetColumnSpan(info, 2);

        var defaults = isOutput
            ? new S3Connection { Endpoint = "msk.s3.wgos.org", Region = "us-east-1", Bucket = "edadeal" }
            : new S3Connection();
        endpoint.Text = existing?.Endpoint ?? prefillFrom?.Endpoint ?? defaults.Endpoint;
        region.Text = existing?.Region ?? prefillFrom?.Region ?? defaults.Region;
        bucket.Text = existing?.Bucket ?? defaults.Bucket;
        keyId.Text = existing?.AccessKeyId ?? prefillFrom?.AccessKeyId ?? "";
        rootFolder.Text = existing?.RootFolder ?? defaults.RootFolder;
        publicBase.Text = existing?.PublicBaseUrl ?? (isOutput ? "https://msk.s3.wgos.org/edadeal" : "");
        if (!string.IsNullOrEmpty(existing?.SecretKeyEncrypted))
            info.Text = "Секретный ключ сохранён (в зашифрованном виде). Оставьте поле пустым, чтобы не менять его.";

        test.Click += async (_, _) => await TestAsync();
    }

    /// <summary>Введённый секретный ключ, либо сохранённый (если идентификатор не менялся), либо взятый у первого подключения.</summary>
    public string EffectiveSecret(string id)
    {
        if (secret.Text.Length > 0) return secret.Text;
        if (orig is not null && orig.AccessKeyId == id) return orig.GetSecret();
        return borrowSecret?.Invoke(id) ?? "";
    }

    /// <summary>
    /// Текст проблемы или null. Входящее подключение без бакета не используется: остальные поля (например, подставленные
    /// из первого подключения) игнорируются и проблемой не являются. Исходящее без бакета, но с ключом — ошибка.
    /// </summary>
    public string? Validate()
    {
        string b = bucket.Text.Trim(), id = keyId.Text.Trim();
        if (b.Length == 0 && !isOutput) return null;
        if (b.Length == 0 && id.Length == 0 && secret.Text.Length == 0) return null;
        if (b.Length == 0) return "Укажите бакет.";
        if (id.Length == 0) return "Укажите идентификатор ключа.";
        if (EffectiveSecret(id).Length == 0) return "Введите секретный ключ.";
        if (endpoint.Text.Trim().Length == 0) return "Укажите адрес сервиса.";
        return null;
    }

    /// <summary>Подключение из введённых полей; null — если вкладка не заполнена.</summary>
    public S3Connection? ToConnection()
    {
        string b = bucket.Text.Trim();
        if (b.Length == 0) return null;
        var c = new S3Connection
        {
            Endpoint = endpoint.Text.Trim(),
            Region = region.Text.Trim().Length > 0 ? region.Text.Trim() : "ru-central1",
            Bucket = b,
            AccessKeyId = keyId.Text.Trim(),
            RootFolder = isOutput ? rootFolder.Text.Trim().Trim('/') : "",
            PublicBaseUrl = isOutput ? publicBase.Text.Trim().TrimEnd('/') : ""
        };
        string s = EffectiveSecret(c.AccessKeyId);
        if (s.Length > 0) c.SetSecret(s);
        return c;
    }

    private async Task TestAsync()
    {
        string b = bucket.Text.Trim(), id = keyId.Text.Trim();
        string s = EffectiveSecret(id);
        if (b.Length == 0 || id.Length == 0 || s.Length == 0)
        {
            info.Text = "Заполните бакет, идентификатор и секретный ключ.";
            return;
        }

        test.Enabled = false;
        info.Text = "Проверяю подключение...";
        try
        {
            var client = new S3Client(endpoint.Text, region.Text, id, s);
            var listing = await Task.Run(() => client.ListAsync(b, "", CancellationToken.None));
            info.Text = $"Подключение успешно. Папок в бакете: {listing.Folders.Count}.";
        }
        catch (Exception ex)
        {
            info.Text = "Не получилось: " + ex.Message;
        }
        finally
        {
            test.Enabled = true;
        }
    }
}


/// <summary>Название и target источника (для YML: регион, под которым он станет отдельным каталогом).</summary>
public sealed class SourceEditDialog : Form
{
    private readonly TextBox txtLabel = new() { Dock = DockStyle.Fill };
    private readonly TextBox txtTarget = new() { Dock = DockStyle.Fill };
    private readonly Button btnOk = new() { Text = "OK", Width = 90, DialogResult = DialogResult.OK };
    private readonly Button btnCancel = new() { Text = "Отмена", Width = 90, DialogResult = DialogResult.Cancel };

    public string? LabelText { get; private set; }
    public string? Target { get; private set; }

    public SourceEditDialog(NetworkSource src)
    {
        Text = "Источник";
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 250);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        txtLabel.Text = src.Label ?? "";
        txtTarget.Text = src.Region ?? "";

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control c)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(c);
        }

        Add(new Label { Text = src.Source, AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText });
        Add(new Label { Text = "Название (необязательно):", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
        Add(txtLabel);
        Add(new Label { Text = "Target (регион) — для YML (необязательно):", AutoSize = true, Margin = new Padding(3, 12, 3, 3) });
        Add(txtTarget);
        Add(new Label
        {
            Text = "Если указать, этот YML станет отдельным каталогом с таким target (например, «Москва» или «Россия»), " +
                   "а не частью общего каталога. Пусто — как раньше.",
            AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3)
        });
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0)
        };
        buttons.Controls.Add(btnCancel);
        buttons.Controls.Add(btnOk);
        Add(buttons);
        Controls.Add(layout);

        FormClosing += (_, _) =>
        {
            if (DialogResult != DialogResult.OK) return;
            LabelText = string.IsNullOrWhiteSpace(txtLabel.Text) ? null : txtLabel.Text.Trim();
            Target = string.IsNullOrWhiteSpace(txtTarget.Text) ? null : txtTarget.Text.Trim();
        };
    }
}
