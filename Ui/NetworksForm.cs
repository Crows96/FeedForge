namespace FeedForge;

/// <summary>
/// Главное окно: слева список сетей, справа настройки выбранной сети, её источники и журнал.
/// Настройки каждой сети сохраняются сразу при изменении. Старт обрабатывает отмеченные сети:
/// проверка свежести → кэш исходных файлов → конвертация (пока в Excel; сборка JSON появится на шаге 3).
/// </summary>
public sealed class NetworksForm : Form
{
    private AppStorage store;
    private SourceCache cache;
    private CancellationTokenSource? cts;
    private bool applying;
    private bool busy;
    private NetworkConfig? current;
    private RecodeOptions recode = RecodeOptions.Default;
    private readonly Dictionary<string, string> freshness = new();

    // ---- верхняя панель
    private readonly Button btnStart = new() { Text = "▶ Старт", AutoSize = true };
    private readonly Button btnStop = new() { Text = "■ Стоп", AutoSize = true, Enabled = false };
    private readonly Button btnAdd = new() { Text = "+ Сеть...", AutoSize = true };
    private readonly Button btnEdit = new() { Text = "Изменить...", AutoSize = true };
    private readonly Button btnDelete = new() { Text = "Удалить", AutoSize = true };
    private readonly Button btnFresh = new() { Text = "Проверить свежесть", AutoSize = true };
    private readonly Button btnSettings = new() { Text = "Настройки...", AutoSize = true };
    private readonly Button btnOld = new() { Text = "JSON → Excel", AutoSize = true };
    private readonly Button btnPreview = new() { Text = "Предрасчёт", AutoSize = true };
    private readonly Button btnAnalyze = new() { Text = "Анализ сети", AutoSize = true };

    /// <summary>Json — собрать сокращённый JSON; Preview — только посчитать, что получится; Excel — выгрузка в Excel с картинками (локально).</summary>
    private enum RunKind { Json, Excel, Preview, Analyze }
    private readonly Button btnS3Nets = new() { Text = "Сети из S3...", AutoSize = true };
    private readonly SplitContainer split = new();

    // ---- список
    private readonly ListView list = new()
    {
        View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false, MultiSelect = false,
        Dock = DockStyle.Fill, GridLines = true
    };

