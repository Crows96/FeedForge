using System.Text.Json;
using System.Xml;

namespace FeedForge;

public enum PriceMode { Min, Max, Average, Median }

/// <summary>Формат исходного файла (определяется по содержимому).</summary>
public enum SourceFormat { Json, Xml }

/// <summary>Как делить выгрузку по гео (target_regions / region / target_shops).</summary>
public enum RegionMode
{
    Ignore,       // регионы не учитываем
    AllRegions,   // один Excel: только товары, которые есть во всех регионах
    PerRegion,    // отдельный Excel (папка) на каждый регион (название папки = значение из target_regions)
    PerShop,      // отдельный Excel (папка) на каждый магазин/адрес (название папки = значение из target_shops)
    RegionsMerged // один Excel: охват и цены считаются в каждом регионе отдельно; товар берём, если прошёл фильтр в каждом регионе, где он есть
}

/// <summary>Один файл (или ссылка) в режиме объединения. YML — отдельный магазин; в JSON магазины берутся из каталогов (target_shops).</summary>
public sealed record FeedSource(string Source, string Label, string? Region = null);

public sealed record ConvertOptions(
    string JsonSource,          // путь к файлу или ссылка
    string OutDir,
    decimal MaxSpreadPercent,   // допустимый разброс цен, %
    PriceMode PriceMode,
    int MaxPartMb,              // макс. размер картинок в одной части
    bool MakeZip,
    int MaxProducts,            // 0 — без ограничения (в каждом Excel)
    bool MergeSame,             // объединять товары с одинаковым названием и картинкой
    bool ShopFilter,            // фильтр по магазинам (target_shops)
    decimal ShopPercent,        // минимум % магазинов
    RegionMode RegionMode,
    RecodeOptions Recode,       // сжатие картинок
    int DownloadThreads,        // сколько картинок скачивать одновременно
    bool Economy,               // режим для слабого ПК: меньше потоков и нагрузки
    bool PromoOnly,             // только акционные позиции: в оффере есть price_old (старая цена)
    IReadOnlyList<FeedSource>? Feeds = null,   // не null — режим объединения: все файлы списка (YML и/или JSON) в один результат
    bool RegionBySubject = false,              // регион = субъект РФ (область, край, республика) по справочнику, а не город
    JsonBuildOptions? Json = null);            // не null — результат не Excel, а сокращённый JSON в том же формате

/// <summary>Настройки сборки JSON. LimitOffers — максимум офферов в файле (0 — без лимита), режим лимита — LimitMode.</summary>
public sealed record JsonBuildOptions(int LimitOffers, LimitMode LimitMode, string FileName, JsonS3Options? S3 = null,
                                      JsonRunStats? Stats = null, JsonYmlOptions? Yml = null, bool AllowRepeats = true,
                                      int CatalogDays = 0, bool Common = false, bool PreferPromo = false);

/// <summary>Параметры каталога, который строится из YML-офферов (в YML нет каталогов): условия, срок действия, дата начала.</summary>
public sealed record JsonYmlOptions(string Conditions, int Days, DateOnly Today, string Target = "Россия")
{
    public const string DefaultConditions =
        "Предложения действительны в магазинах торговой сети на территории России. " +
        "Информацию об ассортименте товаров, участвующих в акции, уточняйте в магазине. " +
        "Количество товаров ограничено, не является публичной офертой.";
}

/// <summary>Итоги запуска сборки JSON (накапливаются по всем файлам сети): для истории запусков.</summary>
public sealed class JsonRunStats
{
    public int Files, Offers, Catalogs;
    public long Bytes;
    public int ImagesTotal, ImagesNew, ImagesReused, ImagesFailed, ExcludedSmall;
    public bool Uploaded;
    /// <summary>Публичная ссылка на последний загруженный на S3 JSON.</summary>
    public string? Link;
}

/// <summary>
/// Выгрузка на исходящий S3. Folder — «корень/сеть» внутри бакета; Images — сжимать картинки и класть в Folder/images/,
/// подменяя ссылки в JSON; UploadJson — загружать готовый JSON в Folder/.
/// </summary>
public sealed record JsonS3Options(S3Connection Conn, string Folder, bool Images, bool UploadJson, int Threads,
                                   bool KeepLocal = true, bool ExcludeSmall = true);

/// <summary>Строка предрасчёта: что получится в одном результирующем файле.</summary>
public sealed record JsonScopeRow(string Title, int Products, int Offers, int Catalogs, long EstimatedBytes, string? Problem,
                                   int AvgPerCatalog = 0);

/// <summary>Итог предрасчёта: без записи файлов и скачивания картинок.</summary>
public sealed record JsonPreview(List<JsonScopeRow> Rows, int SourceOffers, int SourceProducts, long SourceBytes);
