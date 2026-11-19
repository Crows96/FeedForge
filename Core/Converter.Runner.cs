using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FeedForge;

// Основной процесс: чтение источников, состав выгрузок (регионы, магазины).
public static partial class Converter
{
    private sealed partial class Runner
    {
        private readonly ConvertOptions opt;
        private readonly IProgress<string> log;
        private readonly IProgress<double> percent;
        private readonly CancellationToken ct;
        private readonly List<string> createdParts;

        private readonly string outDir;
        private readonly string cacheDir;                       // временная папка для скачанных картинок
        private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
        private readonly int downloadThreads;
        private readonly SemaphoreSlim encodeGate;                  // сколько картинок одновременно сжимается
        private long bytesIn, bytesOut;                             // объём картинок до и после сжатия

        // Имя картинки в Excel/частях (читаемое, уникальное) и имя файла в кэше (по хэшу ссылки)
        private readonly Dictionary<string, string> urlToName = new();
        private readonly Dictionary<string, string> nameToCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> usedNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentBag<string> failed = new();        // временные сбои: повторный запуск может помочь
        private readonly ConcurrentBag<string> missing = new();       // картинки нет на сервере (404/410) или это не картинка

        private string? tempJson;
        private int scopeIndex;
        private int scopeTotal = 1;

        private readonly bool previewOnly;
        // Локальные файлы-источники JSON (нужны при сборке): соль добавляется к id офферов файла при расчёте хэша,
        // чтобы одинаковые id из разных файлов (в режиме объединения) не путались; для одного файла соль пустая
        private readonly List<(string Salt, string Path)> jsonFiles = new();
        private string idSalt = "";
        // Порядок каталогов каждого файла -> общий индекс каталога (по «соли» файла); нужен, чтобы при записи знать,
        // какие офферы выбраны именно для этого каталога
        private readonly Dictionary<string, List<int>> catPos = new();
        // Для лимита «на каждый каталог»: каталог -> выбранные для него офферы (оффер, лежащий в двух каталогах,
        // может быть выбран для одного из них и не попасть в список другого)
        private Dictionary<int, HashSet<ulong>>? lastChosen;
        private static readonly HashSet<ulong> NoOffers = new();
        // Офферы с ошибками по правилам FeedChecker (нет обязательного ключа) в обработку не берутся
        private int invalidTotal;
        private readonly Dictionary<string, int> invalidBy = new();
        private static readonly string[] RequiredKeys = { "description", "id", "image", "url", "price_new" };

