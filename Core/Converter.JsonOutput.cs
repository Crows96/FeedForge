using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FeedForge;

// Сборка JSON: отбор офферов с лимитом, общий каталог, запись файла, предрасчёт.
public static partial class Converter
{
    private sealed partial class Runner
    {
        // ---------- Отбор товаров внутри одной выгрузки ----------
        // ---------- Сборка JSON ----------

        // Каталоги оффера: обычный код — один каталог, составной (<= -2) — несколько, YML — один общий «Россия»
        private static readonly int[] YmlCatalogKey = { int.MinValue };

        private int[] CatalogsOf(PriceEntry e)
        {
            if (IsYml(e)) return YmlCatalogKey;
            if (e.Cat <= -2 && currentGeo != null)
            {
                int mi = -e.Cat - 2;
                if (mi >= 0 && mi < currentGeo.Multi.Count) return currentGeo.Multi[mi];
            }
            return new[] { e.Cat };
        }

        // Сколько офферов не взято, потому что в исходных данных они не указаны ни в одном каталоге
        private int noCatalogSkipped;

        // Один товар (название) в одном каталоге — один оффер. В исходных данных у одного товара бывает несколько id
        // в одном каталоге (например, с разными кодами региона); без этой проверки дубли съедают место в лимите.
        private List<PriceEntry> OnePerCatalog(List<PriceEntry> entries)
        {
            if (entries.Count < 2) return entries;
            var seen = new HashSet<int>();
            var res = new List<PriceEntry>(entries.Count);
            foreach (var e in entries)
            {
                if (IsYml(e)) { res.Add(e); continue; }
                var cks = CatalogsOf(e);
                bool fresh = false;
                foreach (var ck in cks)
                    if (!seen.Contains(ck)) { fresh = true; break; }
                if (!fresh) continue;
                foreach (var ck in cks) seen.Add(ck);
                res.Add(e);
            }
            return res;
        }

        // Какие офферы остаются в одной выгрузке. Товары берутся по порядку «шире охват, затем стабильный перемешанный порядок по хэшу названия»,
        // поэтому при лимите состав стабилен от запуска к запуску. Лимит считается в офферах итогового файла:
        // «на файл» — товар берётся целиком, пока помещается; «на каталог» — оффер берётся, пока в его каталоге есть место.
        private HashSet<ulong> ComputeKeptOffers(Scope scope, List<(Group Group, decimal Price)> selected,
                                                JsonBuildOptions jo, HashSet<ulong>? excluded, out int products)
        {
            var kept = new HashSet<ulong>();
            var perCatalog = new Dictionary<int, int>();
            int limit = Math.Max(0, jo.LimitOffers);
            bool common = jo.Common;   // общий каталог: каждый товар (по названию) — один оффер, лимит — на весь каталог
            bool byCatalog = !common && limit > 0 && jo.LimitMode == LimitMode.PerCatalog;
            products = 0;
            noCatalogSkipped = 0;
            lastChosen = byCatalog ? new Dictionary<int, HashSet<ulong>>() : null;
            ymlPrice.Clear();
            ymlKept = 0;
            commonPrice.Clear();

            // Порядок: сначала акционные (если включено), затем шире охват, затем стабильный перемешанный порядок
            // (ассортимент не обрезается по алфавиту)
            IEnumerable<(Group Group, decimal Price)> ordered = selected;
            if (limit > 0 || jo.PreferPromo)
            {
                var o1 = jo.PreferPromo
                    ? selected.OrderByDescending(x => x.Group.OldPrice.HasValue ? 1 : 0)
                    : selected.OrderBy(_ => 0);
                ordered = o1.ThenByDescending(x => x.Group.Shops.Count + x.Group.Regions.Count)
                            .ThenBy(x => HashId(x.Group.Description));
            }

            foreach (var (g, groupPrice) in ordered)
            {
                var entries = new List<PriceEntry>(g.Offers.Count);
                bool ymlSeen = false;
                foreach (var e in g.Offers)
                {
                    if (e.Id == 0) continue;
                    if (excluded != null && excluded.Contains(e.Id)) continue;
                    if (scope.Region >= 0 && !InRegion(e, scope.Region)) continue;
                    if (scope.Shop >= 0 && !InShop(e, scope.Shop)) continue;
                    // Оффер, не указанный в исходных данных ни в одном каталоге (Cat = -1), в результат не берётся:
                    // иначе он занимал бы место в лимите как «каталог без номера» и попадал в offers без каталога.
                    // Исключения: YML и общий каталог (там каталог один на всех)
                    if (!common && e.Cat == -1 && !IsYml(e)) { noCatalogSkipped++; continue; }
                    // YML: на один товар (название) берётся один оффер, цена — по режиму «Цена»
                    if (IsYml(e)) { if (ymlSeen) continue; ymlSeen = true; }
                    entries.Add(e);
                }
                if (entries.Count == 0) continue;
                if (!common) entries = OnePerCatalog(entries);

                // Общий каталог: из всех офферов товара берётся один — с ценой, выбранной режимом «Цена»
                if (common)
                {
                    PriceEntry pick = entries.FirstOrDefault(e => e.Price == groupPrice);
                    if (pick.Id == 0) pick = entries[0];
                    commonPrice[pick.Id] = groupPrice;
                    entries = new List<PriceEntry> { pick };
                }

                // Без повторов товар попадает только в один каталог — тот, где пока меньше всего офферов
                bool slotsTaken = false;
                if (!common && !jo.AllowRepeats && (entries.Count > 1 || byCatalog))
                {
                    PriceEntry best = entries[0];
                    int bestCat = 0, bestLoad = int.MaxValue;
                    bool found = false;
                    foreach (var e in entries)
                        foreach (var ck in CatalogsOf(e))
                        {
                            perCatalog.TryGetValue(ck, out int load);
                            if (byCatalog && load >= limit) continue;   // в этом каталоге места нет
                            if (load < bestLoad) { best = e; bestCat = ck; bestLoad = load; found = true; }
                        }
                    if (!found) continue;
                    if (byCatalog)
                    {
                        perCatalog[bestCat] = bestLoad + 1;
                        if (!lastChosen!.TryGetValue(bestCat, out var one)) lastChosen[bestCat] = one = new HashSet<ulong>();
                        one.Add(best.Id);
                        slotsTaken = true;
                    }
                    entries = new List<PriceEntry> { best };
                }

                if (byCatalog && !slotsTaken)
                {
                    // «слот» — это пара (оффер, каталог): оффер, лежащий в двух каталогах, занимает место в каждом, где оно ещё есть
                    var take = new List<PriceEntry>();
                    foreach (var e in entries)
                    {
                        bool any = false;
                        foreach (var ck in CatalogsOf(e))
                        {
                            perCatalog.TryGetValue(ck, out int n);
                            if (n >= limit) continue;
                            perCatalog[ck] = n + 1;
                            if (!lastChosen!.TryGetValue(ck, out var set)) lastChosen[ck] = set = new HashSet<ulong>();
                            set.Add(e.Id);
                            any = true;
                        }
                        if (any) take.Add(e);
                    }
                    entries = take;
                    if (entries.Count == 0) continue;
                }
                else if (limit > 0 && kept.Count + entries.Count > limit)
                {
                    continue;   // товар целиком не помещается, пробуем следующий (может быть меньше)
                }

                foreach (var e in entries)
                {
                    kept.Add(e.Id);
                    if (IsYml(e)) { ymlPrice[e.Id] = groupPrice; ymlKept++; }
                }
                products++;
                if (limit > 0 && !byCatalog && kept.Count >= limit) break;
            }
            return kept;
        }

