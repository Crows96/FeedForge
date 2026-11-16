using System.Text;
using System.Text.Json.Serialization;

namespace FeedForge;

/// <summary>К чему применяется лимит количества офферов (полностью работает в сборке JSON, шаг 3).</summary>
public enum LimitMode { WholeFile, PerCatalog, PerResultFile }

/// <summary>Что делать, если фид «старый» (не обновлялся сегодня).</summary>
public enum StaleAction { Ask, AlwaysConvert, AlwaysSkip }

/// <summary>Один источник сети: ссылка или локальный файл (формат строки в диалоге: источник;название;регион).</summary>
public sealed class NetworkSource
{
    public string Source { get; set; } = "";
    public string? Label { get; set; }
    public string? Region { get; set; }
}

/// <summary>Сеть: источники + свои настройки + состояние последнего запуска. Хранится отдельным файлом.</summary>
public sealed class NetworkConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Короткое имя латиницей: папка результата и (позже) папка на S3.</summary>
    public string Slug { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<NetworkSource> Sources { get; set; } = new();
    public UiSettings Build { get; set; } = new();
    public int LimitMode { get; set; } = 1;   // 1 — на каждый каталог
    /// <summary>Режим отбора: 0 — как в файлах, 1 — одна цена на товар, 2 — свои настройки. Старые сети остаются на «свои».</summary>
    public int Preset { get; set; } = 2;
    /// <summary>Каталог из YML-офферов: срок действия в днях и текст условий (пусто — стандартный).</summary>
    public int YmlDays { get; set; } = 7;
    public string YmlConditions { get; set; } = "";
    /// <summary>Загружать готовый JSON на исходящий S3.</summary>
    public bool UploadToS3 { get; set; }
    /// <summary>Сжимать картинки, класть их на S3 (папка сети/images) и подставлять ссылки в JSON.</summary>
    public bool ImagesToS3 { get; set; }
    /// <summary>Исключать из JSON офферы с картинкой меньше 400 px (минимум по правилам Едадил).</summary>
    public bool ExcludeSmallImages { get; set; } = true;
    public StaleAction StaleAction { get; set; } = StaleAction.Ask;
    public DateTime? LastRunUtc { get; set; }
    public string LastStatus { get; set; } = "";
}

/// <summary>Запись истории запусков сети.</summary>
public sealed class RunRecord
{
    public DateTime StartedUtc { get; set; }
    /// <summary>JSON, Excel или Предрасчёт.</summary>
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public double Seconds { get; set; }
    public int Files { get; set; }
    public int Offers { get; set; }
    public int Catalogs { get; set; }
    public long Bytes { get; set; }
    public int ImagesNew { get; set; }
    public int ImagesReused { get; set; }
    public int ImagesFailed { get; set; }
    public int ExcludedSmall { get; set; }
    public bool Uploaded { get; set; }
}

/// <summary>Глобальные настройки программы (относятся к компьютеру, а не к сети).</summary>
public sealed class AppSettings
{
    public string DataDir { get; set; } = "";
    public string CacheDir { get; set; } = "";
    /// <summary>Куда сохраняются результаты Excel (локально, на ПК).</summary>
    public string OutputDir { get; set; } = "";
    public bool KeepSources { get; set; } = true;
    /// <summary>Оставлять ли локальную копию JSON после успешной загрузки на S3 (при ошибке загрузки файл остаётся всегда).</summary>
    public bool KeepLocalJson { get; set; }
    public int TimezoneHours { get; set; } = 3;
    /// <summary>Фид считается старым, если не обновлялся столько дней и больше (1 = «не обновлён сегодня»).</summary>
    public int StaleDays { get; set; } = 1;
    public int Threads { get; set; } = 16;
    public bool Economy { get; set; }

    /// <summary>Входящие подключения S3 (откуда берём сырые фиды): до двух, только чтение.</summary>
    public List<S3Connection> S3Inputs { get; set; } = new();
    /// <summary>Исходящее подключение S3 (куда выгружаем готовый результат). Загрузка — шаг 4.</summary>
    public S3Connection? S3Output { get; set; }

    [JsonIgnore]
    public IEnumerable<S3Connection> ConfiguredInputs => S3Inputs.Where(c => c.IsConfigured);

    /// <summary>Входящее подключение по имени бакета (адрес s3://бакет/...).</summary>
    public S3Connection? FindInput(string bucket) =>
        ConfiguredInputs.FirstOrDefault(c => string.Equals(c.Bucket.Trim(), bucket, StringComparison.OrdinalIgnoreCase));

    public void Normalize()
    {
        S3Inputs ??= new List<S3Connection>();
        S3Inputs.RemoveAll(c => c is null);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(DataDir)) DataDir = Path.Combine(appData, "FeedForge", "data");
        if (string.IsNullOrWhiteSpace(CacheDir)) CacheDir = Path.Combine(local, "FeedForge", "cache");
        if (string.IsNullOrWhiteSpace(OutputDir)) OutputDir = Path.Combine(docs, "FeedForge");
        TimezoneHours = Math.Clamp(TimezoneHours, -12, 14);
        StaleDays = Math.Clamp(StaleDays, 1, 365);
        Threads = Math.Clamp(Threads, 1, 64);
    }
}

public static class Translit
{
    private static readonly Dictionary<char, string> Map = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya"
    };

    /// <summary>«Спар Супермаркет 3791» → «spar_supermarket_3791».</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        bool lastSep = true;
        foreach (char ch in name.Trim().ToLowerInvariant())
        {
            if (Map.TryGetValue(ch, out var t))
            {
                sb.Append(t);
                if (t.Length > 0) lastSep = false;
            }
            else if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastSep = false;
            }
            else if (!lastSep)
            {
                sb.Append('_');
                lastSep = true;
            }
        }
        string s = sb.ToString().Trim('_');
        return s.Length == 0 ? "network" : s;
    }
}

/// <summary>Свежесть фида по дате изменения файла (без скачивания). Правило как в FeedChecker: «обновлён сегодня» или «старый на N дн.».</summary>
public static class Freshness
{
    public static DateOnly Today(AppSettings s) => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(s.TimezoneHours));

    public static int? AgeDays(DateTimeOffset? lastModified, AppSettings s)
    {
        if (lastModified is null) return null;
        var d = DateOnly.FromDateTime(lastModified.Value.UtcDateTime.AddHours(s.TimezoneHours));
        return Math.Max(0, Today(s).DayNumber - d.DayNumber);
    }

    public static bool IsStale(int? age, AppSettings s) => age is { } a && a >= s.StaleDays;

    public static string Describe(int? age) => age switch
    {
        null => "дата неизвестна",
        0 => "обновлён сегодня",
        1 => "старый на 1 дн.",
        var a => $"старый на {a} дн."
    };
}
