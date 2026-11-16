using System.Text.Json;

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

    /// <summary>Json — собрать сокращённый JSON; Preview — только посчитать, что получится; Excel — выгрузка в Excel с картинками (локально).</summary>
    private enum RunKind { Json, Excel, Preview }
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
    private readonly CheckBox chkSpreadOff = new() { Text = "Не проверять разброс цен (у каждого каталога своя цена)", AutoSize = true };
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
    private readonly CheckBox chkMerge = new() { Text = "Объединять одинаковые товары с разными кодами", AutoSize = true };
    private readonly CheckBox chkCombine = new() { Text = "Объединить файлы сети в один результат (каждый файл — отдельный магазин)", AutoSize = true };
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
    private readonly NumericUpDown numPartMb = new() { Minimum = 1, Maximum = 100000, Width = 80 };
    private readonly CheckBox chkZip = new() { Text = "Картинки в ZIP", AutoSize = true };
    private readonly Button btnRecode = new() { Text = "Сжатие картинок...", AutoSize = true };
    private readonly Label lblRecode = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 6, 3, 0) };
    private readonly ComboBox cmbStale = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly Label lblSlug = new() { AutoSize = true, ForeColor = SystemColors.GrayText };

    // ---- низ
    private readonly ProgressBar progress = new() { Dock = DockStyle.Top, Height = 20 };
    private readonly Label lblStatus = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

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
            "Одна цена на товар: проверка разброса цен",
            "Свои настройки (расширенные)"
        });
        cmbPrice.Items.AddRange(new object[] { "Минимальная", "Максимальная", "Средняя", "Медиана" });
        // порядок пунктов совпадает с enum RegionMode
        cmbGeo.Items.AddRange(new object[]
        {
            "Не разбивать — один результат на все магазины",
            "Только товары, которые есть во всех регионах — один результат",
            "По регионам — отдельный результат (папка) на каждый регион / город",
            "По магазинам — отдельный результат (папка) на каждый магазин",
            "По регионам, но один общий результат — фильтр считается в каждом регионе отдельно"
        });
        cmbLimitMode.Items.AddRange(new object[]
        {
            "На весь результат", "На каждый каталог (магазин / регион)", "На каждый результирующий файл"
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
        settings.Controls.Add(chkCombine);

        settings.Controls.Add(Hdr("Лимит офферов"));
        settings.Controls.Add(Row(Lbl("Лимит:"), numLimit, Lbl("(0 — без лимита)")));
        settings.Controls.Add(Row(Lbl("Применять:"), cmbLimitMode));

        settings.Controls.Add(Hdr("Выгрузка на S3"));
        settings.Controls.Add(chkUpload);
        settings.Controls.Add(chkImagesS3);
        settings.Controls.Add(chkExcludeSmall);
        settings.Controls.Add(Row(btnRecode, lblRecode));


        // Расширенные настройки: видны только в режиме «Свои настройки»
        advPanel.Controls.Add(Hdr("Отбор товаров"));
        advPanel.Controls.Add(Row(Lbl("Макс. разброс цен, %:"), numSpread, Lbl("  Цена:"), cmbPrice));
        advPanel.Controls.Add(chkSpreadOff);
        advPanel.Controls.Add(Row(chkShop, numShop));
        advPanel.Controls.Add(Row(Lbl("Гео:"), cmbGeo));
        advPanel.Controls.Add(chkSubject);
        advPanel.Controls.Add(chkMerge);

        advPanel.Controls.Add(Hdr("Каталог из YML-офферов (target — Россия)"));
        advPanel.Controls.Add(Row(Lbl("Срок действия, дней:"), numYmlDays));
        advPanel.Controls.Add(Row(Lbl("Условия:"), txtYmlConditions));

        advPanel.Controls.Add(Hdr("Только для Excel"));
        advPanel.Controls.Add(Row(Lbl("Размер части, МБ:"), numPartMb, chkZip));

        advPanel.Controls.Add(Hdr("Запуск"));
        advPanel.Controls.Add(Row(Lbl("Если фид старый:"), cmbStale));
        settings.Controls.Add(advPanel);
        tabSettings.Controls.Add(settings);
        tabSettings.Tag = settings;

        tabSources.Controls.Add(lstSources);
        tabLog.Controls.Add(log);
        tabHistory.Controls.Add(lstHistory);
        foreach (var (title, width) in new[] { ("Дата", 110), ("Режим", 80), ("Статус", 200), ("Файлов", 55), ("Офферов", 65), ("Картинки (новых / уже было / ошибок)", 210), ("Время", 60) })
            lstHistory.Columns.Add(title, width);
        tabs.TabPages.Add(tabSettings);
        tabs.TabPages.Add(tabSources);
        tabs.TabPages.Add(tabHistory);
        tabs.TabPages.Add(tabLog);

        split.Dock = DockStyle.Fill;
        split.Orientation = Orientation.Vertical;
        split.Panel1.Controls.Add(list);
        split.Panel2.Controls.Add(tabs);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(6) };
        foreach (var b in new[] { btnStart, btnStop, btnPreview, btnAdd, btnS3Nets, btnEdit, btnDelete, btnFresh, btnSettings, btnOld })
        {
            b.Margin = new Padding(3, 3, 9, 3);
            top.Controls.Add(b);
        }

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(6, 4, 6, 4) };
        bottom.Controls.Add(lblStatus);
        bottom.Controls.Add(progress);

        // порядок: сначала заполняющий элемент, потом нижняя и верхняя панели
        Controls.Add(split);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    private void WireEvents()
    {
        btnAdd.Click += (_, _) => AddNetwork();
        btnS3Nets.Click += (_, _) => AddNetworksFromS3();
        btnEdit.Click += (_, _) => EditNetwork();
        btnDelete.Click += (_, _) => DeleteNetwork();
        btnSettings.Click += (_, _) => OpenSettings();
        btnOld.Click += async (_, _) => await StartAsync(RunKind.Excel);
        btnPreview.Click += async (_, _) => await StartAsync(RunKind.Preview);
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

        foreach (var c in new Control[] { numSpread, numShop, numLimit, numPartMb, numYmlDays })
            ((NumericUpDown)c).ValueChanged += (_, _) => SettingChanged();
        foreach (var c in new[] { cmbPrice, cmbGeo, cmbLimitMode, cmbStale })
            c.SelectedIndexChanged += (_, _) => SettingChanged();
        txtYmlConditions.TextChanged += (_, _) => SettingChanged();
        cmbPreset.SelectedIndexChanged += (_, _) => PresetChanged();
        foreach (var c in new[] { chkSpreadOff, chkShop, chkSubject, chkPromo, chkMerge, chkCombine, chkZip, chkUpload, chkImagesS3, chkExcludeSmall })
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
        applying = true;
        try
        {
            var s = current.Build;
            cmbPreset.SelectedIndex = Math.Clamp(current.Preset, 0, 2);
            lblPresetHint.Text = PresetHint(cmbPreset.SelectedIndex);
            advPanel.Visible = cmbPreset.SelectedIndex == 2;   // расширенные настройки — только в режиме «Свои настройки»
            numSpread.Value = Math.Clamp(s.Spread, numSpread.Minimum, numSpread.Maximum);
            chkSpreadOff.Checked = s.SpreadOff;
            numSpread.Enabled = !s.SpreadOff;
            numYmlDays.Value = Math.Clamp(current.YmlDays, 1, 60);
            txtYmlConditions.Text = current.YmlConditions;
            cmbPrice.SelectedIndex = Math.Clamp(s.PriceMode, 0, cmbPrice.Items.Count - 1);
            chkShop.Checked = s.ShopFilter;
            numShop.Value = Math.Clamp(s.ShopPercent, numShop.Minimum, numShop.Maximum);
            cmbGeo.SelectedIndex = Math.Clamp(s.GeoMode, 0, cmbGeo.Items.Count - 1);
            chkSubject.Checked = s.RegionBySubject;
            chkPromo.Checked = s.PromoOnly;
            chkMerge.Checked = s.Merge;
            chkCombine.Checked = s.Combine;
            numLimit.Value = Math.Clamp(s.Limit, 0, (int)numLimit.Maximum);
            cmbLimitMode.SelectedIndex = Math.Clamp(current.LimitMode, 0, cmbLimitMode.Items.Count - 1);
            numPartMb.Value = Math.Clamp(s.PartMb, 1, (int)numPartMb.Maximum);
            chkZip.Checked = s.Zip;
            chkUpload.Checked = current.UploadToS3;
            chkImagesS3.Checked = current.ImagesToS3;
            chkExcludeSmall.Checked = current.ExcludeSmallImages;
            recode = new RecodeOptions(s.RecodeEnabled, (RecodeFormat)Math.Clamp(s.RecodeFormat, 0, 2),
                                       Math.Clamp(s.RecodeMaxSide, 0, 10000), Math.Clamp(s.RecodeQuality, 1, 100),
                                       Math.Clamp(s.RecodeMaxKb, 0, 100000) * 1000);
            lblRecode.Text = recode.Describe();
            cmbStale.SelectedIndex = Math.Clamp((int)current.StaleAction, 0, cmbStale.Items.Count - 1);
        }
        finally
        {
            applying = false;
        }

        numShop.Enabled = chkShop.Checked;
        lblSlug.Text = $"Папка: {current.Slug}    (результат: {Path.Combine(store.Settings.OutputDir, current.Slug)})";
        foreach (var src in current.Sources)
            lstSources.Items.Add(src.Source + (src.Label != null ? "   [" + src.Label + "]" : ""));
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
            s.ShopPercent = 95;
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
        1 => "Для товара выбирается одна цена; товары с сильно различающимися ценами в разных магазинах и регионах отбрасываются.",
        _ => "Все параметры отбора настраиваются вручную: ниже появятся расширенные настройки."
    };

    private void SettingChanged()
    {
        if (applying || current == null) return;
        numShop.Enabled = chkShop.Checked;
        var s = current.Build;
        s.Spread = numSpread.Value;
        s.SpreadOff = chkSpreadOff.Checked;
        numSpread.Enabled = !s.SpreadOff;
        current.YmlDays = (int)numYmlDays.Value;
        current.YmlConditions = txtYmlConditions.Text.Trim();
        s.PriceMode = Math.Max(0, cmbPrice.SelectedIndex);
        s.ShopFilter = chkShop.Checked;
        s.ShopPercent = numShop.Value;
        s.GeoMode = Math.Max(0, cmbGeo.SelectedIndex);
        s.RegionBySubject = chkSubject.Checked;
        s.PromoOnly = chkPromo.Checked;
        s.Merge = chkMerge.Checked;
        s.Combine = chkCombine.Checked;
        s.Limit = (int)numLimit.Value;
        current.LimitMode = Math.Max(0, cmbLimitMode.SelectedIndex);
        s.PartMb = (int)numPartMb.Value;
        s.Zip = chkZip.Checked;
        current.UploadToS3 = chkUpload.Checked;
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
        n.Build = SettingsStore.Load(out _).Last;
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
        var defaults = SettingsStore.Load(out _).Last;
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
                Build = JsonSerializer.Deserialize<UiSettings>(JsonSerializer.Serialize(defaults)) ?? new UiSettings(),
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

    private List<NetworkConfig> CheckedNetworks() =>
        list.CheckedItems.Cast<ListViewItem>().Select(i => (NetworkConfig)i.Tag!).ToList();

    // ================================================================== запуск

    private void SetBusy(bool on)
    {
        busy = on;
        btnStart.Enabled = btnPreview.Enabled = btnOld.Enabled = btnS3Nets.Enabled = btnAdd.Enabled = btnEdit.Enabled =
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

    private void RecordRun(NetworkConfig net, RunKind kind, DateTime startedUtc, string status,
                           System.Diagnostics.Stopwatch sw, JsonRunStats s)
    {
        store.AddHistory(net, new RunRecord
        {
            StartedUtc = startedUtc,
            Kind = kind switch { RunKind.Json => "JSON", RunKind.Excel => "Excel", _ => "Предрасчёт" },
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
        progress.Value = 0;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        var st = store.Settings;

        // Режим для слабого ПК: пониженный приоритет, чтобы компьютер не «ложился»
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var oldPriority = process.PriorityClass;
        if (st.Economy)
        {
            try { process.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
        }

        var logProgress = new Progress<string>(s => log.AppendText(s + Environment.NewLine));
        int done = 0, skipped = 0, failed = 0;
        var uploadedLinks = new List<string>();

        try
        {
            for (int idx = 0; idx < nets.Count; idx++)
            {
                token.ThrowIfCancellationRequested();
                var net = nets[idx];
                int netIndex = idx;
                lblStatus.Text = $"{net.Name} ({idx + 1} из {nets.Count})";
                log.AppendText($"===== {net.Name} =====" + Environment.NewLine);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var runStats = new JsonRunStats();
                DateTime startedUtc = DateTime.UtcNow;
                string runStatus;
                try
                {
                    string? result = await RunNetworkAsync(net, netIndex, nets.Count, logProgress, token, kind, runStats);
                    if (result == null) { skipped++; runStatus = net.LastStatus; }
                    else { done++; runStatus = "OK"; if (kind != RunKind.Preview) net.LastStatus = "OK"; }
                }
                catch (OperationCanceledException)
                {
                    RecordRun(net, kind, startedUtc, "Остановлено", sw, runStats);
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    net.LastStatus = "Ошибка: " + ex.Message.Split('\n')[0];
                    runStatus = net.LastStatus;
                    log.AppendText("ОШИБКА: " + ex.Message + Environment.NewLine);
                }
                RecordRun(net, kind, startedUtc, runStatus, sw, runStats);
                if (runStats.Link != null) uploadedLinks.Add($"{net.Name}: {runStats.Link}");

                if (kind != RunKind.Preview) net.LastRunUtc = DateTime.UtcNow;
                store.SaveNetwork(net);
                UpdateRow(net);
                if (!st.KeepSources) cache.Clear(net.Slug);
            }

            progress.Value = 100;
            lblStatus.Text = $"Готово: обработано {done}, пропущено {skipped}, с ошибкой {failed}.";
            if (kind != RunKind.Preview)
            {
                string where;
                if (uploadedLinks.Count > 0)
                {
                    where = "JSON загружен на S3:\n" + string.Join("\n", uploadedLinks.Take(10)) +
                            (uploadedLinks.Count > 10 ? $"\n… и ещё {uploadedLinks.Count - 10}" : "") +
                            (st.KeepLocalJson ? $"\n\nЛокальная копия: {st.OutputDir}" : "");
                }
                else where = $"Папка результатов: {st.OutputDir}";
                MessageBox.Show(this, lblStatus.Text + "\n\n" + where, Text);
            }
        }
        catch (OperationCanceledException)
        {
            progress.Value = 0;
            lblStatus.Text = "Остановлено.";
            log.AppendText("■ Остановлено пользователем." + Environment.NewLine);
        }
        finally
        {
            if (st.Economy)
            {
                try { process.PriorityClass = oldPriority; } catch { }
            }
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
        var regionMode = (RegionMode)Math.Clamp(b.GeoMode, 0, 4);
        if (kind != RunKind.Excel && regionMode is RegionMode.PerRegion or RegionMode.PerShop)
        {
            // JSON всегда один файл (регионы и адреса остаются каталогами внутри него)
            regionMode = regionMode == RegionMode.PerRegion ? RegionMode.RegionsMerged : RegionMode.Ignore;
            logProgress.Report("Для JSON разбивка на отдельные файлы не применяется: регионы и магазины остаются каталогами в одном файле.");
        }
        var recodeOptions = new RecodeOptions(b.RecodeEnabled, (RecodeFormat)Math.Clamp(b.RecodeFormat, 0, 2),
                                              Math.Clamp(b.RecodeMaxSide, 0, 10000), Math.Clamp(b.RecodeQuality, 1, 100),
                                              Math.Clamp(b.RecodeMaxKb, 0, 100000) * 1000);

        // Выгрузка на исходящий S3 (JSON и картинки); для Excel и предрасчёта загрузки JSON нет
        JsonS3Options? s3opt = null;
        if (kind != RunKind.Excel && (net.UploadToS3 || net.ImagesToS3))
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
        var items = new List<ConvertOptions>();
        decimal spread = b.SpreadOff ? 100000m : b.Spread;
        var ymlOpt = new JsonYmlOptions(net.YmlConditions, net.YmlDays, Freshness.Today(st));

        // Для JSON объединение работает, когда в сети два и больше файлов; для Excel — как раньше
        if (b.Combine && (kind == RunKind.Excel || local.Count >= 2))
        {
            var feeds = local
                .Select(x => new FeedSource(x.Path, x.Src.Label ?? SourceTitle(x.Src.Source), x.Src.Region))
                .ToList();
            items.Add(new ConvertOptions(
                local[0].Path, baseDir, spread, mode, b.PartMb, b.Zip, b.Limit, b.Merge, b.ShopFilter, b.ShopPercent,
                regionMode, recodeOptions, st.Threads, st.Economy, b.PromoOnly, feeds, b.RegionBySubject,
                kind == RunKind.Excel
                    ? null
                    : new JsonBuildOptions(b.Limit, (LimitMode)Math.Clamp(net.LimitMode, 0, 2), net.Slug, s3opt, stats, ymlOpt)));
        }
        else
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in local)
            {
                string dir = baseDir;
                string fileName = net.Slug;
                if (local.Count > 1)
                {
                    string name = x.Src.Label != null ? SafeName(x.Src.Label) : SourceTitle(x.Src.Source);
                    string unique = name;
                    int k = 2;
                    while (!used.Add(unique)) unique = $"{name} ({k++})";
                    dir = Path.Combine(baseDir, unique);
                    fileName = unique;
                }

                var json = kind == RunKind.Excel
                    ? null
                    : new JsonBuildOptions(b.Limit, (LimitMode)Math.Clamp(net.LimitMode, 0, 2), fileName, s3opt, stats, ymlOpt);
                items.Add(new ConvertOptions(
                    x.Path, dir, spread, mode, b.PartMb, b.Zip, b.Limit, b.Merge, b.ShopFilter, b.ShopPercent,
                    regionMode, recodeOptions, st.Threads, st.Economy, b.PromoOnly, null, b.RegionBySubject, json));
            }
        }

        var percent = new Progress<int>(p => progress.Value = Math.Clamp((netIndex * 100 + p) / netCount, 0, 100));

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
            net.LastStatus = "Предрасчёт выполнен";
            return "preview";
        }

        // 4b. Сборка JSON или выгрузка в Excel
        string result = await Task.Run(() => Converter.RunBatchAsync(items, logProgress, percent, token));
        logProgress.Report(result);
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
        log.Report($"  {"Файл",-30} {"Товаров",8} {"Офферов",8} {"Каталогов",9} {"Размер",10}");
        foreach (var r in pv.Rows)
        {
            if (r.Problem != null && r.Offers == 0)
            {
                log.Report($"  {r.Title,-30} — {r.Problem}");
                continue;
            }
            log.Report($"  {r.Title,-30} {r.Products,8} {r.Offers,8} {r.Catalogs,9} {FormatBytes(r.EstimatedBytes),10}");
            if (r.Problem != null) log.Report("    ⚠ " + r.Problem);
        }
        log.Report("Вес JSON — оценка по доле офферов; точный размер будет после сборки.");
    }
}