        private static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            return name.Length == 0 ? "result" : name.Length > 100 ? name[..100] : name;
        }

        private ImagePipeline? pipeline;

        private ImagePipeline GetPipeline()
        {
            if (pipeline != null) return pipeline;
            var s3 = opt.Json!.S3!;
            pipeline = new ImagePipeline(s3.Conn.CreateClient(), s3.Conn.Bucket, s3.Folder + "/images/",
                PublicBase(s3.Conn), opt.Recode, s3.Threads, log);
            return pipeline;
        }

        private static string PublicBase(S3Connection c)
        {
            if (!string.IsNullOrWhiteSpace(c.PublicBaseUrl)) return c.PublicBaseUrl.Trim().TrimEnd('/');
            string host = c.Endpoint.Trim().TrimEnd('/');
            if (!host.Contains("://")) host = "https://" + host;
            return host + "/" + c.Bucket;
        }

        private static bool IsHttpUrl(string u) =>
            u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // Картинка каждого оставленного оффера: хэш id -> адрес (пустые не включаются)
        private Dictionary<ulong, string> CollectImages(HashSet<ulong> kept)
        {
            var map = new Dictionary<ulong, string>();
            foreach (var (salt, path) in jsonFiles)
            {
                ScanArray(path, "offers", o =>
                {
                    string id = GetString(o, "id") ?? "";
                    if (id.Length == 0) return;
                    ulong h = HashId(salt + id);
                    if (!kept.Contains(h)) return;
                    string? img = GetString(o, "image");
                    if (string.IsNullOrWhiteSpace(img)) return;
                    string raw = img.Trim();
                    if (IsHttpUrl(raw)) map[h] = raw;   // относительные и прочие ссылки не считаются: оффер без рабочей картинки
                }, ct);
            }
            foreach (var (salt, path) in ymlFiles)
            {
                ReadYmlFile(path, o =>
                {
                    ulong h = HashId(salt + YmlId(o));
                    if (kept.Contains(h) && !string.IsNullOrWhiteSpace(o.Image) && IsHttpUrl(o.Image!.Trim())) map[h] = o.Image!.Trim();
                }, ct, null);
            }
            return map;
        }

