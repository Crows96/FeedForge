using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FeedForge;

public static partial class Converter
{
    // YML-фиды часто в windows-1251: в .NET такие кодировки доступны только после регистрации провайдера
    static Converter() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // ======================= Модель данных =======================

    // Цена одного оффера (0 — цены нет) и код каталога (гео): >= 0 — один каталог,
    // -1 — нет каталога, <= -2 — оффер лежит в нескольких каталогах (индекс в GeoModel.Multi = -Cat - 2)
    // Id — хэш id оффера из файла (0 — неизвестен): по нему при сборке JSON находим исходный оффер
    private readonly record struct PriceEntry(decimal Price, int Cat, ulong Id = 0, bool Promo = false);   // Promo: у оффера есть price_old

    private sealed class Group
    {
        public string Description = "";
        public string? ImageUrl;
        public List<PriceEntry> Offers = new();
        public HashSet<int> Shops = new();      // уникальные магазины (target_shops), где есть товар
        public HashSet<int> Regions = new();    // регионы (target_regions / region), где есть товар
        public DateTime? DateStart;
        public DateTime? DateEnd;
        public decimal? OldPrice;               // старая (обычная) цена — есть в YML (oldprice)
    }

    private sealed class GeoModel
    {
        public List<string> RegionNames = new();
        public List<string> ShopNames = new();                            // индекс магазина -> значение из target_shops
        public List<int[]> Multi = new();                                 // офферы, лежащие в нескольких каталогах
        public int[][] CatRegions = Array.Empty<int[]>();                 // каталог -> регионы
        public int[][] CatShops = Array.Empty<int[]>();                   // каталог -> магазины
        public Dictionary<int, HashSet<int>> RegionShops = new();         // регион -> магазины его каталогов
        public HashSet<int> UsedShops = new();                            // магазины, в которых реально есть товары
        public HashSet<int> UsedRegions = new();                          // регионы, в которых реально есть товары
    }

    // Одна выгрузка: либо весь файл, либо один регион
    private sealed class Scope
    {
        public string Title = "";
        public string Dir = "";
        public int Region = -1;                         // -1 — все регионы
        public int Shop = -1;                           // -1 — не по магазину; иначе выгрузка одного магазина
        public string Kind = "Регион";                  // подпись для лога: «Регион» / «Магазин»
        public List<Group> Candidates = new();
        public HashSet<int> ShopSet = new();            // магазины, по которым считаем охват
        public List<(Group Group, decimal Price)>? Preselected;   // отбор уже сделан (режим «по регионам, один Excel»)
    }

    private sealed class SelStats
    {
        public int Dropped, NoPrice, Single, Equal, Similar, ShopDropped;

        // Подробности для журнала предрасчёта
        public int Candidates, Promo;                  // уникальных товаров (по названию) в выгрузке; из них акционных
        public long Offers;                            // офферов с корректной ценой во всех этих товарах
        public int OneGeo, MultiGeo;                   // товар в одном гео (каталоге) / в нескольких
        public int Geo2to5, Geo6to20, Geo21to100, GeoOver100;
        // Товары в нескольких гео, прошедшие охват по магазинам и имеющие цену: как соотносятся цены в разных гео
        public int MultiEqual, MultiSimilar, MultiDropped;
        public long MultiEqualOffers, MultiSimilarOffers;
        public decimal SimilarSpreadSum, SimilarSpreadMax, DroppedSpreadMax;   // доли (0.05 = 5%)
    }

    private sealed record Row(string Description, string? ImageFile, decimal Price, DateTime? DateStart, DateTime? DateEnd, decimal? OldPrice = null);