    // ---- вкладки
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage tabSettings = new("Настройки");
    private readonly TabPage tabSources = new("Источники");
    private readonly TabPage tabLog = new("Журнал");
    private readonly Button btnEditSource = new() { Text = "Изменить источник...", AutoSize = true };
    private readonly ListBox lstSources = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly TextBox log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Font = new Font("Consolas", 9F)
    };

    // ---- настройки сети
    private readonly NumericUpDown numSpread = new() { DecimalPlaces = 1, Minimum = 0, Maximum = 1000, Width = 70 };
    private readonly ComboBox cmbPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
    private readonly Label lblPresetHint = new()
    {
        AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 8)
    };
    private readonly FlowLayoutPanel advPanel = new()
    {
        AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Visible = false
    };
    private readonly CheckBox chkPreferPromo = new() { Text = "Сначала акционные: если их не хватает до лимита, добираются обычные", AutoSize = true };
    private readonly FlowLayoutPanel selPanel = new()
    {
        AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Visible = false
    };
    private readonly TabPage tabErrors = new("Ошибки");
    private readonly ListView lstErrors = new()
    {
        View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, Dock = DockStyle.Fill
    };
    private readonly CheckBox chkSpreadOff = new() { Text = "Не проверять разброс цен (у каждого каталога своя цена)", AutoSize = true };
    private readonly NumericUpDown numCatalogDays = new() { Minimum = 0, Maximum = 60, Width = 70 };
    private readonly NumericUpDown numYmlDays = new() { Minimum = 1, Maximum = 60, Width = 70 };
    private readonly TextBox txtYmlConditions = new()
    {
        Multiline = true, Width = 460, Height = 62, ScrollBars = ScrollBars.Vertical, PlaceholderText = "Пусто — стандартный текст условий"
    };
    private readonly ComboBox cmbPrice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly CheckBox chkShop = new() { Text = "Фильтр по охвату магазинов, не менее %:", AutoSize = true };
    private readonly NumericUpDown numShop = new() { Minimum = 1, Maximum = 100, Width = 70 };
    private readonly ComboBox cmbGeo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
    private readonly CheckBox chkSubject = new() { Text = "Регион — субъект РФ (область, край, республика) по справочнику", AutoSize = true };
    private readonly CheckBox chkPromo = new() { Text = "Только акционные офферы (с ключом price_old)", AutoSize = true };
    private readonly CheckBox chkMerge = new() { Text = "Собирать только уникальные товары", AutoSize = true };
    private readonly CheckBox chkCombine = new() { Text = "Объединить файлы сети в один результат", AutoSize = true };
    private readonly CheckBox chkRepeats = new() { Text = "Один товар может быть в разных каталогах (иначе — только в одном)", AutoSize = true };
    private readonly CheckBox chkUpload = new() { Text = "Загружать JSON на S3 (исходящее подключение)", AutoSize = true };
    private readonly CheckBox chkExcludeSmall = new() { Text = "Исключать офферы с картинкой меньше 400 px (иначе остаются как есть)", AutoSize = true };
    private readonly TabPage tabHistory = new("История");
    private readonly ListView lstHistory = new()
    {
        View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false, Dock = DockStyle.Fill
    };
    private readonly CheckBox chkImagesS3 = new() { Text = "Сжимать картинки и класть на S3, подставляя ссылки в JSON", AutoSize = true };
    private readonly ComboBox cmbLimitMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly NumericUpDown numLimit = new() { Minimum = 0, Maximum = 1000000, Width = 90 };
    private readonly Button btnRecode = new() { Text = "Сжатие картинок (JSON на S3 и Excel)...", AutoSize = true };
    private readonly Label lblRecode = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 6, 3, 0) };
    private readonly ComboBox cmbStale = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly Label lblSlug = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    // ---- низ
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, Style = ProgressBarStyle.Continuous };   // 1000 делений = точность 0,1%
    private readonly Label lblPercent = new() { Dock = DockStyle.Right, Width = 84, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 11F, FontStyle.Bold) };
    private readonly Label lblStatus = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label lblTimes = new() { Dock = DockStyle.Bottom, Height = 20, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer progressTimer = new() { Interval = 250 };
    private System.Diagnostics.Stopwatch? runWatch;
    private double overallPercent, smoothedTotalSec;          // общий прогресс 0–100 и сглаженная оценка полного времени
    private string statusBase = "", stageText = "", baseTitle = "";

    public NetworksForm()
    {
        store = AppStorage.Load();
        cache = new SourceCache(store.Settings.CacheDir, store.Settings);

        Text = "FeedForge";
        Font = new Font("Segoe UI", 9F);
        Width = 1250;
        Height = 780;
        MinimumSize = new Size(1000, 600);
        StartPosition = FormStartPosition.CenterScreen;

        FillCombos();
        BuildLayout();
        WireEvents();

        progressTimer.Tick += OnProgressTick;
        RefreshList(null);
        lblStatus.Text = "Добавьте сеть и нажмите «Старт».";
        Shown += (_, _) =>
        {
            // ширина считается после показа окна (с учётом масштаба экрана): слева список, справа настройки
            try
            {
                split.Panel1MinSize = 360;
                split.Panel2MinSize = 480;
                split.SplitterDistance = Math.Clamp((int)(split.Width * 0.42), 360, Math.Max(360, split.Width - 480));
            }
            catch { }
            if (store.Warning != null) MessageBox.Show(this, store.Warning, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
    }

    // ================================================================== построение окна

    private void FillCombos()
    {
        cmbPreset.Items.AddRange(new object[]
        {
            "Как в исходных файлах (рекомендуется): у каждого каталога свои цены",
            "Общий каталог для всей России: один оффер на товар, цена максимальная",
            "Свои настройки (расширенные)"
        });
        cmbPrice.Items.AddRange(new object[] { "Минимальная", "Максимальная", "Средняя", "Медиана" });
        // для JSON: отдельных файлов по регионам и магазинам не бывает, их выбор — в окне настроек Excel
        // порядок пунктов: 0 → RegionMode.Ignore, 1 → AllRegions, 2 → RegionsMerged (см. GeoToIndex / IndexToGeo)
        cmbGeo.Items.AddRange(new object[]
        {
            "Не разбивать — один результат на все магазины",
            "Только товары, которые есть во всех регионах или магазинах",
            "По регионам — охват и цены считаются в каждом регионе отдельно"
        });
        cmbLimitMode.Items.AddRange(new object[]
        {
            "На весь результат", "На каждый каталог (магазин / регион)"
        });
        cmbStale.Items.AddRange(new object[]
        {
            "Спрашивать, конвертировать ли", "Всегда конвертировать", "Всегда пропускать"
        });
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(3, 3, 3, 3) };
        foreach (var c in controls)
        {
            if (c is Label l && l.Margin == new Padding(3)) l.Margin = new Padding(0, 6, 6, 0);
            row.Controls.Add(c);
        }
        return row;
    }

    private static Label Lbl(string text) => new() { Text = text, AutoSize = true };

    private static Label Hdr(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 9F), Margin = new Padding(3, 12, 3, 2)
    };

    private void BuildLayout()
    {
        // колонки списка
        list.Columns.Add("Сеть", 190);
        list.Columns.Add("Ист.", 45);
        list.Columns.Add("Свежесть", 130);
        list.Columns.Add("Запуск", 105);
        list.Columns.Add("Статус", 200);

        // вкладка «Настройки»
        var settings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(10)
        };
        settings.Controls.Add(lblSlug);

        settings.Controls.Add(Hdr("Как собирать результат"));
        settings.Controls.Add(cmbPreset);
        settings.Controls.Add(lblPresetHint);
        settings.Controls.Add(chkPromo);
        settings.Controls.Add(chkPreferPromo);
        settings.Controls.Add(chkCombine);
        settings.Controls.Add(Row(Lbl("Срок действия каталогов, дней:"), numCatalogDays, Lbl("(0 — как в файле; иначе date_end = date_start + N)")));

        // Отбор по цене и охвату: виден в режимах «Общий каталог» и «Свои настройки»
        selPanel.Controls.Add(Hdr("Цена и охват"));
        selPanel.Controls.Add(Row(Lbl("Макс. разброс цен, %:"), numSpread, Lbl("  Цена:"), cmbPrice));
        selPanel.Controls.Add(chkSpreadOff);
        selPanel.Controls.Add(Row(chkShop, numShop));
        selPanel.Controls.Add(Hdr("Каталог «Россия» (общий каталог и YML)"));
        selPanel.Controls.Add(Row(Lbl("Срок действия, дней:"), numYmlDays));
        selPanel.Controls.Add(Row(Lbl("Условия:"), txtYmlConditions));
        settings.Controls.Add(selPanel);

        settings.Controls.Add(Hdr("Лимит офферов в JSON"));
        settings.Controls.Add(Row(Lbl("Лимит:"), numLimit, Lbl("(0 — без лимита)")));
        settings.Controls.Add(Row(Lbl("Применять:"), cmbLimitMode));
        settings.Controls.Add(chkRepeats);

        settings.Controls.Add(Hdr("Выгрузка на S3"));
        settings.Controls.Add(chkUpload);
        settings.Controls.Add(chkImagesS3);
        settings.Controls.Add(chkExcludeSmall);
        settings.Controls.Add(Row(btnRecode, lblRecode));


        // Расширенные настройки: видны только в режиме «Свои настройки»
        advPanel.Controls.Add(Hdr("Гео и объединение товаров"));
        advPanel.Controls.Add(Row(Lbl("Гео в JSON:"), cmbGeo));
        advPanel.Controls.Add(chkSubject);
        advPanel.Controls.Add(chkMerge);

        advPanel.Controls.Add(Hdr("Запуск"));
        advPanel.Controls.Add(Row(Lbl("Если фид старый:"), cmbStale));
        settings.Controls.Add(advPanel);
        tabSettings.Controls.Add(settings);
        tabSettings.Tag = settings;

        var srcHint = new Label
        {
            Text = "Для YML-источника можно задать target (например, «Москва»): тогда он станет отдельным каталогом с этим target " +
                   "вместо общего каталога «Россия». Выберите источник и нажмите «Изменить источник…» (или двойной щелчок).",
            AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 6)
        };
        var srcBottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(6)
        };
        srcBottom.Controls.Add(btnEditSource);
        srcBottom.Controls.Add(srcHint);
        tabSources.Controls.Add(lstSources);
        tabSources.Controls.Add(srcBottom);
        tabLog.Controls.Add(log);
        tabHistory.Controls.Add(lstHistory);
        tabErrors.Controls.Add(lstErrors);
        foreach (var (title, width) in new[] { ("Сеть", 150), ("Что случилось", 340), ("Что можно сделать", 320) })
            lstErrors.Columns.Add(title, width);
        foreach (var (title, width) in new[] { ("Дата", 110), ("Режим", 80), ("Статус", 200), ("Файлов", 55), ("Офферов", 65), ("Картинки (новых / уже было / ошибок)", 210), ("Время", 60) })
            lstHistory.Columns.Add(title, width);
        tabs.TabPages.Add(tabSettings);
        tabs.TabPages.Add(tabSources);
        tabs.TabPages.Add(tabHistory);
        tabs.TabPages.Add(tabErrors);
        tabs.TabPages.Add(tabLog);

        split.Dock = DockStyle.Fill;
        split.Orientation = Orientation.Vertical;
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(tabs);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(6) };
        foreach (var b in new[] { btnStart, btnStop, btnPreview, btnAnalyze, btnAdd, btnS3Nets, btnEdit, btnDelete, btnFresh, btnSettings, btnOld })
        {
            b.Margin = new Padding(3, 3, 9, 3);
            top.Controls.Add(b);
        }

        var progressRow = new Panel { Dock = DockStyle.Top, Height = 26 };
        progressRow.Controls.Add(progress);      // заполняет всё, кроме процента справа
        progressRow.Controls.Add(lblPercent);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 80, Padding = new Padding(6, 4, 6, 4) };
        bottom.Controls.Add(lblStatus);          // сеть и текущий этап
        bottom.Controls.Add(lblTimes);           // прошло / осталось / во сколько закончится
        bottom.Controls.Add(progressRow);

        // порядок: сначала заполняющий элемент, потом нижняя и верхняя панели
        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    private void WireEvents()
    {
        btnAdd.Click += (_, _) => AddNetwork();
        btnS3Nets.Click += (_, _) => AddNetworksFromS3();
        btnEditSource.Click += (_, _) => EditSource();
        lstSources.DoubleClick += (_, _) => EditSource();
        btnEdit.Click += (_, _) => EditNetwork();
        btnDelete.Click += (_, _) => DeleteNetwork();
        btnSettings.Click += (_, _) => OpenSettings();
        btnOld.Click += async (_, _) => await StartExcelAsync();
        btnPreview.Click += async (_, _) => await StartAsync(RunKind.Preview);
        btnAnalyze.Click += async (_, _) => await StartAsync(RunKind.Analyze);
        btnStart.Click += async (_, _) => await StartAsync(RunKind.Json);
        btnStop.Click += (_, _) => cts?.Cancel();
        btnFresh.Click += async (_, _) => await CheckFreshnessAsync();
        btnRecode.Click += (_, _) => EditRecode();

        list.SelectedIndexChanged += (_, _) => ShowCurrent();
        list.ItemChecked += (_, e) =>
        {
            if (applying || e.Item.Tag is not NetworkConfig n) return;
            n.Enabled = e.Item.Checked;
            store.SaveNetwork(n);
        };
        list.DoubleClick += (_, _) => EditNetwork();

        foreach (var c in new Control[] { numSpread, numShop, numLimit, numYmlDays, numCatalogDays })
            ((NumericUpDown)c).ValueChanged += (_, _) => SettingChanged();
        foreach (var c in new[] { cmbPrice, cmbGeo, cmbLimitMode, cmbStale })
            c.SelectedIndexChanged += (_, _) => SettingChanged();
        txtYmlConditions.TextChanged += (_, _) => SettingChanged();
        cmbPreset.SelectedIndexChanged += (_, _) => PresetChanged();
        foreach (var c in new[] { chkSpreadOff, chkShop, chkSubject, chkPromo, chkMerge, chkCombine, chkUpload, chkImagesS3, chkExcludeSmall, chkRepeats, chkPreferPromo })
            c.CheckedChanged += (_, _) => SettingChanged();

        FormClosing += (_, e) =>
        {
            if (!busy) return;
            if (MessageBox.Show(this, "Идёт обработка. Остановить и выйти?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes)
                e.Cancel = true;
            else
                cts?.Cancel();
        };
    }

    // ================================================================== список и панель настроек

    private static string FormatRun(NetworkConfig n) =>
        n.LastRunUtc is { } t ? t.ToLocalTime().ToString("dd.MM HH:mm") : "";

    private void RefreshList(string? selectId)
    {
        applying = true;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var n in store.Networks)
            {
                var item = new ListViewItem(n.Name) { Tag = n, Checked = n.Enabled };
                item.SubItems.Add(n.Sources.Count.ToString());
                item.SubItems.Add(freshness.TryGetValue(n.Id, out var f) ? f : "");
                item.SubItems.Add(FormatRun(n));
                item.SubItems.Add(n.LastStatus);
                list.Items.Add(item);
                if (n.Id == selectId) item.Selected = true;
            }
        }
        finally
        {
            list.EndUpdate();
            applying = false;
        }
        if (list.SelectedItems.Count == 0 && list.Items.Count > 0 && selectId == null) list.Items[0].Selected = true;
        ShowCurrent();
    }

    private void UpdateRow(NetworkConfig n)
    {
        foreach (ListViewItem item in list.Items)
        {
            if (!ReferenceEquals(item.Tag, n)) continue;
            item.Text = n.Name;
            item.SubItems[1].Text = n.Sources.Count.ToString();
            item.SubItems[2].Text = freshness.TryGetValue(n.Id, out var f) ? f : "";
            item.SubItems[3].Text = FormatRun(n);
            item.SubItems[4].Text = n.LastStatus;
        }
    }

    private void ShowCurrent()
    {
        current = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as NetworkConfig : null;
        ((Control)tabSettings.Tag!).Enabled = current != null;
        lstSources.Items.Clear();
        lstHistory.Items.Clear();
        if (current == null)
        {
            lblSlug.Text = "";
            return;
        }

        ShowHistory();
        if (current.Build.PromoOnly && current.PreferPromo)   // взаимоисключающие: при «Только акционные» «Сначала акционные» не нужно
        {
            current.PreferPromo = false;
            store.SaveNetwork(current);
        }
        if (current.LimitMode == 2)   // режим «на каждый результирующий файл» убран: он работал так же, как «на весь результат»
        {
            current.LimitMode = 0;
            store.SaveNetwork(current);
        }
        // старые сети: разбивка на отдельные файлы по регионам/магазинам — это настройка Excel, в JSON её не бывает
        if (current.Build.GeoMode is 2 or 3)
        {
            current.Build.ExcelGeoMode ??= current.Build.GeoMode;
            current.Build.GeoMode = current.Build.GeoMode == 2 ? 4 : 0;
            store.SaveNetwork(current);
        }
        applying = true;
        try
        {
            var s = current.Build;
            cmbPreset.SelectedIndex = Math.Clamp(current.Preset, 0, 2);
            lblPresetHint.Text = PresetHint(cmbPreset.SelectedIndex);
            selPanel.Visible = cmbPreset.SelectedIndex >= 1;    // цена и охват — в режимах «Общий каталог» и «Свои настройки»
            advPanel.Visible = cmbPreset.SelectedIndex == 2;   // остальные расширенные настройки — только в «Свои настройки»
            chkPreferPromo.Checked = current.PreferPromo;
            numSpread.Value = Math.Clamp(s.Spread, numSpread.Minimum, numSpread.Maximum);
            chkSpreadOff.Checked = s.SpreadOff;
            numSpread.Enabled = !s.SpreadOff;
            numYmlDays.Value = Math.Clamp(current.YmlDays, 1, 60);
            numCatalogDays.Value = Math.Clamp(current.CatalogDays, 0, 60);
            txtYmlConditions.Text = current.YmlConditions;
            cmbPrice.SelectedIndex = Math.Clamp(s.PriceMode, 0, cmbPrice.Items.Count - 1);
            chkShop.Checked = s.ShopFilter;
            numShop.Value = Math.Clamp(s.ShopPercent, numShop.Minimum, numShop.Maximum);
            cmbGeo.SelectedIndex = GeoToIndex(s.GeoMode);
            chkSubject.Checked = s.RegionBySubject;
            chkPromo.Checked = s.PromoOnly;
            chkMerge.Checked = s.Merge;
            chkCombine.Checked = s.Combine;
            numLimit.Value = Math.Clamp(s.Limit, 0, (int)numLimit.Maximum);
            cmbLimitMode.SelectedIndex = Math.Clamp(current.LimitMode, 0, cmbLimitMode.Items.Count - 1);
            chkUpload.Checked = current.UploadToS3;
            chkRepeats.Checked = current.AllowRepeats;
            chkImagesS3.Checked = current.ImagesToS3;
            chkExcludeSmall.Checked = current.ExcludeSmallImages;
            recode = BuildRecode(s);
            lblRecode.Text = recode.Describe();
            cmbStale.SelectedIndex = Math.Clamp((int)current.StaleAction, 0, cmbStale.Items.Count - 1);
        }
        finally
        {
            applying = false;
        }

        numShop.Enabled = chkShop.Checked;
        chkPreferPromo.Enabled = !chkPromo.Checked;   // при «Только акционные» неакционных нет — «Сначала акционные» не нужно
        chkPromo.Enabled = !chkPreferPromo.Checked;   // и наоборот: «Сначала акционные» берёт и обычные товары, «Только акционные» их исключает
        lblSlug.Text = $"Папка: {current.Slug}    (Excel: {Path.Combine(store.Settings.OutputDir, current.Slug)};  JSON: {Path.Combine(store.Settings.OutputDir, current.Slug, "json")})";
        foreach (var src in current.Sources)
            lstSources.Items.Add(src.Source + (src.Label != null ? "   [" + src.Label + "]" : "") +
                                 (!string.IsNullOrWhiteSpace(src.Region) ? "   → target: " + src.Region : ""));
    }

    /// <summary>Значения отбора для готовых режимов. 0 — без фильтров по цене и магазинам (максимум товаров), 1 — одна цена на товар.</summary>
    private static void ApplyPresetValues(UiSettings s, int preset)
    {
        if (preset == 0)
        {
            s.SpreadOff = true;
            s.ShopFilter = false;
            s.GeoMode = 0;
            s.RegionBySubject = false;
            s.Merge = true;
        }
        else if (preset == 1)
        {
            s.SpreadOff = false;
            s.Spread = 5;
            s.PriceMode = 1;
            s.ShopFilter = true;
            s.ShopPercent = 100;
            s.GeoMode = 0;
            s.RegionBySubject = false;
            s.Merge = true;
        }
    }

    private void PresetChanged()
    {
        if (applying || current == null) return;
        int idx = Math.Max(0, cmbPreset.SelectedIndex);
        current.Preset = idx;
        if (idx < 2) ApplyPresetValues(current.Build, idx);
        store.SaveNetwork(current);
        ShowCurrent();   // перечитать значения в элементах
    }

    private static string PresetHint(int preset) => preset switch
    {
        0 => "Каталоги, регионы и цены остаются как в файлах. Фильтры по цене и магазинам не применяются, поэтому товаров получается максимум.",
        1 => "Все файлы сети собираются в один каталог с гео «Россия»: товары по названию, цена — максимальная, товары с разбросом цен больше заданного отбрасываются. Лимит — число офферов в этом каталоге.",
        _ => "Все параметры отбора настраиваются вручную: ниже появятся расширенные настройки."
    };

    private void SettingChanged()
    {
        if (applying || current == null) return;
        numShop.Enabled = chkShop.Checked;
        chkPreferPromo.Enabled = !chkPromo.Checked;
        chkPromo.Enabled = !chkPreferPromo.Checked;
        var s = current.Build;
        s.Spread = numSpread.Value;
        s.SpreadOff = chkSpreadOff.Checked;
        numSpread.Enabled = !s.SpreadOff;
        current.YmlDays = (int)numYmlDays.Value;
        current.CatalogDays = (int)numCatalogDays.Value;
        current.YmlConditions = txtYmlConditions.Text.Trim();
        s.PriceMode = Math.Max(0, cmbPrice.SelectedIndex);
        s.ShopFilter = chkShop.Checked;
        s.ShopPercent = numShop.Value;
        s.GeoMode = IndexToGeo(cmbGeo.SelectedIndex);
        s.RegionBySubject = chkSubject.Checked;
        s.PromoOnly = chkPromo.Checked;
        s.Merge = chkMerge.Checked;
        s.Combine = chkCombine.Checked;
        s.Limit = (int)numLimit.Value;
        current.LimitMode = Math.Max(0, cmbLimitMode.SelectedIndex);
        current.UploadToS3 = chkUpload.Checked;
        current.AllowRepeats = chkRepeats.Checked;
        current.PreferPromo = chkPreferPromo.Checked;
        current.ImagesToS3 = chkImagesS3.Checked;
        current.ExcludeSmallImages = chkExcludeSmall.Checked;
        current.StaleAction = (StaleAction)Math.Max(0, cmbStale.SelectedIndex);
        store.SaveNetwork(current);
    }

    private void EditRecode()
    {
        if (current == null) return;
        using var dlg = new RecoderForm(recode);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        recode = dlg.Result;
        lblRecode.Text = recode.Describe();
        var s = current.Build;
        s.RecodeEnabled = recode.Enabled;
        s.RecodeFormat = (int)recode.Format;
        s.RecodeMaxSide = recode.MaxSide;
        s.RecodeQuality = recode.Quality;
        s.RecodeMaxKb = recode.MaxBytes / 1000;
        store.SaveNetwork(current);
    }

    // ================================================================== сети и настройки

    private void AddNetwork()
    {
        using var dlg = new NetworkDialog(null, w => store.UniqueSlug(w, null), store.Settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var n = new NetworkConfig { Name = dlg.NetworkName, Slug = dlg.Slug, Sources = dlg.Sources };
        // новая сеть начинает с настроек, выбранных в старом окне
        n.Build = new UiSettings();
        n.Preset = 0;
        ApplyPresetValues(n.Build, 0);
        store.AddNetwork(n);
        RefreshList(n.Id);
    }

    private void AddNetworksFromS3()
    {
        if (!store.Settings.ConfiguredInputs.Any())
        {
            MessageBox.Show(this, "Сначала добавьте подключение к S3: «Настройки...» → вкладка «S3: входящий 1».", Text);
            return;
        }
        using var dlg = new S3BrowserDialog(store.Settings, S3BrowseMode.Networks);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var known = new HashSet<string>(
            store.Networks.SelectMany(n => n.Sources).Select(x => x.Source), StringComparer.OrdinalIgnoreCase);
        int added = 0, skipped = 0;
        string? lastId = null;
        foreach (var uri in dlg.Result)
        {
            string folder = uri.TrimEnd('/');
            string uriFolder = folder + "/";
            if (known.Contains(uriFolder)) { skipped++; continue; }

            string name = folder[(folder.LastIndexOf('/') + 1)..];
            if (name.Length == 0) name = "network";
            var n = new NetworkConfig
            {
                Name = name,
                Slug = store.UniqueSlug(name, null),
                Sources = new List<NetworkSource> { new() { Source = uriFolder } },
                Build = new UiSettings(),
                Preset = 0
            };
            ApplyPresetValues(n.Build, 0);
            store.AddNetwork(n);
            known.Add(uriFolder);
            lastId = n.Id;
            added++;
        }
        RefreshList(lastId);
        lblStatus.Text = $"Создано сетей: {added}" + (skipped > 0 ? $", уже были: {skipped}" : "") + ".";
    }

    private void EditSource()
    {
        if (current == null || busy) return;
        int idx = lstSources.SelectedIndex;
        if (idx < 0 || idx >= current.Sources.Count)
        {
            MessageBox.Show(this, "Выберите источник в списке.", Text);
            return;
        }
        var src = current.Sources[idx];
        using var dlg = new SourceEditDialog(src);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        src.Label = dlg.LabelText;
        src.Region = dlg.Target;
        store.SaveNetwork(current);
        ShowCurrent();
        lstSources.SelectedIndex = Math.Min(idx, lstSources.Items.Count - 1);
    }

    private void EditNetwork()
    {
        if (current == null) return;
        var n = current;
        using var dlg = new NetworkDialog(n, w => store.UniqueSlug(w, n), store.Settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        n.Name = dlg.NetworkName;
        n.Slug = dlg.Slug;
        n.Sources = dlg.Sources;
        store.SaveNetwork(n);
        RefreshList(n.Id);
    }

    private void DeleteNetwork()
    {
        if (current == null) return;
        var n = current;
        if (MessageBox.Show(this, $"Удалить сеть «{n.Name}» (настройки и источники)?\nФайлы результатов не удаляются.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        store.DeleteNetwork(n);
        long size = cache.SizeBytes(n.Slug);
        if (size > 0 && MessageBox.Show(this, $"Удалить и кэш этой сети ({size / 1024.0 / 1024.0:F0} МБ)?", Text,
                MessageBoxButtons.YesNo) == DialogResult.Yes)
            cache.Clear(n.Slug);
        RefreshList(null);
    }

    private void OpenSettings()
    {
        if (busy) return;
        using var dlg = new AppSettingsDialog(store.Settings);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string oldData = store.Settings.DataDir;
        var s = dlg.Result;
        bool dataChanged = !string.Equals(oldData, s.DataDir, StringComparison.OrdinalIgnoreCase);
        store.Settings.DataDir = s.DataDir;
        store.Settings.CacheDir = s.CacheDir;
        store.Settings.OutputDir = s.OutputDir;
        store.Settings.KeepSources = s.KeepSources;
        store.Settings.KeepLocalJson = s.KeepLocalJson;
        store.Settings.TimezoneHours = s.TimezoneHours;
        store.Settings.StaleDays = s.StaleDays;
        store.Settings.Threads = s.Threads;
        store.Settings.Economy = s.Economy;
        store.Settings.S3Inputs = s.S3Inputs;
        store.Settings.S3Output = s.S3Output;
        store.SaveSettings();
        cache = new SourceCache(store.Settings.CacheDir, store.Settings);

        if (dataChanged)
        {
            store.ReloadNetworks();
            freshness.Clear();
            RefreshList(null);
        }
        else
        {
            ShowCurrent();
        }
    }

    // ================================================================== свежесть

    private sealed record SourceAge(NetworkSource Source, SourceProbe Probe, int? Age);

    /// <summary>Раскрывает S3-папки в файлы и проверяет дату каждого источника, не скачивая его.</summary>
    private async Task<List<SourceAge>> ProbeNetworkAsync(NetworkConfig n, CancellationToken ct)
    {
        var expanded = await cache.ExpandAsync(n.Sources, ct);
        var probes = await Task.WhenAll(expanded.Select(e => e.Known != null ? Task.FromResult(e.Known) : cache.ProbeAsync(e.Source.Source, ct)));
        return expanded.Select((e, i) =>
            new SourceAge(e.Source, probes[i], probes[i].Ok ? Freshness.AgeDays(probes[i].LastModified, store.Settings) : null)).ToList();
    }

    private string DescribeFreshness(List<SourceAge> ages)
    {
        if (ages.Count == 0) return "";
        var known = ages.Where(a => a.Age != null).ToList();
        if (known.Count == 0) return ages.All(a => !a.Probe.Ok) ? "ошибка проверки" : "дата неизвестна";
        int stale = known.Count(a => Freshness.IsStale(a.Age, store.Settings));
        if (stale == 0) return known.Max(a => a.Age) == 0 ? "обновлён сегодня" : "свежий";
        if (known.Count == 1 || stale == known.Count) return Freshness.Describe(known.Min(a => a.Age));
        return $"старых: {stale} из {known.Count}";
    }

    private async Task CheckFreshnessAsync()
    {
        if (busy) return;
        var nets = CheckedNetworks();
        if (nets.Count == 0 && current != null) nets = new List<NetworkConfig> { current };
        if (nets.Count == 0) return;

        SetBusy(true);
        cts = new CancellationTokenSource();
        try
        {
            int i = 0;
            foreach (var n in nets)
            {
                lblStatus.Text = $"Проверяю свежесть: {n.Name} ({++i} из {nets.Count})";
                freshness[n.Id] = "проверяю...";
                UpdateRow(n);
                try
                {
                    var ages = await ProbeNetworkAsync(n, cts.Token);
                    freshness[n.Id] = DescribeFreshness(ages);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    freshness[n.Id] = "ошибка: " + ex.Message.Split('\n')[0];
                }
                UpdateRow(n);
            }
            lblStatus.Text = "Проверка свежести завершена.";
        }
        catch (OperationCanceledException)
        {
            lblStatus.Text = "Проверка остановлена.";
        }
        finally
        {
            cts?.Dispose();
            cts = null;
            SetBusy(false);
        }
    }

    /// <summary>Настройки сжатия картинок из настроек сети (одно место для запуска, справки и окна Excel).</summary>
    private static RecodeOptions BuildRecode(UiSettings s) =>
        new(s.RecodeEnabled, (RecodeFormat)Math.Clamp(s.RecodeFormat, 0, 2),
            Math.Clamp(s.RecodeMaxSide, 0, 10000), Math.Clamp(s.RecodeQuality, 1, 100),
            Math.Clamp(s.RecodeMaxKb, 0, 100000) * 1000);

    // Индексы списка «Гео в JSON» ↔ RegionMode (отдельные файлы по регионам и магазинам для JSON не бывают)
    private static int GeoToIndex(int geo) => geo switch { 1 => 1, 4 => 2, 2 => 2, _ => 0 };
    private static int IndexToGeo(int idx) => idx switch { 1 => 1, 2 => 4, _ => 0 };

    /// <summary>Кнопка «JSON → Excel»: сначала окно настроек, которые есть только у Excel, потом запуск.</summary>
    private async Task StartExcelAsync()
    {
        if (busy) return;
        var nets = CheckedNetworks();
        if (nets.Count == 0)
        {
            MessageBox.Show(this, "Отметьте галочкой хотя бы одну сеть.", Text);
            return;
        }
        string title = nets.Count == 1 ? $"Настройки Excel — {nets[0].Name}" : $"Настройки Excel — отмеченных сетей: {nets.Count}";
        using var dlg = new ExcelSettingsForm(title, nets[0].Build, ExcelSharedSummary(nets[0]), nets.Count);
        var res = dlg.ShowDialog(this);
        if (res == DialogResult.Cancel) return;
        foreach (var n in nets)
        {
            n.Build.ExcelGeoMode = dlg.GeoMode;
            n.Build.ExcelLimit = dlg.Limit;
            n.Build.PartMb = dlg.PartMb;
            n.Build.Zip = dlg.Zip;
            store.SaveNetwork(n);
        }
        if (res == DialogResult.OK) await StartAsync(RunKind.Excel);
    }

    /// <summary>Настройки основного окна, которые Excel тоже использует (показываются в окне настроек Excel для справки).</summary>
    private static string ExcelSharedSummary(NetworkConfig n)
    {
        var s = n.Build;
        string[] prices = { "минимальная", "максимальная", "средняя", "медиана" };
        var recode = BuildRecode(s);
        var parts = new List<string>
        {
            "цена товара: " + prices[Math.Clamp(s.PriceMode, 0, 3)],
            s.SpreadOff ? "разброс цен: не проверяется" : $"разброс цен: не больше {s.Spread}%",
            s.ShopFilter ? $"охват магазинов: не менее {s.ShopPercent}%" : "охват магазинов: не проверяется",
            "только акционные: " + (s.PromoOnly ? "да" : "нет"),
            "собирать только уникальные товары: " + (s.Merge ? "да" : "нет"),
            "объединять файлы сети в один результат: " + (s.Combine ? "да" : "нет"),
            "регион — субъект РФ: " + (s.RegionBySubject ? "да" : "нет"),
            "сжатие картинок: " + recode.Describe()
        };
        return string.Join("\n", parts.Select(p => "• " + p));
    }

    private List<NetworkConfig> CheckedNetworks() =>
        list.CheckedItems.Cast<ListViewItem>().Select(i => (NetworkConfig)i.Tag!).ToList();

    // ================================================================== запуск

    private void SetBusy(bool on)
    {
        busy = on;
        btnStart.Enabled = btnPreview.Enabled = btnAnalyze.Enabled = btnOld.Enabled = btnS3Nets.Enabled = btnAdd.Enabled = btnEdit.Enabled =
            btnDelete.Enabled = btnFresh.Enabled = btnSettings.Enabled = !on;
        btnStop.Enabled = on;
        list.Enabled = !on;
        ((Control)tabSettings.Tag!).Enabled = !on && current != null;
    }

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length > 80) name = name[..80].TrimEnd();
        return string.IsNullOrWhiteSpace(name) ? "result" : name;
    }

    private static string SourceTitle(string source)
    {
        source = source.Trim();
        string name = "result";
        if (Converter.IsUrl(source))
        {
            try
            {
                var u = new Uri(source);
                var seg = u.Segments.Select(s => s.Trim('/')).LastOrDefault(s => s.Length > 0);
                name = !string.IsNullOrEmpty(seg) ? Path.GetFileNameWithoutExtension(seg) : u.Host;
            }
            catch { }
        }
        else
        {
            name = Path.GetFileNameWithoutExtension(source);
        }
        return SafeName(name);
    }

    private int errorCount;

    private void AddError(string network, string what, string todo)
    {
        var item = new ListViewItem(network);
        item.SubItems.Add(what);
        item.SubItems.Add(todo);
        lstErrors.Items.Add(item);
        errorCount++;
        tabErrors.Text = $"Ошибки ({errorCount})";
    }

    private void ClearErrors()
    {
        lstErrors.Items.Clear();
        errorCount = 0;
        tabErrors.Text = "Ошибки";
    }

    // Понятное объяснение ошибки и что с ней делать
    private static (string What, string Todo) Explain(Exception ex)
    {
        string m = ex.Message.Split('\n')[0];
        return ex switch
        {
            S3Exception => (m, "Проверьте подключение: Настройки → вкладки S3, а также права ключа и имя бакета."),
            HttpRequestException => ("Источник недоступен или не отвечает: " + m, "Проверьте ссылку в источниках сети и доступ в интернет, затем запустите ещё раз."),
            FileNotFoundException => (m, "Проверьте путь к файлу в источниках сети."),
            TaskCanceledException or TimeoutException => ("Источник не ответил вовремя.", "Повторите запуск позже."),
            InvalidOperationException when m.Contains("не осталось офферов") =>
                ("Пустой результат: " + m, "Нажмите «Предрасчёт» и смягчите настройки: лимит, разброс цен, охват магазинов."),
            InvalidOperationException when m.Contains("Не настроено исходящее") =>
                (m, "Откройте Настройки → вкладка «S3: исходящий» и заполните ключи."),
            InvalidOperationException when m.Contains("включите сжатие") =>
                (m, "Включите сжатие картинок («Сжатие картинок…») или выключите картинки на S3."),
            _ => (m, "Если ошибка повторяется, откройте вкладку «Журнал» — там подробности.")
        };
    }

    private void RecordRun(NetworkConfig net, RunKind kind, DateTime startedUtc, string status,
                           System.Diagnostics.Stopwatch sw, JsonRunStats s)
    {
        store.AddHistory(net, new RunRecord
        {
            StartedUtc = startedUtc,
            Kind = kind switch { RunKind.Json => "JSON", RunKind.Excel => "Excel", RunKind.Analyze => "Анализ", _ => "Предрасчёт" },
            Status = status,
            Seconds = sw.Elapsed.TotalSeconds,
            Files = s.Files, Offers = s.Offers, Catalogs = s.Catalogs, Bytes = s.Bytes,
            ImagesNew = s.ImagesNew, ImagesReused = s.ImagesReused, ImagesFailed = s.ImagesFailed,
            ExcludedSmall = s.ExcludedSmall, Uploaded = s.Uploaded
        });
        if (ReferenceEquals(net, current)) ShowHistory();
    }

    private static string FormatDuration(double sec) =>
        sec >= 60 ? $"{(int)(sec / 60)} мин {(int)(sec % 60)} с" : $"{sec:F0} с";

    private void ShowHistory()
    {
        lstHistory.Items.Clear();
        if (current == null) return;
        foreach (var r in store.LoadHistory(current))
        {
            string status = r.Status + (r.Uploaded ? " (загружено на S3)" : "");
            var item = new ListViewItem(r.StartedUtc.ToLocalTime().ToString("dd.MM.yy HH:mm"));
            item.SubItems.Add(r.Kind);
            item.SubItems.Add(status);
            item.SubItems.Add(r.Files > 0 ? r.Files.ToString() : "");
            item.SubItems.Add(r.Offers > 0 ? r.Offers.ToString() : "");
            bool hasImages = r.ImagesNew + r.ImagesReused + r.ImagesFailed > 0;
            item.SubItems.Add(hasImages
                ? $"{r.ImagesNew} / {r.ImagesReused} / {r.ImagesFailed}" + (r.ExcludedSmall > 0 ? $"  (исключено маленьких: {r.ExcludedSmall})" : "")
                : "");
            item.SubItems.Add(FormatDuration(r.Seconds));
            lstHistory.Items.Add(item);
        }
    }

    // ---------- Прогресс: проценты с долями, прошло / осталось / во сколько закончится, текущий этап ----------

    private void BeginProgress()
    {
        if (baseTitle.Length == 0) baseTitle = Text;
        overallPercent = 0;
        smoothedTotalSec = 0;
        statusBase = "";
        stageText = "";
        runWatch = System.Diagnostics.Stopwatch.StartNew();
        progress.Value = 0;
        lblPercent.Text = "0,0 %";
        lblTimes.Text = "Прошло 00:00   •   Осталось: считаю…";
        progressTimer.Start();
    }

    /// <summary>Конец запуска: заполняет итоговые подписи. completed = false — остановлено или ошибка.</summary>
    private void FinishProgress(bool completed)
    {
        progressTimer.Stop();
        double elapsed = runWatch?.Elapsed.TotalSeconds ?? 0;
        runWatch = null;
        if (baseTitle.Length > 0) Text = baseTitle;
        if (completed)
        {
            progress.Value = progress.Maximum;
            lblPercent.Text = "100 %";
            lblTimes.Text = $"Готово за {FormatClock(elapsed)}";
        }
        else
        {
            progress.Value = 0;
            lblPercent.Text = "";
            lblTimes.Text = $"Остановлено через {FormatClock(elapsed)}";
        }
    }

    private void StopProgressTimer()
    {
        progressTimer.Stop();
        runWatch = null;
        if (baseTitle.Length > 0) Text = baseTitle;
    }

    // Строка журнала — текущий этап (служебные заголовки сетей пропускаем)
    private void NoteStage(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s.StartsWith("=====") || s.StartsWith("#####") || s.StartsWith("---")) return;
        stageText = s.Length > 120 ? s[..120] + "…" : s;
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        if (runWatch == null) return;
        double pct = Math.Clamp(overallPercent, 0, 100);
        progress.Value = Math.Clamp((int)Math.Round(pct * 10), 0, progress.Maximum);
        lblPercent.Text = $"{pct:0.0} %";
        Text = $"{baseTitle} — {pct:0.0} %";   // процент виден и на кнопке панели задач

        double elapsed = runWatch.Elapsed.TotalSeconds;
        string times = $"Прошло {FormatClock(elapsed)}";
        if (pct >= 1.0 && elapsed >= 3)
        {
            // Оценка полного времени = прошло / доля выполненного; сглаживаем, чтобы цифры не прыгали
            double estimate = elapsed / (pct / 100.0);
            smoothedTotalSec = smoothedTotalSec <= 0 ? estimate : smoothedTotalSec * 0.9 + estimate * 0.1;
            double remain = Math.Max(0, smoothedTotalSec - elapsed);
            times += $"   •   Осталось ≈ {FormatClock(remain)}   •   Закончится ≈ {DateTime.Now.AddSeconds(remain):HH:mm}";
        }
        else times += "   •   Осталось: считаю…";
        lblTimes.Text = times;

        lblStatus.Text = stageText.Length > 0 ? $"{statusBase} — {stageText}" : statusBase;
    }

    private static string FormatClock(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    private async Task StartAsync(RunKind kind)
    {
        if (busy) return;
        var nets = CheckedNetworks();
        if (nets.Count == 0)
        {
            MessageBox.Show(this, "Отметьте галочкой хотя бы одну сеть.", Text);
            return;
        }

        SetBusy(true);
        tabs.SelectedTab = tabLog;
        log.Clear();
        BeginProgress();
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var st = store.Settings;
        bool dry = kind is RunKind.Preview or RunKind.Analyze;   // предрасчёт и анализ ничего не записывают
        ClearErrors();

        // Режим для слабого ПК: пониженный приоритет, чтобы компьютер не «ложился»
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var oldPriority = process.PriorityClass;
        if (st.Economy)
        {
            try { process.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
        }

        var logProgress = new Progress<string>(s => { log.AppendText(s + Environment.NewLine); NoteStage(s); });
        int done = 0, skipped = 0, failed = 0;
        var uploadedLinks = new List<string>();

        try
        {
            for (int idx = 0; idx < nets.Count; idx++)
            {
                token.ThrowIfCancellationRequested();
                var net = nets[idx];
                int netIndex = idx;
                statusBase = $"{net.Name} ({idx + 1} из {nets.Count})";
                stageText = "";
                lblStatus.Text = statusBase;
                log.AppendText($"===== {net.Name} =====" + Environment.NewLine);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var runStats = new JsonRunStats();
                DateTime startedUtc = DateTime.UtcNow;
                string runStatus;
                try
                {
                    string? result = await RunNetworkAsync(net, netIndex, nets.Count, logProgress, token, kind, runStats);
                    if (result == null)
                    {
                        skipped++;
                        runStatus = net.LastStatus;
                        if (runStatus.StartsWith("Пропущена: фид старый"))
                            AddError(net.Name, "Сеть пропущена: фид не обновлялся.", "Запустите ещё раз после обновления фида или выберите в настройках сети «Всегда конвертировать».");
                        else
                            AddError(net.Name, runStatus.Length > 0 ? runStatus : "Сеть пропущена.", "Проверьте источники сети: есть ли в них файлы json/xml.");
                    }
                    else { done++; runStatus = "OK"; if (!dry) net.LastStatus = "OK"; }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)   // остановка пользователем (а не тайм-аут источника)
                {
                    RecordRun(net, kind, startedUtc, "Остановлено", sw, runStats);
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    var (what, todo) = Explain(ex);
                    AddError(net.Name, what, todo);
                    net.LastStatus = "Ошибка: " + ex.Message.Split('\n')[0];
                    runStatus = net.LastStatus;
                    log.AppendText("ОШИБКА: " + ex.Message + Environment.NewLine);
                }
                RecordRun(net, kind, startedUtc, runStatus, sw, runStats);
                if (runStats.Link != null) uploadedLinks.Add($"{net.Name}: {runStats.Link}");

                if (!dry) net.LastRunUtc = DateTime.UtcNow;
                store.SaveNetwork(net);
                UpdateRow(net);
                // «Удалять скачанные исходные файлы»: только после успешной сборки (не после предрасчёта, анализа и ошибки)
                if (!st.KeepSources && !dry && runStatus == "OK") cache.Clear(net.Slug);
            }

            FinishProgress(true);
            lblStatus.Text = $"Готово: обработано {done}, пропущено {skipped}, с ошибкой {failed}.";
            if (!dry)
            {
                string where;
                if (uploadedLinks.Count > 0)
                {
                    where = "JSON загружен на S3:\n" + string.Join("\n", uploadedLinks.Take(10)) +
                            (uploadedLinks.Count > 10 ? $"\n… и ещё {uploadedLinks.Count - 10}" : "") +
                            (st.KeepLocalJson ? $"\n\nЛокальная копия: {st.OutputDir}" : "");
                }
                else where = $"Папка результатов: {st.OutputDir}\n(Excel — в папке сети, JSON — в её подпапке json)";
                if (errorCount > 0) where += $"\n\nПроблемы ({errorCount}): смотрите вкладку «Ошибки».";
                MessageBox.Show(this, lblStatus.Text + "\n\n" + where, Text);
                if (errorCount > 0) tabs.SelectedTab = tabErrors;
            }
            else if (errorCount > 0) tabs.SelectedTab = tabErrors;
        }
        catch (OperationCanceledException)
        {
            FinishProgress(false);
            lblStatus.Text = "Остановлено.";
            log.AppendText("■ Остановлено пользователем." + Environment.NewLine);
        }
        finally
        {
            if (st.Economy)
            {
                try { process.PriorityClass = oldPriority; } catch { }
            }
            StopProgressTimer();
            cts?.Dispose();
            cts = null;
            SetBusy(false);
        }
    }

    /// <summary>Обработка одной сети. null — сеть пропущена (нет источников или фид старый, и пользователь отказался).</summary>
    private async Task<string?> RunNetworkAsync(NetworkConfig net, int netIndex, int netCount,
                                                IProgress<string> logProgress, CancellationToken token, RunKind kind, JsonRunStats stats)
    {
        var st = store.Settings;
        var b = net.Build;

        if (net.Sources.Count == 0)
        {
            net.LastStatus = "Нет источников";
            logProgress.Report("У сети нет источников — пропускаю.");
            return null;
        }

        // 1. Свежесть: дешёвая проверка без скачивания
        var ages = await ProbeNetworkAsync(net, token);
        freshness[net.Id] = DescribeFreshness(ages);
        UpdateRow(net);

        if (ages.Count == 0)
        {
            net.LastStatus = "Нет файлов в источниках";
            logProgress.Report("В источниках не найдено файлов (json/xml/yml) — пропускаю.");
            return null;
        }
        if (b.Combine && kind == RunKind.Excel && ages.Count < 2)
        {
            net.LastStatus = "Для объединения нужно 2+ файла";
            logProgress.Report("Для объединения нужно минимум два файла — пропускаю.");
            return null;
        }

        var stale = ages.Where(a => Freshness.IsStale(a.Age, st)).ToList();
        if (stale.Count > 0)
        {
            string detail = string.Join("\n", stale.Take(6).Select(a =>
                $"• {(a.Source.Label ?? SourceTitle(a.Source.Source))}: {Freshness.Describe(a.Age)}")) +
                (stale.Count > 6 ? $"\n… и ещё {stale.Count - 6}" : "");
            logProgress.Report($"Фид старый ({stale.Count} из {ages.Count} источников).");

            bool convert = net.StaleAction switch
            {
                StaleAction.AlwaysConvert => true,
                StaleAction.AlwaysSkip => false,
                _ => MessageBox.Show(this,
                         $"Сеть «{net.Name}»: фид старый (не обновлялся сегодня).\n\n{detail}\n\nВсё равно конвертировать?",
                         "Старый фид", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes
            };
            if (!convert)
            {
                net.LastStatus = "Пропущена: фид старый";
                logProgress.Report("Пропускаю: фид старый.");
                return null;
            }
        }
        foreach (var a in ages.Where(a => !a.Probe.Ok))
            logProgress.Report($"Предупреждение: не удалось проверить {a.Source.Source}: {a.Probe.Error}");

        // 2. Исходные файлы: из кэша или скачать, если изменились
        var local = new List<(NetworkSource Src, string Path)>();
        foreach (var a in ages)
        {
            token.ThrowIfCancellationRequested();
            logProgress.Report(a.Source.Label ?? SourceTitle(a.Source.Source));
            var c = await cache.GetAsync(net.Slug, a.Source.Source, false, logProgress, token, a.Probe.Ok ? a.Probe : null);
            local.Add((a.Source, c.Path));
        }

        // 3. Настройки сети -> параметры конвертации
        var mode = (PriceMode)Math.Clamp(b.PriceMode, 0, 3);
        // Excel: своя разбивка и свой лимит (окно «Настройки Excel»); если не заданы — как в настройках JSON
        var regionMode = (RegionMode)Math.Clamp(kind == RunKind.Excel ? (b.ExcelGeoMode ?? b.GeoMode) : b.GeoMode, 0, 4);
        int maxProducts = kind == RunKind.Excel ? (b.ExcelLimit ?? b.Limit) : b.Limit;
        if (kind != RunKind.Excel && regionMode is RegionMode.PerRegion or RegionMode.PerShop)
        {
            // JSON всегда один файл (регионы и адреса остаются каталогами внутри него)
            regionMode = regionMode == RegionMode.PerRegion ? RegionMode.RegionsMerged : RegionMode.Ignore;
        }
        var recodeOptions = BuildRecode(b);

        // Выгрузка на исходящий S3 (JSON и картинки); для Excel и предрасчёта загрузки JSON нет
        JsonS3Options? s3opt = null;
        if (kind is not (RunKind.Excel or RunKind.Analyze) && (net.UploadToS3 || net.ImagesToS3))
        {
            var outConn = st.S3Output;
            if (outConn == null || !outConn.IsConfigured)
                throw new InvalidOperationException("Не настроено исходящее подключение S3: Настройки → вкладка «S3: исходящий».");
            string root = outConn.RootFolder.Trim().Trim('/');
            string folder = root.Length > 0 ? root + "/" + net.Slug : net.Slug;
            s3opt = new JsonS3Options(outConn, folder, net.ImagesToS3, kind == RunKind.Json && net.UploadToS3, st.Threads, st.KeepLocalJson, net.ExcludeSmallImages);
        }

        // Excel лежит в папке сети, JSON — в её подпапке json
        string baseDir = kind == RunKind.Excel
            ? Path.Combine(st.OutputDir, net.Slug)
            : Path.Combine(st.OutputDir, net.Slug, "json");
        decimal spread = b.SpreadOff ? 100000m : b.Spread;
        var ymlOpt = new JsonYmlOptions(net.YmlConditions, net.YmlDays, Freshness.Today(st));
        var limitMode = (LimitMode)Math.Clamp(net.LimitMode, 0, 1);

        // YML-источники с заданным target — отдельные каталоги с этим target: собираются отдельными частями,
        // затем все части склеиваются в один JSON
        var ymlTargets = kind == RunKind.Excel
            ? new List<(NetworkSource Src, string Path)>()
            : local.Where(x => !string.IsNullOrWhiteSpace(x.Src.Region) && Converter.LooksLikeXml(x.Path)).ToList();
        bool mergeParts = ymlTargets.Count > 0;
        var rest = mergeParts ? local.Where(x => !ymlTargets.Contains(x)).ToList() : local;
        string workDir = mergeParts ? Path.Combine(baseDir, "_parts") : baseDir;
        var s3Items = mergeParts && s3opt != null ? s3opt with { UploadJson = false } : s3opt;
        var items = new List<ConvertOptions>();

        // Для JSON объединение работает, когда в сети два и больше файлов; для Excel — как раньше
        if (rest.Count > 0 && b.Combine && (kind == RunKind.Excel || rest.Count >= 2))
        {
            var feeds = rest
                .Select(x => new FeedSource(x.Path, x.Src.Label ?? SourceTitle(x.Src.Source), x.Src.Region))
                .ToList();
            items.Add(new ConvertOptions(
                rest[0].Path, workDir, spread, mode, b.PartMb, b.Zip, maxProducts, b.Merge, b.ShopFilter, b.ShopPercent,
                regionMode, recodeOptions, st.Threads, st.Economy, b.PromoOnly, feeds, b.RegionBySubject,
                kind == RunKind.Excel
                    ? null
                    : new JsonBuildOptions(b.Limit, limitMode, net.Slug, s3Items, stats, ymlOpt, net.AllowRepeats, net.CatalogDays, net.Preset == 1, net.PreferPromo)));
        }
        else
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in rest)
            {
                string dir = workDir;
                string fileName = net.Slug;
                if (local.Count > 1)
                {
                    string name = x.Src.Label != null ? SafeName(x.Src.Label) : SourceTitle(x.Src.Source);
                    string unique = name;
                    int k = 2;
                    while (!used.Add(unique)) unique = $"{name} ({k++})";
                    dir = Path.Combine(workDir, unique);
                    fileName = unique;
                }

                var json = kind == RunKind.Excel
                    ? null
                    : new JsonBuildOptions(b.Limit, limitMode, fileName, s3Items, stats, ymlOpt, net.AllowRepeats, net.CatalogDays, net.Preset == 1, net.PreferPromo);
                items.Add(new ConvertOptions(
                    x.Path, dir, spread, mode, b.PartMb, b.Zip, maxProducts, b.Merge, b.ShopFilter, b.ShopPercent,
                    regionMode, recodeOptions, st.Threads, st.Economy, b.PromoOnly, null, b.RegionBySubject, json));
            }
        }

        // Каждый YML с target — своя часть: один каталог с этим target, товары по названию, цена по режиму «Цена»
        int partNo = 0;
        foreach (var t in ymlTargets)
        {
            partNo++;
            string target = t.Src.Region!.Trim();
            var ymlTarget = new JsonYmlOptions(net.YmlConditions, net.YmlDays, Freshness.Today(st), target);
            items.Add(new ConvertOptions(
                t.Path, workDir, spread, mode, b.PartMb, b.Zip, maxProducts, b.Merge, b.ShopFilter, b.ShopPercent,
                regionMode, recodeOptions, st.Threads, st.Economy, b.PromoOnly, null, b.RegionBySubject,
                new JsonBuildOptions(b.Limit, limitMode, $"yml_target_{partNo}", s3Items, stats, ymlTarget,
                                     net.AllowRepeats, net.CatalogDays, true, net.PreferPromo)));
        }
        if (mergeParts)
            logProgress.Report($"YML с заданным target: {ymlTargets.Count} — каждый станет отдельным каталогом, части склеиваются в один JSON.");

        var percent = new Progress<double>(p => overallPercent = Math.Clamp((netIndex * 100.0 + p) / netCount, 0, 100));

        // 4. Анализ сети: читаем всё без фильтров и выдаём отчёт с рекомендациями по настройкам
        if (kind == RunKind.Analyze)
        {
            foreach (var item in items)
            {
                if (items.Count > 1) logProgress.Report($"--- {item.Json!.FileName} ---");
                var lines = await Task.Run(() => Converter.AnalyzeNetworkAsync(item, logProgress, percent, token));
                foreach (var line in lines) logProgress.Report(line);
            }
            net.LastStatus = "Анализ выполнен";
            return "analyze";
        }

        // 4a. Предрасчёт: ничего не записываем и не скачиваем
        if (kind == RunKind.Preview)
        {
            foreach (var item in items)
            {
                if (items.Count > 1) logProgress.Report($"--- {item.Json!.FileName} ---");
                var pv = await Task.Run(() => Converter.PreviewJsonAsync(item, logProgress, percent, token));
                ReportPreview(pv, b, logProgress);
                stats.Files += pv.Rows.Count(r => r.Offers > 0);
                stats.Offers += pv.Rows.Sum(r => r.Offers);
                stats.Catalogs += pv.Rows.Sum(r => r.Catalogs);
            }
            if (mergeParts)
                logProgress.Report($"Итоговый JSON склеивается из {items.Count} частей: всего офферов {stats.Offers}, каталогов {stats.Catalogs}.");
            net.LastStatus = "Предрасчёт выполнен";
            return "preview";
        }

        // 4b. Сборка JSON или выгрузка в Excel
        string result = await Task.Run(() => Converter.RunBatchAsync(items, logProgress, percent, token));
        logProgress.Report(result);

        if (mergeParts)
        {
            var partFiles = Directory.Exists(workDir)
                ? Directory.GetFiles(workDir, "*.json", SearchOption.AllDirectories)
                    .OrderBy(f => Path.GetFileName(f).StartsWith("yml_target_", StringComparison.Ordinal) ? 1 : 0)
                    .ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            if (partFiles.Count == 0)
                throw new InvalidOperationException("После отбора и лимита не осталось офферов ни в одной части.");
            if (partFiles.Count < items.Count)
                AddError(net.Name, $"Собрано частей {partFiles.Count} из {items.Count}: остальные пустые или с ошибкой.",
                    "Откройте «Журнал»: там причина для каждой части. Для YML с target проверьте разброс цен и лимит.");

            string merged = Path.Combine(baseDir, net.Slug + ".json");
            JsonMerge.Merge(partFiles, merged, out int mc, out int mo);
            try { Directory.Delete(workDir, true); } catch { }
            long size = new FileInfo(merged).Length;
            logProgress.Report($"Части объединены в один файл: {merged} — каталогов {mc}, офферов {mo}, {size / 1024.0 / 1024.0:F1} МБ");
            stats.Files = 1;
            stats.Offers = mo;
            stats.Catalogs = mc;
            stats.Bytes = size;

            if (s3opt != null && s3opt.UploadJson)
            {
                string key = s3opt.Folder + "/" + net.Slug + ".json";
                logProgress.Report("Загружаю JSON на S3...");
                await s3opt.Conn.CreateClient().PutFileAsync(s3opt.Conn.Bucket, key, merged, "application/json", token);
                string host = !string.IsNullOrWhiteSpace(s3opt.Conn.PublicBaseUrl)
                    ? s3opt.Conn.PublicBaseUrl.Trim().TrimEnd('/')
                    : $"https://{s3opt.Conn.Endpoint.Trim().TrimEnd('/')}/{s3opt.Conn.Bucket}";
                string link = host + "/" + SigV4.EncodePath(key);
                logProgress.Report($"Загружено: {link}");
                stats.Uploaded = true;
                stats.Link = link;
                if (!s3opt.KeepLocal)
                {
                    try { File.Delete(merged); logProgress.Report("Локальная копия удалена (оставлять её можно в Настройках)."); } catch { }
                }
            }
        }
        return result;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} МБ" : $"{bytes / 1024.0:F0} КБ";

    private static void ReportPreview(JsonPreview pv, UiSettings b, IProgress<string> log)
    {
        log.Report($"В исходном файле: офферов {pv.SourceOffers}, уникальных товаров {pv.SourceProducts}, {FormatBytes(pv.SourceBytes)}");
        if (b.PromoOnly && pv.SourceOffers == 0)
            log.Report("⚠ Включён режим «только акционные», но офферов с price_old в файле нет.");

        log.Report("Результат (приблизительно):");
        log.Report($"  {"Файл",-30} {"Товаров",8} {"Офферов",8} {"Каталогов",9} {"На каталог",11} {"Размер",10}");
        foreach (var r in pv.Rows)
        {
            if (r.Problem != null && r.Offers == 0)
            {
                log.Report($"  {r.Title,-30} — {r.Problem}");
                continue;
            }
            log.Report($"  {r.Title,-30} {r.Products,8} {r.Offers,8} {r.Catalogs,9} {r.AvgPerCatalog,11} {FormatBytes(r.EstimatedBytes),10}");
            if (r.Problem != null) log.Report("    ⚠ " + r.Problem);
        }
        log.Report("Вес JSON — оценка по доле офферов; точный размер будет после сборки.");
    }
}