        // Сколько офферов приходится на каталог: среднее, минимум и максимум. kept == null — весь отобранный пул
        private (int Catalogs, int Avg, int Min, int Max) PerCatalogStats(
            List<(Group Group, decimal Price)> selected, HashSet<ulong>? kept)
        {
            var counts = new Dictionary<int, int>();
            foreach (var (g, _) in selected)
                foreach (var e in g.Offers)
                {
                    if (kept != null && !kept.Contains(e.Id)) continue;
                    foreach (var ck in CatalogsOf(e))
                    {
                        if (ck == -1) continue;   // оффер без каталога — не каталог
                        counts[ck] = counts.TryGetValue(ck, out var c) ? c + 1 : 1;
                    }
                }
            if (counts.Count == 0) return (0, 0, 0, 0);
            if (kept != null && lastChosen != null)
            {
                // лимит «на каждый каталог»: считаем офферы, выбранные именно для каталога
                var chosen = counts.Keys.ToDictionary(k => k, k =>
                    lastChosen.TryGetValue(k, out var set) ? set.Count(h => kept.Contains(h)) : 0);
                return (chosen.Count, (int)Math.Round(chosen.Values.Average()), chosen.Values.Min(), chosen.Values.Max());
            }
            return (counts.Count, (int)Math.Round(counts.Values.Average()), counts.Values.Min(), counts.Values.Max());
        }