        // Первый обязательный ключ, которого нет или он пустой (число или непустая строка считаются заполненными)
        private static string? FirstMissingRequiredKey(JsonElement o)
        {
            foreach (var k in RequiredKeys)
            {
                if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(k, out var v)) return k;
                bool ok = v.ValueKind == JsonValueKind.Number ||
                          (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()));
                if (!ok) return k;
            }
            return null;
        }

        private void CountInvalid(string key, int n = 1)
        {
            invalidTotal += n;
            invalidBy[key] = invalidBy.TryGetValue(key, out var c) ? c + n : n;
        }
        // YML-файлы-источники (в JSON-режиме из их офферов строится один каталог с target «Россия»)
        private readonly List<(string Salt, string Path)> ymlFiles = new();
        private readonly HashSet<int> ymlCats = new();      // коды «каталогов» YML-файлов (режим объединения)
        private bool ymlAll;                                // единственный файл — YML (все записи с Cat = -1)
        private readonly Dictionary<ulong, decimal> ymlPrice = new();   // цена выбранного оффера YML (по режиму «Цена»)
        private int ymlKept;
        private readonly Dictionary<ulong, decimal> commonPrice = new();   // общий каталог: цена выбранного оффера товара
        private bool IsYml(PriceEntry e) => ymlAll || ymlCats.Contains(e.Cat);
        private static string YmlId(YmlOffer o) => o.Id.Length > 0 ? o.Id : "h" + HashId(o.Name).ToString("x");
        public readonly List<JsonScopeRow> Rows = new();
        public int SourceOffers, SourceProducts;
        public long SourceBytes;

        public Runner(ConvertOptions opt, IProgress<string> log, IProgress<double> percent,
                      CancellationToken ct, List<string> createdParts, bool previewOnly = false)
        {
            this.previewOnly = previewOnly;
            this.opt = opt;
            this.log = log;
            this.percent = percent;
            this.ct = ct;
            this.createdParts = createdParts;
            outDir = opt.OutDir;
            cacheDir = Path.Combine(outDir, "_cache");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

            downloadThreads = Math.Clamp(opt.DownloadThreads, 1, 64);
            int encodeThreads = opt.Economy ? 1 : Math.Max(1, Environment.ProcessorCount - 1);
            encodeGate = new SemaphoreSlim(encodeThreads, encodeThreads);
        }

        public async Task<string> RunAsync()
        {
            try { return await CoreAsync(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ошибка посреди работы: _cache и _source.json не оставляем
                // (при «Стоп» временные файлы убирает внешний код)
                Cleanup(keepCache: false);
                try
                {
                    if (Directory.Exists(outDir) && !Directory.EnumerateFileSystemEntries(outDir).Any())
                        Directory.Delete(outDir);   // пустую папку результата тоже убираем
                }
                catch { }
                throw;
            }
            finally { http.Dispose(); encodeGate.Dispose(); }
        }

        // Общий прогресс: каждая выгрузка занимает свою долю шкалы (внутри: 0–80% скачивание, 80–99% упаковка)
        private void Pct(double local)
        {
            double v = Math.Min(99.9, pctBase + (scopeIndex * 100.0 + Math.Clamp(local, 0, 100)) / scopeTotal * pctSpan / 100.0);
            if (v <= lastPct) return;   // полоса идёт только вперёд (несколько кругов отбора, несколько выгрузок)
            lastPct = v;
            percent.Report(v);
        }
        private int pctBase = 0, pctSpan = 100;
        private double lastPct;

        private void Cleanup(bool keepCache)
        {
            if (tempJson != null)
            {
                try { File.Delete(tempJson); } catch { }
            }
            if (!keepCache && Directory.Exists(cacheDir))
            {
                try { Directory.Delete(cacheDir, true); }
                catch (Exception ex) { log.Report("Не удалось удалить временную папку _cache: " + ex.Message); }
            }

            // Если выгружать оказалось нечего, пустую папку результата не оставляем
            try
            {
                if (Directory.Exists(outDir) && !Directory.EnumerateFileSystemEntries(outDir).Any())
                    Directory.Delete(outDir);
            }
            catch { }
        }

        private async Task<string> CoreAsync()
        {
            if (!previewOnly)
            {
                Directory.CreateDirectory(outDir);
                if (opt.Json == null) Directory.CreateDirectory(cacheDir);
            }

            List<Group> groups;
            int offersCount;
            GeoModel geo;

            if (opt.Feeds != null)
            {
                // ---------- Несколько YML-файлов: каждый файл — отдельный магазин ----------
                (groups, offersCount, geo) = await ReadGroupsFeedsAsync();
                try { SourceBytes = jsonFiles.Concat(ymlFiles).Sum(f => new FileInfo(f.Path).Length); } catch { }
                pctBase = 40;       // чтение файлов заняло первые 40% полосы прогресса
                pctSpan = 60;
            }
            else
            {
                // ---------- 0. Если указана ссылка — сначала скачиваем файл ----------
                string jsonPath = opt.JsonSource.Trim();
                if (IsUrl(jsonPath))
                {
                    tempJson = Path.Combine(outDir, "_source.json");
                    await DownloadJsonAsync(jsonPath, tempJson, log, ct);
                    jsonPath = tempJson;
                }

                // ---------- 1. Читаем файл и группируем записи по товару ----------
                var format = DetectFormat(jsonPath);
                if (format == SourceFormat.Xml) { ymlFiles.Add(("", jsonPath)); ymlAll = true; }
                else jsonFiles.Add(("", jsonPath));
                try { SourceBytes = new FileInfo(jsonPath).Length; } catch { }
                log.Report(format == SourceFormat.Xml ? "Читаю YML (XML)..." : "Читаю JSON...");
                (groups, offersCount, geo) = format == SourceFormat.Xml
                    ? ReadGroupsXml(jsonPath, ct)
                    : ReadGroups(jsonPath, ct);
                pctBase = 40;       // чтение файла заняло первые 40% шкалы прогресса
                pctSpan = 60;
            }
            GC.Collect();   // служебные структуры чтения больше не нужны
            ct.ThrowIfCancellationRequested();
            log.Report($"Записей в файле: {offersCount}, уникальных товаров: {groups.Count}");
            SourceOffers = offersCount;
            SourceProducts = groups.Count;
            if (invalidTotal > 0)
                log.Report($"Исключено офферов с ошибками: {invalidTotal} " +
                           $"(причины: {string.Join(", ", invalidBy.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} — {kv.Value}"))})");
            if (geo.UsedShops.Count > 0 || geo.UsedRegions.Count > 0)
                log.Report($"География: магазинов — {geo.UsedShops.Count}, регионов — {geo.UsedRegions.Count}");

            if (opt.MergeSame)
            {
                int before = groups.Count;
                groups = MergeSameProducts(groups);
                if (groups.Count < before)
                    log.Report($"Объединено товаров с одинаковым названием и картинкой: {before - groups.Count} " +
                               $"(осталось {groups.Count})");
            }

            // ---------- 2. Выгрузки: весь файл / один Excel на все регионы / Excel на каждый регион ----------
            var scopes = BuildScopes(groups, geo);
            scopeCount = scopes.Count;
            if (scopes.Count == 0)
                throw new InvalidOperationException("Нет товаров для выгрузки: ни один товар не подошёл под выбранный режим. Причины — в журнале выше.");

            scopeTotal = scopes.Count;
            for (scopeIndex = 0; scopeIndex < scopes.Count; scopeIndex++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ProcessScopeAsync(scopes[scopeIndex]);
                }
                catch (InvalidOperationException ex) when (previewOnly)
                {
                    // в предрасчёте причина показывается строкой таблицы, остальные выгрузки считаются дальше
                    Rows.Add(new JsonScopeRow(scopes[scopeIndex].Title, 0, 0, 0, 0, ex.Message));
                }
            }

            if (previewOnly)
            {
                percent.Report(100);
                return outDir;
            }

            // Временную папку с картинками удаляем после упаковки. Если часть картинок не скачалась,
            // оставляем её: при повторном запуске докачаются только недостающие.
            Cleanup(keepCache: !failed.IsEmpty);

            if (opt.Recode.Enabled && bytesIn > 0)
                log.Report($"Картинки после сжатия: {bytesIn / 1048576.0:F0} МБ → {bytesOut / 1048576.0:F0} МБ " +
                           $"(в {(double)bytesIn / Math.Max(1, bytesOut):F1} раза меньше)");
            log.Report($"Готово. Результат: {outDir}");
            if (!missing.IsEmpty)
            {
                log.Report($"Картинок нет на сервере ({missing.Count}), эти товары в Excel не добавлены:");
                foreach (var f in missing) log.Report("  " + f);
            }
            if (!failed.IsEmpty)
            {
                log.Report($"Не удалось скачать картинки ({failed.Count}), эти товары в Excel не добавлены:");
                foreach (var f in failed) log.Report("  " + f);
                log.Report("Папка _cache сохранена — запустите программу ещё раз, чтобы докачать недостающее.");
            }
            percent.Report(100);
            return outDir;
        }

        // ---------- Состав выгрузок ----------
        private List<Scope> BuildScopes(List<Group> groups, GeoModel geo)
        {
            var scopes = new List<Scope>();
            var mode = opt.RegionMode;

            // «Во всех регионах или магазинах»: если регионов в файле нет, «все» считаем по магазинам
            bool allByShops = mode == RegionMode.AllRegions && geo.UsedRegions.Count == 0 && geo.UsedShops.Count > 1;

            if (!allByShops && (mode == RegionMode.AllRegions || mode == RegionMode.PerRegion || mode == RegionMode.RegionsMerged) && geo.UsedRegions.Count == 0)
            {
                log.Report("В файле нет регионов (target_regions / region) — режим регионов не применяется.");
                mode = RegionMode.Ignore;
            }
            if (mode == RegionMode.PerShop && geo.UsedShops.Count == 0)
            {
                log.Report("В файле нет магазинов (target_shops) — разбивка по магазинам не применяется.");
                mode = RegionMode.Ignore;
            }

            switch (mode)
            {
                case RegionMode.AllRegions:
                {
                    string unit = allByShops ? "магазинах" : "регионах";
                    int all = allByShops ? geo.UsedShops.Count : geo.UsedRegions.Count;
                    var cand = groups.Where(g => (allByShops ? g.Shops.Count : g.Regions.Count) == all).ToList();
                    log.Report($"Гео: только товары, которые есть во всех {unit} ({all}): {cand.Count}" +
                               (allByShops ? " (регионов в файле нет, поэтому считаются магазины)" : ""));
                    if (cand.Count == 0)
                    {
                        int best = groups.Count == 0 ? 0 : groups.Max(g => allByShops ? g.Shops.Count : g.Regions.Count);
                        log.Report($"Ни один товар не встречается во всех {unit} (максимальный охват — {best} из {all}). " +
                                   "Выберите другой вариант в настройке «Гео».");
                        return scopes;
                    }
                    scopes.Add(new Scope { Dir = outDir, Candidates = cand, ShopSet = geo.UsedShops });
                    break;
                }
                case RegionMode.PerRegion:
                {
                    var usedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (int r in geo.UsedRegions.OrderBy(x => x))
                    {
                        var cand = groups.Where(g => g.Regions.Contains(r)).ToList();
                        if (cand.Count == 0) continue;

                        var shopSet = new HashSet<int>();
                        if (geo.RegionShops.TryGetValue(r, out var rs))
                            foreach (var sh in rs)
                                if (geo.UsedShops.Contains(sh)) shopSet.Add(sh);

                        string folder = UniqueName(SafeFolderName(geo.RegionNames[r]), usedDirs);
                        scopes.Add(new Scope
                        {
                            Title = geo.RegionNames[r],
                            Region = r,
                            Dir = Path.Combine(outDir, folder),
                            Candidates = cand,
                            ShopSet = shopSet
                        });
                    }
                    log.Report($"Режим регионов: отдельный Excel на каждый регион — регионов: {scopes.Count}");
                    break;
                }
                case RegionMode.RegionsMerged:
                {
                    // Каждый регион проверяем отдельно: процент магазинов считается среди магазинов этого региона,
                    // разброс цен — среди цен этого региона. Товар попадает в общий Excel, если прошёл проверку
                    // в каждом регионе, где он есть. Цену для Excel выбираем по всем его офферам (режим цены из настроек).
                    log.Report($"Гео: один общий результат, фильтр считается в каждом регионе отдельно — регионов: {geo.UsedRegions.Count}");
                    var failedGroups = new HashSet<Group>();
                    foreach (int r in geo.UsedRegions.OrderBy(x => x))
                    {
                        var cand = groups.Where(g => g.Regions.Contains(r)).ToList();
                        if (cand.Count == 0) continue;

                        var shopSet = new HashSet<int>();
                        if (geo.RegionShops.TryGetValue(r, out var rs))
                            foreach (var sh in rs)
                                if (geo.UsedShops.Contains(sh)) shopSet.Add(sh);

                        bool useShops = opt.ShopFilter && shopSet.Count > 1;
                        int need = useShops ? Math.Max(1, (int)Math.Ceiling(shopSet.Count * opt.ShopPercent / 100m)) : 0;
                        var sub = new Scope { Title = geo.RegionNames[r], Region = r, Candidates = cand, ShopSet = shopSet };
                        var (sel, _) = SelectProducts(sub, useShops, need);

                        var pass = new HashSet<Group>(sel.Select(x => x.Group));
                        foreach (var g in cand)
                            if (!pass.Contains(g)) failedGroups.Add(g);

                        log.Report($"  {geo.RegionNames[r]}: магазинов {shopSet.Count}, товаров {cand.Count}, прошло {pass.Count}" +
                                   (useShops ? $" (нужно минимум в {need} магазинах)" : ""));
                    }

                    var allScope = new Scope();     // без региона и магазина: цены всех офферов
                    var final = new List<(Group Group, decimal Price)>();
                    int noRegion = 0;
                    foreach (var g in groups)
                    {
                        if (g.Regions.Count == 0) { noRegion++; continue; }
                        if (failedGroups.Contains(g)) continue;
                        var prices = ScopePrices(g, allScope);
                        if (prices.Count == 0) continue;
                        final.Add((g, PickPrice(prices, opt.PriceMode)));
                    }

                    log.Report($"Итого в общем результате: {final.Count} товаров. Отсеяно из-за регионов: {failedGroups.Count}" +
                               (noRegion > 0 ? $", без региона: {noRegion}" : ""));
                    if (final.Count == 0)
                    {
                        log.Report("Ни один товар не прошёл проверку во всех регионах, где он есть. " +
                                   "Уменьшите процент магазинов или увеличьте допустимую разницу цен.");
                        return scopes;
                    }
                    scopes.Add(new Scope
                    {
                        Dir = outDir,
                        Candidates = final.Select(x => x.Group).ToList(),
                        ShopSet = geo.UsedShops,
                        Preselected = final
                    });
                    break;
                }
                case RegionMode.PerShop:
                {
                    var usedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (int s in geo.UsedShops.OrderBy(x => x))
                    {
                        var cand = groups.Where(g => g.Shops.Contains(s)).ToList();
                        if (cand.Count == 0) continue;

                        string shopName = geo.ShopNames[s];
                        string folder = UniqueName(SafeFolderName(shopName, "shop"), usedDirs);
                        scopes.Add(new Scope
                        {
                            Title = shopName,
                            Kind = "Магазин",
                            Shop = s,
                            Dir = Path.Combine(outDir, folder),
                            Candidates = cand,
                            ShopSet = new HashSet<int> { s }
                        });
                    }
                    log.Report($"Режим: отдельный Excel на каждый магазин (target_shops) — магазинов: {scopes.Count}");
                    break;
                }
                default:
                    scopes.Add(new Scope { Dir = outDir, Candidates = groups, ShopSet = geo.UsedShops });
                    break;
            }
            return scopes;
        }
    }
}
