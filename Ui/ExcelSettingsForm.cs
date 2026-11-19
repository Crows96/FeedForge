namespace FeedForge;

/// <summary>
/// Настройки, которые есть только у выгрузки в Excel (кнопка «JSON → Excel»): разбивка по регионам и магазинам,
/// лимит товаров в файле, размер частей и ZIP. Остальные настройки (цена, разброс, охват и др.) берутся из основного окна.
/// Кнопки: «Запустить» (DialogResult.OK), «Сохранить» (DialogResult.Yes), «Отмена».
/// </summary>
public sealed class ExcelSettingsForm : Form
{
    private readonly ComboBox cmbGeo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 470 };
    private readonly NumericUpDown numLimit = new() { Minimum = 0, Maximum = 1000000, Width = 90 };
    private readonly NumericUpDown numPartMb = new() { Minimum = 1, Maximum = 100000, Width = 80 };
    private readonly CheckBox chkZip = new() { Text = "Картинки в ZIP", AutoSize = true };

    // порядок пунктов совпадает с enum RegionMode
    private static readonly string[] GeoItems =
    {
        "Не разбивать — один результат на все магазины",
        "Только товары, которые есть во всех регионах или магазинах — один результат",
        "По регионам — отдельный результат (папка) на каждый регион / город",
        "По магазинам — отдельный результат (папка) на каждый магазин",
        "По регионам, но один общий результат — фильтр считается в каждом регионе отдельно"
    };

    public int GeoMode => Math.Max(0, cmbGeo.SelectedIndex);
    public int Limit => (int)numLimit.Value;
    public int PartMb => (int)numPartMb.Value;
    public bool Zip => chkZip.Checked;

    public ExcelSettingsForm(string title, UiSettings s, string sharedSummary, int networksCount)
    {
        Text = title;
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        cmbGeo.Items.AddRange(GeoItems);
        cmbGeo.SelectedIndex = Math.Clamp(s.ExcelGeoMode ?? s.GeoMode, 0, GeoItems.Length - 1);
        numLimit.Value = Math.Clamp(s.ExcelLimit ?? s.Limit, 0, (int)numLimit.Maximum);
        numPartMb.Value = Math.Clamp(s.PartMb, 1, (int)numPartMb.Maximum);
        chkZip.Checked = s.Zip;

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(12)
        };

        panel.Controls.Add(Hdr("Как делить результат"));
        panel.Controls.Add(cmbGeo);

        panel.Controls.Add(Hdr("Лимит"));
        panel.Controls.Add(Row(Lbl("Не больше товаров в каждом Excel:"), numLimit, Lbl("(0 — без лимита)")));

        panel.Controls.Add(Hdr("Картинки"));
        panel.Controls.Add(Row(Lbl("Размер части, МБ:"), numPartMb, chkZip));

        panel.Controls.Add(Hdr("Из основного окна для Excel также применяются"));
        panel.Controls.Add(new Label
        {
            Text = sharedSummary, AutoSize = true, MaximumSize = new Size(540, 0),
            ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 8)
        });
        if (networksCount > 1)
            panel.Controls.Add(new Label
            {
                Text = $"Значения выше применятся ко всем отмеченным сетям ({networksCount}); остальное у каждой сети своё.",
                AutoSize = true, MaximumSize = new Size(540, 0), Margin = new Padding(3, 0, 3, 8)
            });

        var btnRun = new Button { Text = "Запустить", DialogResult = DialogResult.OK, Width = 110 };
        var btnSave = new Button { Text = "Сохранить", DialogResult = DialogResult.Yes, Width = 110 };
        var btnCancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 110 };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange(new Control[] { btnRun, btnSave, btnCancel });
        panel.Controls.Add(buttons);

        Controls.Add(panel);
        AcceptButton = btnRun;
        CancelButton = btnCancel;
    }

    private static Label Hdr(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(3, 10, 3, 4)
    };

    private static Label Lbl(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 0)
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        p.Controls.AddRange(controls);
        return p;
    }
}