        private async Task FinishJsonScopeAsync(Scope scope, List<(Group Group, decimal Price)> selected, Action<string> L)
        {
            var jo = opt.Json!;
            var s3 = jo.S3;
            var stats = jo.Stats;

            if (s3?.Images == true && !opt.Recode.Enabled)
                throw new InvalidOperationException("Для картинок на S3 включите сжатие картинок в настройках сети.");

            int promoGroups = selected.Count(x => x.Group.OldPrice.HasValue);
            L($"Акционных товаров (есть price_old): {promoGroups} из {selected.Count}" +
              (jo.PreferPromo ? (promoGroups > 0 ? "; акционные берутся первыми, остальные добираются до лимита" : "; скидок нет, берутся обычные товары") : ""));
            if (jo.Common) L("Общий каталог для всей России: один оффер на товар, цена — по режиму «Цена».");

            string title = scope.Title.Length > 0 ? SafeFileName(jo.FileName + "_" + scope.Title) : SafeFileName(jo.FileName);
            string dst = Path.Combine(outDir, title + ".json");

            // Отбор офферов по кругу. Оффер без рабочей картинки (нет ключа image или ссылка не http/https, например
            // относительная «/upload/…») исключается; с картинками на S3 так же исключаются картинки меньше 400 px.
            // При лимите на место исключённых берётся следующий товар (до 5 кругов).
            var excluded = new HashSet<ulong>();
            var imageMap = !previewOnly && s3?.Images == true ? new Dictionary<string, string>() : null;
            HashSet<ulong> kept;
            int products;
            int noImageTotal = 0, smallTotal = 0;
            bool cheapOnly = previewOnly;   // предрасчёт: без скачивания картинок
            for (int round = 1; ; round++)
            {
                kept = ComputeKeptOffers(scope, selected, jo, excluded, out products);
                if (kept.Count == 0)
                    throw new InvalidOperationException("После отбора и лимита не осталось офферов" +
                        (noImageTotal + smallTotal > 0 ? $" (исключено без рабочей картинки: {noImageTotal}, с маленькой: {smallTotal})." : ".") +
                        (noCatalogSkipped > 0 ? $" Офферов без каталога в исходных данных: {noCatalogSkipped} — такие офферы не берутся." : ""));
                if (round == 1 && noCatalogSkipped > 0)
                    L($"Офферов, не указанных ни в одном каталоге исходных файлов: {noCatalogSkipped} — в результат не берутся (проверьте исходные данные).");
                if (round == 1 && !previewOnly)
                    L(jo.LimitOffers > 0
                        ? $"Лимит офферов: {jo.LimitOffers} ({(jo.LimitMode == LimitMode.PerCatalog ? "на каждый каталог" : "на весь результат")}); оставлено: {kept.Count}"
                        : $"Оставлено офферов: {kept.Count}");

                var imgs = CollectImages(kept);   // только офферы с рабочей (http/https) ссылкой на картинку
                var noImg = kept.Where(h => !imgs.ContainsKey(h)).ToList();
                if (noImg.Count > 0)
                {
                    noImageTotal += noImg.Count;
                    L($"Исключено офферов без рабочей ссылки на картинку: {noImg.Count}");
                    if (jo.LimitOffers == 0 || round >= 5)
                    {
                        kept.ExceptWith(noImg);
                        foreach (var h in noImg) imgs.Remove(h);
                        if (kept.Count == 0) throw new InvalidOperationException("После исключения офферов без картинки не осталось офферов.");
                    }
                    else
                    {
                        foreach (var h in noImg) excluded.Add(h);   // пересчёт отбора: на их место придут другие товары
                        continue;
                    }
                }

                if (cheapOnly)
                {
                    if (s3?.Images == true)
                    {
                        var urlsP = imgs.Values.ToHashSet(StringComparer.Ordinal);
                        int pending = await GetPipeline().CountPendingAsync(urlsP, ct);
                        L($"Картинки: уникальных {urlsP.Count}, уже на S3 {urlsP.Count - pending}, нужно сжать и загрузить {pending}");
                    }
                    break;
                }
                if (imageMap == null) break;

                var urls = imgs.Values.ToHashSet(StringComparer.Ordinal);
                if (round == 1) L($"Картинки в результате: {urls.Count}");
                int basePct = 5;
                var (map, istats, small) = await GetPipeline().ProcessAsync(urls, p => Pct(basePct + p * 60 / 100), ct);   // картинки: 5–65% шкалы выгрузки, дальше запись JSON
                foreach (var kv in map) imageMap[kv.Key] = kv.Value;
                if (stats != null)
                {
                    stats.ImagesTotal += istats.Total;
                    stats.ImagesNew += istats.Uploaded;
                    stats.ImagesReused += istats.Reused;
                    stats.ImagesFailed += istats.Failed;
                }

                if (!s3!.ExcludeSmall || small.Count == 0) break;
                var bad = imgs.Where(kv => small.Contains(kv.Value)).Select(kv => kv.Key).ToList();
                if (bad.Count == 0) break;
                smallTotal += bad.Count;
                L($"Исключено офферов с картинкой меньше {ImagePipeline.MinSide} px: {bad.Count}");

                if (jo.LimitOffers == 0 || round >= 5)
                {
                    kept.ExceptWith(bad);
                    if (kept.Count == 0) throw new InvalidOperationException("После исключения маленьких картинок не осталось офферов.");
                    break;
                }
                foreach (var b in bad) excluded.Add(b);   // пересчёт отбора: на место исключённых придут другие товары
            }
            if (stats != null) stats.ExcludedSmall += smallTotal;

            if (previewOnly)
            {
                int catalogs = CountCatalogs(kept);
                long est = SourceOffers > 0
                    ? (long)((double)SourceBytes * Math.Min(1.0, (double)kept.Count / SourceOffers))
                    : 0;
                var pool = PerCatalogStats(selected, null);
                var res = PerCatalogStats(selected, kept);
                Rows.Add(new JsonScopeRow(scope.Title.Length > 0 ? scope.Title : jo.FileName, products, kept.Count, catalogs, est,
                    kept.Count < 10 ? "меньше 10 офферов: по правилам сервиса в сети должно быть не меньше 10 акционных товаров" : null,
                    res.Avg));
                if (pool.Catalogs > 0 && !jo.Common)
                    L($"На один каталог в исходных данных приходится в среднем {pool.Avg} офферов (от {pool.Min} до {pool.Max}), каталогов: {pool.Catalogs}.");
                if (res.Catalogs > 0 && !jo.Common)
                    L($"В результате на один каталог в среднем {res.Avg} офферов (от {res.Min} до {res.Max}).");
                L($"Предрасчёт: товаров {products}, офферов {kept.Count}, каталогов {catalogs}" +
                  (noImageTotal > 0 ? $" (без рабочей картинки исключено: {noImageTotal})" : ""));
                if (jo.LimitOffers > 0 && jo.LimitMode == LimitMode.PerCatalog && !jo.Common)
                    L($"Лимит {jo.LimitOffers} применяется к каждому из {catalogs} каталогов: в файле может быть до {(long)jo.LimitOffers * catalogs} офферов.");
                return;
            }

            WriteJson(dst, kept, imageMap, out int catalogsWritten, out int offersWritten);
            long size = new FileInfo(dst).Length;
            L($"{(s3?.UploadJson == true ? "Файл собран" : "Записано")}: {dst} — каталогов {catalogsWritten}, офферов {offersWritten}, {size / 1024.0 / 1024.0:F1} МБ");
            if (stats != null)
            {
                stats.Files++;
                stats.Offers += offersWritten;
                stats.Catalogs += catalogsWritten;
                stats.Bytes += size;
            }

            if (s3?.UploadJson == true)
            {
                string key = s3.Folder + "/" + title + ".json";
                L("Загружаю JSON на S3...");
                await s3.Conn.CreateClient().PutFileAsync(s3.Conn.Bucket, key, dst, "application/json", ct);
                string link = $"{PublicBase(s3.Conn)}/{SigV4.EncodePath(key)}";
                L($"Загружено: {link}");
                if (stats != null) { stats.Uploaded = true; stats.Link = link; }
                if (!s3.KeepLocal)
                {
                    try { File.Delete(dst); L("Локальная копия удалена (оставлять её можно в Настройках)."); } catch { }
                }
            }
        }