    private sealed class Part
    {
        public List<Row> Rows = new();
        public HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase);
        public long Bytes;
    }

    // ======================= Точка входа =======================

    public static bool IsUrl(string s) =>
        Uri.TryCreate(s?.Trim(), UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>Возвращает путь к папке с результатом.</summary>
    public static async Task<string> RunAsync(
        ConvertOptions opt, IProgress<string> log, IProgress<double> percent, CancellationToken ct)
    {
        var createdParts = new List<string>();   // папки частей, созданные в этом запуске
        var runner = new Runner(opt, log, percent, ct, createdParts);
        try
        {
            return await runner.RunAsync();
        }
        catch (OperationCanceledException)
        {
            // Пользователь нажал «Стоп»: удаляем всё временное и незаконченное
            CleanupAfterCancel(opt.OutDir, createdParts);
            log.Report("Остановлено. Временные файлы удалены.");
            throw;
        }
    }

    /// <summary>
    /// Предрасчёт сборки JSON: те же чтение и отбор, что при настоящей сборке, но без записи файлов и без картинок.
    /// Нужен ConvertOptions с заполненным Json.
    /// </summary>
    public static async Task<JsonPreview> PreviewJsonAsync(
        ConvertOptions opt, IProgress<string> log, IProgress<double> percent, CancellationToken ct)
    {
        if (opt.Json == null) throw new ArgumentException("Для предрасчёта нужны настройки JSON.");
        var runner = new Runner(opt, log, percent, ct, new List<string>(), previewOnly: true);
        await runner.RunAsync();
        return new JsonPreview(runner.Rows, runner.SourceOffers, runner.SourceProducts, runner.SourceBytes);
    }

    /// <summary>Файл начинается с «&lt;» (после BOM и пробелов) — это XML/YML, а не JSON.</summary>
    public static bool LooksLikeXml(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[4096];
            int n = fs.Read(buf, 0, buf.Length);
            int i = 0;
            if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            while (i < n && buf[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
            return i < n && buf[i] == (byte)'<';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Обрабатывает несколько JSON по очереди; каждый файл — отдельная выгрузка в своей папке.</summary>
    public static async Task<string> RunBatchAsync(
        IReadOnlyList<ConvertOptions> items, IProgress<string> log, IProgress<double> percent, CancellationToken ct)
    {
        int n = items.Count;
        int ok = 0;
        var errors = new List<string>();
        var keptCache = new List<string>();      // файлы, у которых остался _cache (не все картинки скачались)

        try
        {
            for (int i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = items[i];
                if (n > 1) log.Report($"########## Файл {i + 1} из {n}: {item.JsonSource} ##########");

                int fileIndex = i;
                var filePercent = new DelegateProgress<double>(p => percent.Report((fileIndex * 100.0 + p) / n));
                try
                {
                    await RunAsync(item, log, filePercent, ct);
                    ok++;
                    if (Directory.Exists(Path.Combine(item.OutDir, "_cache"))) keptCache.Add(item.JsonSource);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (n > 1)
                {
                    // Один «плохой» файл не должен останавливать остальные
                    errors.Add($"{item.JsonSource}: {ex.Message}");
                    log.Report($"ОШИБКА в файле {item.JsonSource}: {ex.Message}");
                }

                // Между файлами освобождаем память
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        catch (OperationCanceledException)
        {
            if (n > 1)
            {
                try
                {
                    var parent = Path.GetDirectoryName(Path.GetFullPath(items[0].OutDir));
                    if (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                        Directory.Delete(parent);
                }
                catch { }
            }
            throw;
        }

        percent.Report(100);
        var sb = new StringBuilder();
        if (n == 1 && items[0].Feeds != null)
        {
            // Режим объединения: сколько файлов списка вошло в результат
            int total = items[0].Feeds!.Count;
            string failedPath = Path.Combine(items[0].OutDir, "failed_feeds.txt");
            int failed = File.Exists(failedPath) ? File.ReadAllLines(failedPath).Length : 0;
            sb.Append($"Объединено файлов: {total - failed} из {total}");
            if (failed > 0)
                sb.Append($"\n⚠ Не прочитано: {failed}. Список для повторной загрузки: {failedPath}");
        }
        else
            sb.Append($"Обработано файлов: {ok} из {n}");
        foreach (var e in errors) sb.Append("\n  ✗ ").Append(e);
        if (keptCache.Count > 0)
        {
            sb.Append("\n⚠ Часть картинок не скачалась, папка _cache оставлена (повторный запуск докачает недостающее):");
            foreach (var k in keptCache) sb.Append("\n  ").Append(k);
        }
        return sb.ToString();
    }

    private static void CleanupAfterCancel(string outDir, List<string> createdParts)
    {
        string fullOut = Path.GetFullPath(outDir);

        foreach (var p in createdParts)
        {
            try { if (Directory.Exists(p)) Directory.Delete(p, true); } catch { }

            // Если часть лежала в папке региона и там больше ничего нет — убираем и папку региона
            try
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(p));
                if (parent != null && !string.Equals(parent, fullOut, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    Directory.Delete(parent);
            }
            catch { }
        }
        try
        {
            string cache = Path.Combine(outDir, "_cache");
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }
        catch { }
        try
        {
            string src = Path.Combine(outDir, "_source.json");
            if (File.Exists(src)) File.Delete(src);
        }
        catch { }
        try
        {
            // Папку результата убираем, только если в ней ничего не осталось
            if (Directory.Exists(outDir) && !Directory.EnumerateFileSystemEntries(outDir).Any())
                Directory.Delete(outDir);
        }
        catch { }
    }

    // ======================= Вспомогательные функции =======================

    /// <summary>Определяет формат по содержимому: XML (YML) начинается с «&lt;», JSON — с «{» или «[».</summary>
    public static SourceFormat DetectFormat(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buf = new byte[512];
        int n = fs.Read(buf, 0, buf.Length);

        // UTF-16 BOM: так бывают только XML-файлы (JSON читается как UTF-8)
        if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF)))
            return SourceFormat.Xml;

        int i = (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) ? 3 : 0;   // UTF-8 BOM
        for (; i < n; i++)
        {
            byte b = buf[i];
            if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n') continue;
            return b == (byte)'<' ? SourceFormat.Xml : SourceFormat.Json;
        }
        return SourceFormat.Json;
    }

    private static XmlReaderSettings CreateXmlSettings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore,   // в фидах есть <!DOCTYPE ... SYSTEM "shops.dtd">: DTD не загружаем
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false                 // «грязные» управляющие символы в фидах не должны ронять чтение
    };

    // Цена из YML: "195.9", "1 299,90", "1,299.50". Если есть и запятая, и точка, десятичный разделитель — правый.
    private static decimal? ParseYmlPrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        string t = sb.ToString();

        int comma = t.LastIndexOf(','), dot = t.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
        {
            char dec = comma > dot ? ',' : '.';
            char thousands = dec == ',' ? '.' : ',';
            t = t.Replace(thousands.ToString(), "").Replace(dec, '.');
        }
        else if (comma >= 0)
        {
            t = t.Replace(',', '.');
        }

        return decimal.TryParse(t, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                                CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    // Ссылка на картинку: "//cdn..." -> https, относительные достраиваем по адресу магазина, иначе пропускаем
    private static string? ResolvePictureUrl(string? pic, string? shopUrl)
    {
        pic = pic?.Trim();
        if (string.IsNullOrEmpty(pic)) return null;
        if (pic.StartsWith("//")) pic = "https:" + pic;

        if (Uri.TryCreate(pic, UriKind.Absolute, out var abs))
            return abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps ? pic : null;

        if (!string.IsNullOrEmpty(shopUrl) && Uri.TryCreate(shopUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, pic, out var combined))
            return combined.AbsoluteUri;

        return null;
    }

    // Потоковое чтение: находит в корне JSON массив с именем property и вызывает onElement для каждого
    // элемента-объекта. В памяти одновременно лежат только буфер (~1 МБ) и один элемент.
    // Возвращает false, если такого массива в файле нет.
    private static bool ScanArray(string path, string property, Action<JsonElement> onElement,
                                  CancellationToken ct, Action<double>? onPercent = null)
    {
        var options = new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                                      FileOptions.SequentialScan);
        long length = Math.Max(1, fs.Length);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            int have = 0;                       // сколько байт сейчас лежит в буфере
            bool eof = false, first = true;
            bool inTarget = false, inArray = false, found = false, done = false;
            int counter = 0;
            double lastPercent = -1;
            var state = new JsonReaderState(options);

            while (!done)
            {
                // Дочитываем буфер
                while (!eof && have < buffer.Length)
                {
                    int n = fs.Read(buffer, have, buffer.Length - have);
                    if (n == 0) { eof = true; break; }
                    have += n;
                }

                if (first)
                {
                    first = false;
                    // Utf8JsonReader сам BOM не пропускает
                    if (have >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
                    {
                        buffer.AsSpan(3, have - 3).CopyTo(buffer);
                        have -= 3;
                    }
                }

                var reader = new Utf8JsonReader(buffer.AsSpan(0, have), eof, state);
                bool needMore = false;

                while (true)
                {
                    var before = reader;
                    if (!reader.Read()) break;

                    var token = reader.TokenType;
                    int depth = reader.CurrentDepth;

                    if (depth == 1 && token == JsonTokenType.PropertyName)
                    {
                        inTarget = reader.ValueTextEquals(property);
                    }
                    else if (depth == 1 && token == JsonTokenType.StartArray && inTarget)
                    {
                        inArray = true;
                        found = true;
                    }
                    else if (depth == 1 && token == JsonTokenType.EndArray && inArray)
                    {
                        done = true;                    // нужный массив прочитан целиком
                        break;
                    }
                    else if (depth == 2 && token == JsonTokenType.StartObject && inArray)
                    {
                        // Элемент должен целиком поместиться в буфер, иначе дочитываем файл и повторяем
                        var probe = reader;
                        if (!probe.TrySkip())
                        {
                            reader = before;
                            needMore = true;
                            break;
                        }

                        using var doc = JsonDocument.ParseValue(ref reader);
                        onElement(doc.RootElement);

                        if ((++counter & 255) == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (onPercent != null)
                            {
                                double p = Math.Floor(fs.Position * 1000.0 / length) / 10.0;   // с точностью до 0,1%
                                if (p != lastPercent) { lastPercent = p; onPercent(p); }
                            }
                        }
                    }
                }

                if (done) break;
                if (eof && !needMore) break;            // файл закончился

                state = reader.CurrentState;
                int consumed = (int)reader.BytesConsumed;
                int left = have - consumed;
                if (left > 0 && consumed > 0) buffer.AsSpan(consumed, left).CopyTo(buffer);
                have = left;

                if (have == buffer.Length)              // один элемент не поместился — увеличиваем буфер
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, have).CopyTo(bigger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }
                ct.ThrowIfCancellationRequested();
            }
            return found;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // 64-битный хэш (FNV-1a) строки: ключ для таблицы «оффер -> каталог» без хранения самих строк
    private static ulong HashId(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (char ch in s)
        {
            h ^= ch;
            h *= 1099511628211UL;
        }
        return h;
    }

    private static async Task DownloadJsonAsync(string url, string path, IProgress<string> log, CancellationToken ct, bool quiet = false)
    {
        if (!quiet) log.Report("Скачиваю JSON по ссылке...");
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

        try
        {
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buf = new byte[81920];
            long total = 0, lastLogged = 0;
            int read;
            while ((read = await src.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                total += read;
                if (total - lastLogged >= 10L * 1024 * 1024)
                {
                    lastLogged = total;
                    if (!quiet) log.Report($"JSON: скачано {total / 1024.0 / 1024.0:F0} МБ...");
                }
            }
            if (!quiet) log.Report($"JSON скачан: {total / 1024.0 / 1024.0:F1} МБ");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new Exception("Не удалось скачать JSON по ссылке: " + ex.Message);
        }
    }

    // Товары с разными кодами, но одинаковым названием И одинаковой ссылкой на картинку,
    // считаем одним товаром: их цены сравниваются вместе.
    // Если у товара нет названия или картинки, по этому признаку он не объединяется.
    private static List<Group> MergeSameProducts(List<Group> groups)
    {
        var result = new List<Group>();
        var index = new Dictionary<string, Group>();

        foreach (var g in groups)
        {
            string desc = NormalizeText(g.Description);
            string img = g.ImageUrl?.Trim() ?? "";

            if (desc.Length == 0 || img.Length == 0)
            {
                result.Add(g);
                continue;
            }

            string key = desc + "\u0001" + img;
            if (index.TryGetValue(key, out var first))
            {
                first.Offers.AddRange(g.Offers);
                first.Shops.UnionWith(g.Shops);
                first.Regions.UnionWith(g.Regions);
                first.DateStart ??= g.DateStart;
                first.DateEnd ??= g.DateEnd;
                first.OldPrice ??= g.OldPrice;
            }
            else
            {
                index[key] = g;
                result.Add(g);
            }
        }
        return result;
    }

    // Без учёта регистра и лишних пробелов
    private static string NormalizeText(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static int CountIn(HashSet<int> items, HashSet<int> set)
    {
        int n = 0;
        foreach (var x in items)
            if (set.Contains(x)) n++;
        return n;
    }

    private static int Intern(Dictionary<string, int> index, List<string>? names, string value)
    {
        value = value.Trim();
        if (!index.TryGetValue(value, out var i))
        {
            i = index.Count;
            index[value] = i;
            names?.Add(value);
        }
        return i;
    }

    private static IEnumerable<string> GetStringList(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String) continue;
            var s = x.GetString();
            if (!string.IsNullOrWhiteSpace(s)) yield return s;
        }
    }

    private static string SafeFolderName(string name, string fallback = "region")
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = Regex.Replace(name, @"\s+", " ").Trim().TrimEnd('.');
        if (name.Length > 80) name = name[..80].TrimEnd();
        return name.Length == 0 ? fallback : name;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        string unique = name;
        int k = 2;
        while (!used.Add(unique)) unique = $"{name} ({k++})";
        return unique;
    }

    private static decimal PickPrice(List<decimal> prices, PriceMode mode)
    {
        switch (mode)
        {
            case PriceMode.Max:
                return prices.Max();
            case PriceMode.Average:
                return Math.Round(prices.Average(), 2);
            case PriceMode.Median:
            {
                var s = prices.OrderBy(p => p).ToList();
                int n = s.Count;
                return n % 2 == 1 ? s[n / 2] : Math.Round((s[n / 2 - 1] + s[n / 2]) / 2m, 2);
            }
            default:
                return prices.Min();
        }
    }

    private static string? GetString(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? GetDate(JsonElement e, string key)
    {
        var s = GetString(e, key);
        // DateTimeOffset сохраняет календарную дату, записанную в JSON, даже если указан часовой пояс
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.Date : null;
    }

    private static decimal? GetDecimal(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetDecimal(out var n) ? n : null;
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    // Имя файла картинки для Excel и папки: из последней части ссылки.
    // Если имя типа «800x800.jpeg» (одинаковое у всех товаров), добавляем предыдущую часть пути.
    private static string MakeFileName(string url)
    {
        string name = "";
        try
        {
            var uri = new Uri(url);
            var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length > 0)
            {
                string last = segs[^1];
                if (segs.Length > 1 && Regex.IsMatch(Path.GetFileNameWithoutExtension(last), @"^\d{2,4}x\d{2,4}$"))
                    last = segs[^2] + "_" + last;
                name = Uri.UnescapeDataString(last);
            }
        }
        catch { name = ""; }

        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');

        string ext = Path.GetExtension(name);
        string stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length > 90) stem = stem[..90];
        if (stem.Length == 0) stem = HashHex(url, 12);
        if (ext.Length == 0 || ext.Length > 6) ext = ".jpg";
        return stem + ext;
    }

    // Имя файла в кэше зависит от ссылки и настроек сжатия — устаревшие файлы от другого запуска
    // (с другим качеством или размером) не подменят картинку
    private static string CacheName(string url, string displayName, string signature) =>
        HashHex(url + "|" + signature, 20) + Path.GetExtension(displayName);

    private static string HashHex(string text, int length) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..length];
}

/// <summary>Синхронный IProgress: вызывает делегат сразу в том же потоке (Progress&lt;T&gt; может менять порядок вызовов).</summary>
internal sealed class DelegateProgress<T> : IProgress<T>
{
    private readonly Action<T> action;
    public DelegateProgress(Action<T> action) => this.action = action;
    public void Report(T value) => action(value);
}
