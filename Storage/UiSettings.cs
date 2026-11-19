namespace FeedForge;

/// <summary>Настройки сборки одной сети (хранятся в файле сети).</summary>
public sealed class UiSettings
{
    public decimal Spread { get; set; }
    /// <summary>Не проверять разброс цен (для JSON с региональными каталогами у каждого региона своя цена).</summary>
    public bool SpreadOff { get; set; }
    public int PriceMode { get; set; } = 1;
    public int PartMb { get; set; } = 100;
    public bool Zip { get; set; } = true;
    public int Limit { get; set; }
    public bool Merge { get; set; } = true;
    public bool PromoOnly { get; set; }
    public bool ShopFilter { get; set; } = true;
    public decimal ShopPercent { get; set; } = 95;
    public int GeoMode { get; set; }
    /// <summary>Только для Excel: как делить результат (RegionMode, 0–4). null — как в настройке «Гео» для JSON.</summary>
    public int? ExcelGeoMode { get; set; }
    /// <summary>Только для Excel: максимум товаров в каждом файле. null — берётся «Лимит офферов» (старое поведение).</summary>
    public int? ExcelLimit { get; set; }
    public bool RegionBySubject { get; set; }
    public bool Combine { get; set; }
    public bool RecodeEnabled { get; set; } = true;
    public int RecodeFormat { get; set; }
    public int RecodeMaxSide { get; set; } = 1000;
    public int RecodeQuality { get; set; } = 85;
    /// <summary>Максимальный вес картинки, КБ (0 — без ограничения). Для Едадил — до 1 МБ.</summary>
    public int RecodeMaxKb { get; set; } = 1000;
}