        // Офферы, выбранные именно для каталога на данной позиции файла (null — без ограничения: режим не «на каталог»)
        private HashSet<ulong>? AllowedFor(string salt, int pos)
        {
            if (lastChosen == null) return null;
            if (!catPos.TryGetValue(salt, out var list) || pos < 0 || pos >= list.Count) return null;
            int ci = list[pos];
            if (ci < 0) return NoOffers;   // каталог-повтор из другого файла
            return lastChosen.TryGetValue(ci, out var set) ? set : NoOffers;
        }

        private int CountCatalogs(HashSet<ulong> kept)
        {
            if (opt.Json?.Common == true) return kept.Count > 0 ? 1 : 0;   // общий каталог — один
            int n = 0;
            foreach (var (salt, path) in jsonFiles)
            {
                int pos = 0;
                ScanArray(path, "catalogs", c => { if (WriteCatalog(null, c, kept, salt, null, null, AllowedFor(salt, pos++))) n++; }, ct);
            }
            if (ymlKept > 0) n++;   // каталог из YML-офферов
            return n;
        }

        // Пишет каталог с урезанным списком offers; каталог без оставшихся офферов не пишется (offers в каталоге обязателен).
        // w == null — только проверка, нужен ли каталог. mapOffer / mapCatalog переименовывают id при конфликтах между файлами.
        private static bool WriteCatalog(Utf8JsonWriter? w, JsonElement c, HashSet<ulong> kept, string salt,
                                         Func<string, string>? mapOffer, Func<string, string>? mapCatalog,
                                         HashSet<ulong>? allowed, int catalogDays = 0, DateOnly today = default)
        {
            var ids = new List<string>();
            if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("offers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.String) continue;
                    string? id = el.GetString();
                    if (string.IsNullOrEmpty(id)) continue;
                    ulong hid = HashId(salt + id);
                    if (kept.Contains(hid) && (allowed == null || allowed.Contains(hid))) ids.Add(id);
                }
            }
            if (ids.Count == 0) return false;
            if (w == null) return true;

            // Срок действия: date_end = date_start + N дней (формат и время берутся из date_start)
            string? newEnd = null;
            if (catalogDays > 0)
            {
                string? ds = c.TryGetProperty("date_start", out var dsEl) && dsEl.ValueKind == JsonValueKind.String ? dsEl.GetString() : null;
                DateOnly start = today;
                string suffix = "";
                if (!string.IsNullOrWhiteSpace(ds) && ds.Length >= 10 &&
                    DateOnly.TryParseExact(ds[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                           System.Globalization.DateTimeStyles.None, out var parsed))
                {
                    start = parsed;
                    suffix = ds[10..];
                }
                newEnd = start.AddDays(Math.Clamp(catalogDays, 1, 60)).ToString("yyyy-MM-dd") + suffix;
            }

            w.WriteStartObject();
            bool endWritten = false;
            foreach (var p in c.EnumerateObject())
            {
                if (newEnd != null && p.NameEquals("date_end")) continue;   // заменяется вычисленным
                if (newEnd != null && p.NameEquals("date_start"))
                {
                    p.WriteTo(w);
                    w.WriteString("date_end", newEnd);   // сразу после date_start, а не в конце каталога после длинного списка offers
                    endWritten = true;
                    continue;
                }
                if (p.NameEquals("offers"))
                {
                    w.WriteStartArray("offers");
                    foreach (var id in ids) w.WriteStringValue(mapOffer != null ? mapOffer(id) : id);
                    w.WriteEndArray();
                }
                else if (mapCatalog != null && p.NameEquals("id") && p.Value.ValueKind == JsonValueKind.String)
                {
                    w.WriteString("id", mapCatalog(p.Value.GetString() ?? ""));
                }
                else p.WriteTo(w);
            }
            if (newEnd != null && !endWritten) w.WriteString("date_end", newEnd);
            w.WriteEndObject();
            return true;
        }

        // Оффер копируется как есть; меняется только image (если картинка уже на S3) и id (при конфликте между файлами)
        private static void WriteOffer(Utf8JsonWriter w, JsonElement o, Dictionary<string, string>? imageMap,
                                       Func<string, string>? mapId, decimal? newPrice = null)
        {
            string? newUrl = null;
            if (imageMap != null && o.ValueKind == JsonValueKind.Object &&
                o.TryGetProperty("image", out var im) && im.ValueKind == JsonValueKind.String)
                imageMap.TryGetValue((im.GetString() ?? "").Trim(), out newUrl);

            string? id = null, newId = null;
            if (mapId != null && o.ValueKind == JsonValueKind.Object &&
                o.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                id = idEl.GetString();
                newId = id == null ? null : mapId(id);
            }
            bool idChanged = id != null && newId != id;

            // общий каталог: цена товара — по режиму «Цена» (если отличается от цены в самом оффере)
            bool priceChanged = false;
            if (newPrice.HasValue)
            {
                var cur = GetDecimal(o, "price_new");
                priceChanged = !(cur.HasValue && cur.Value == newPrice.Value);
            }

            if (newUrl == null && !idChanged && !priceChanged)
            {
                o.WriteTo(w);
                return;
            }

            w.WriteStartObject();
            foreach (var p in o.EnumerateObject())
            {
                if (newUrl != null && p.NameEquals("image")) w.WriteString("image", newUrl);
                else if (idChanged && p.NameEquals("id")) w.WriteString("id", newId!);
                else if (priceChanged && p.NameEquals("price_new")) w.WriteNumber("price_new", newPrice!.Value);
                else p.WriteTo(w);
            }
            w.WriteEndObject();
        }

        // Второй проход по исходным файлам: офферы копируются как есть (все поля), без пересборки.
        // Несколько файлов склеиваются в один результат; одинаковые id из разных файлов получают префикс «номер файла_».
        private void WriteJson(string dst, HashSet<ulong> kept, Dictionary<string, string>? imageMap,
                               out int catalogs, out int offers)
        {
            int cat = 0, off = 0;
            int files = jsonFiles.Count;

            // конфликты id офферов между файлами (только если файлов несколько)
            var offerMaps = new Func<string, string>?[files];
            if (files > 1)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int fi = 0; fi < files; fi++)
                {
                    var (salt, path) = jsonFiles[fi];
                    var mine = new HashSet<string>(StringComparer.Ordinal);
                    ScanArray(path, "offers", o =>
                    {
                        string id = GetString(o, "id") ?? "";
                        if (id.Length > 0 && kept.Contains(HashId(salt + id))) mine.Add(id);
                    }, ct);
                    var clash = new HashSet<string>(mine.Where(seen.Contains), StringComparer.Ordinal);
                    if (clash.Count > 0)
                    {
                        int prefix = fi + 1;
                        offerMaps[fi] = id => clash.Contains(id) ? $"{prefix}_{id}" : id;
                        log.Report($"Файл {fi + 1}: одинаковых id офферов с другими файлами — {clash.Count}; им добавлен префикс «{prefix}_».");
                    }
                    seen.UnionWith(mine);
                }
            }

            string tmp = dst + ".tmp";
            int catalogDays = opt.Json?.CatalogDays ?? 0;
            bool common = opt.Json?.Common == true;
            DateOnly todayDate = opt.Json?.Yml?.Today ?? DateOnly.FromDateTime(DateTime.Now);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions
                       { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    w.WriteStartObject();

                    w.WriteStartArray("catalogs");
                    var usedCat = new HashSet<string>(StringComparer.Ordinal);
                    for (int fi = 0; fi < (common ? 0 : files); fi++)   // общий каталог: исходные каталоги не пишутся
                    {
                        var (salt, path) = jsonFiles[fi];
                        int prefix = fi + 1;
                        var thisFile = new List<string>();
                        Func<string, string>? mapCat = files > 1
                            ? id => { string m = usedCat.Contains(id) ? $"{prefix}_{id}" : id; thisFile.Add(m); return m; }
                            : null;
                        var mapOff = offerMaps[fi];
                        int pos = 0;
                        ScanArray(path, "catalogs", c => { if (WriteCatalog(w, c, kept, salt, mapOff, mapCat, AllowedFor(salt, pos++), catalogDays, todayDate)) cat++; }, ct);
                        usedCat.UnionWith(thisFile);
                    }
                    // Id оставленных офферов из JSON (нужны для общего каталога)
                    var jsonIds = new List<string>();
                    if (common)
                    {
                        for (int fi = 0; fi < files; fi++)
                        {
                            var (salt, path) = jsonFiles[fi];
                            var mapOffC = offerMaps[fi];
                            ScanArray(path, "offers", o =>
                            {
                                string id = GetString(o, "id") ?? "";
                                if (id.Length == 0 || !kept.Contains(HashId(salt + id))) return;
                                jsonIds.Add(mapOffC != null ? mapOffC(id) : id);
                            }, ct);
                        }
                    }

                    // Каталог для всей России: из YML-офферов, а в режиме «общий каталог» — и из офферов JSON.
                    // Id YML-офферов получают префикс, если есть и JSON-офферы
                    var ymlIds = new Dictionary<ulong, string>();
                    var ymlOrder = new List<string>();
                    if (ymlFiles.Count > 0 && ymlKept > 0)
                    {
                        var seenIds = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var (salt, path) in ymlFiles)
                        {
                            ReadYmlFile(path, o =>
                            {
                                ulong h = HashId(salt + YmlId(o));
                                if (!kept.Contains(h) || ymlIds.ContainsKey(h)) return;
                                string id = YmlId(o);
                                if (files > 0) id = "yml_" + id;
                                if (!seenIds.Add(id)) id = id + "_" + (seenIds.Count + 1);
                                seenIds.Add(id);
                                ymlIds[h] = id;
                                ymlOrder.Add(id);
                            }, ct, null);
                        }
                    }
                    var allIds = common ? jsonIds.Concat(ymlOrder).ToList() : ymlOrder;
                    {
                        if (allIds.Count > 0)
                        {
                            var y = opt.Json?.Yml ?? new JsonYmlOptions(JsonYmlOptions.DefaultConditions, 7, DateOnly.FromDateTime(DateTime.Now));
                            string conditions = string.IsNullOrWhiteSpace(y.Conditions) ? JsonYmlOptions.DefaultConditions : y.Conditions;
                            var start = y.Today;
                            var end = start.AddDays(Math.Clamp(y.Days, 1, 60));
                            w.WriteStartObject();
                            string target = string.IsNullOrWhiteSpace(y.Target) ? "Россия" : y.Target.Trim();
                            string catId = target == "Россия" ? (common ? "russia" : "yml_russia") : "yml_" + HashId(target).ToString("x16")[..8];
                            w.WriteString("id", catId);
                            w.WriteString("conditions", conditions);
                            w.WriteString("date_start", start.ToString("yyyy-MM-dd"));
                            w.WriteString("date_end", end.ToString("yyyy-MM-dd"));
                            w.WriteBoolean("is_main", true);
                            w.WriteStartArray("target_regions");
                            w.WriteStringValue(target);
                            w.WriteEndArray();
                            w.WriteStartArray("offers");
                            foreach (var id in allIds) w.WriteStringValue(id);
                            w.WriteEndArray();
                            w.WriteEndObject();
                            cat++;
                        }
                    }
                    w.WriteEndArray();

                    w.WriteStartArray("offers");
                    for (int fi = 0; fi < files; fi++)
                    {
                        var (salt, path) = jsonFiles[fi];
                        var mapOff = offerMaps[fi];
                        int fileIndex = fi;
                        double next = 0;
                        ScanArray(path, "offers", o =>
                        {
                            string id = GetString(o, "id") ?? "";
                            if (id.Length == 0 || !kept.Contains(HashId(salt + id))) return;
                            decimal? np = common && commonPrice.TryGetValue(HashId(salt + id), out var cp) ? cp : null;
                            WriteOffer(w, o, imageMap, mapOff, np);
                            off++;
                        }, ct, p =>
                        {
                            if (p >= next) { Pct(65 + (fileIndex * 100.0 + p) / files * 34 / 100); next = p + 0.5; }   // запись: 65–99%
                        });
                    }
                    // Офферы из YML: поля собираются из YML (id, название, картинка, цена), одно название — один оффер
                    var ymlWritten = new HashSet<ulong>();
                    foreach (var (salt, path) in ymlFiles)
                    {
                        ReadYmlFile(path, o =>
                        {
                            ulong h = HashId(salt + YmlId(o));
                            if (!ymlIds.TryGetValue(h, out var id) || !ymlWritten.Add(h)) return;
                            decimal price = ymlPrice.TryGetValue(h, out var chosen) && chosen > 0 ? chosen : o.Price;
                            string? image = o.Image?.Trim();
                            if (imageMap != null && image != null && imageMap.TryGetValue(image, out var newImage)) image = newImage;

                            w.WriteStartObject();
                            w.WriteString("id", id);
                            w.WriteString("description", o.Name.Length > 0 ? o.Name : o.Id);
                            if (!string.IsNullOrEmpty(image)) w.WriteString("image", image);
                            w.WriteNumber("price_new", price);
                            if (o.OldPrice.HasValue && o.OldPrice.Value > price) w.WriteNumber("price_old", o.OldPrice.Value);
                            w.WriteEndObject();
                            off++;
                        }, ct, null);
                    }
                    w.WriteEndArray();

                    w.WriteNumber("version", 2);
                    w.WriteEndObject();
                }
                File.Move(tmp, dst, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
            catalogs = cat;
            offers = off;
        }

        private (List<(Group Group, decimal Price)> Items, SelStats Stats) SelectProducts(
            Scope scope, bool useShops, int needShops)
        {
            if (scope.Preselected != null) return (scope.Preselected, new SelStats());

            decimal limit = opt.MaxSpreadPercent / 100m;
            var st = new SelStats();
            var list = new List<(Group Group, decimal Price)>();

            foreach (var g in scope.Candidates)
            {
                var prices = ScopePrices(g, scope);
                int geos = ScopeGeoCount(g, scope);

                // Картина «до отбора»: сколько уникальных товаров и офферов, в скольких гео встречается товар
                st.Candidates++;
                if (g.OldPrice.HasValue) st.Promo++;
                st.Offers += prices.Count;
                if (geos <= 1) st.OneGeo++;
                else
                {
                    st.MultiGeo++;
                    if (geos <= 5) st.Geo2to5++;
                    else if (geos <= 20) st.Geo6to20++;
                    else if (geos <= 100) st.Geo21to100++;
                    else st.GeoOver100++;
                }

                if (useShops && CountIn(g.Shops, scope.ShopSet) < needShops) { st.ShopDropped++; continue; }

                if (prices.Count == 0) { st.NoPrice++; continue; }

                decimal min = prices.Min();
                decimal max = prices.Max();
                decimal spread = (max - min) / min;          // min > 0 гарантировано при чтении

                if (spread > limit)
                {
                    st.Dropped++;
                    if (geos > 1)
                    {
                        st.MultiDropped++;
                        st.DroppedSpreadMax = Math.Max(st.DroppedSpreadMax, spread);
                    }
                    continue;
                }

                if (prices.Count == 1) st.Single++;
                else if (max == min) st.Equal++;
                else st.Similar++;

                if (geos > 1)
                {
                    if (max == min) { st.MultiEqual++; st.MultiEqualOffers += prices.Count; }
                    else
                    {
                        st.MultiSimilar++;
                        st.MultiSimilarOffers += prices.Count;
                        st.SimilarSpreadSum += spread;
                        st.SimilarSpreadMax = Math.Max(st.SimilarSpreadMax, spread);
                    }
                }

                list.Add((g, PickPrice(prices, opt.PriceMode)));
            }
            return (list, st);
        }

        // В скольких гео (каталогах) товар встречается в этой выгрузке (считаются только офферы с корректной ценой)
        private readonly HashSet<int> geoScratch = new();   // один набор на все товары, чтобы не создавать его каждый раз

        private int ScopeGeoCount(Group g, Scope scope)
        {
            var geos = geoScratch;
            geos.Clear();
            foreach (var e in g.Offers)
            {
                if (e.Price <= 0) continue;
                if (scope.Region >= 0 && !InRegion(e, scope.Region)) continue;
                if (scope.Shop >= 0 && !InShop(e, scope.Shop)) continue;
                foreach (var ck in CatalogsOf(e)) geos.Add(ck);
            }
            return geos.Count;
        }

        // Для выгрузки по региону цены берём только из офферов этого региона,
        // чтобы цены других регионов не влияли на отбор и итоговую цену.
        private List<decimal> ScopePrices(Group g, Scope scope)
        {
            var result = new List<decimal>(g.Offers.Count);
            foreach (var e in g.Offers)
            {
                if (e.Price <= 0) continue;                      // оффер без корректной цены
                if (scope.Region >= 0 && !InRegion(e, scope.Region)) continue;
                if (scope.Shop >= 0 && !InShop(e, scope.Shop)) continue;
                result.Add(e.Price);
            }
            return result;
        }

        private GeoModel? currentGeo;   // заполняется при чтении, нужен для проверки региона оффера
        // Регион = субъект РФ: включено в настройках и справочник найден
        private bool BySubject => opt.RegionBySubject && RuGeo.Loaded;
        private int scopeCount;         // сколько выгрузок (Excel) получится: таблицу охвата печатаем, только если их мало

        private bool InRegion(PriceEntry e, int region) =>
            currentGeo != null && CodeHas(currentGeo, currentGeo.CatRegions, e.Cat, region);

        private bool InShop(PriceEntry e, int shop) =>
            currentGeo != null && CodeHas(currentGeo, currentGeo.CatShops, e.Cat, shop);

        private static bool CodeHas(GeoModel geo, int[][] sets, int code, int value)
        {
            if (code >= 0) return Array.IndexOf(sets[code], value) >= 0;
            if (code <= -2)
                foreach (var ci in geo.Multi[-code - 2])
                    if (Array.IndexOf(sets[ci], value) >= 0) return true;
            return false;
        }

        // Добавляет магазины и регионы каталога (или нескольких каталогов) в товар
        private static void AddGeo(Group g, GeoModel geo, int code)
        {
            if (code >= 0)
            {
                g.Shops.UnionWith(geo.CatShops[code]);
                g.Regions.UnionWith(geo.CatRegions[code]);
            }
            else if (code <= -2)
            {
                foreach (var ci in geo.Multi[-code - 2])
                {
                    g.Shops.UnionWith(geo.CatShops[ci]);
                    g.Regions.UnionWith(geo.CatRegions[ci]);
                }
            }
        }
    }
}
