using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FeedForge;

// Чтение YML (XML).
public static partial class Converter
{
    private sealed partial class Runner
    {
        // ---------- Чтение YML (XML) ----------
        // Формат Яндекс.Маркета / Едадила: <offer id="..."><name/><price/><oldprice/><picture/>...</offer>.
        // Читается потоком, по одному офферу.
        private readonly record struct YmlOffer(string Id, string Name, string? Image, decimal Price, decimal? OldPrice);

        private sealed class YmlStats
        {
            public int Count, NotPromo, Invalid;
        }

        // Читает один YML-файл и вызывает onOffer для каждого подходящего оффера
        private YmlStats ReadYmlFile(string path, Action<YmlOffer> onOffer, CancellationToken ct, Action<double>? onPercent)
        {
            var st = new YmlStats();

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                                          FileOptions.SequentialScan);
            long length = Math.Max(1, fs.Length);
            using var reader = XmlReader.Create(fs, CreateXmlSettings());

            string? shopUrl = null;
            double lastPercent = -1;

            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    // Адрес магазина нужен, чтобы достроить относительные ссылки на картинки
                    if (reader.Depth == 2 && reader.Name == "url")
                    {
                        shopUrl = reader.ReadElementContentAsString().Trim();
                        continue;
                    }

                    if (reader.Name == "offer")
                    {
                        var o = (XElement)XNode.ReadFrom(reader);   // после чтения reader уже стоит на следующем узле
                        st.Count++;

                        string name = (o.Element("name")?.Value ?? "").Trim();
                        string id = (o.Attribute("id")?.Value ?? "").Trim();

                        var price = ParseYmlPrice(o.Element("price")?.Value);
                        var oldPrice = ParseYmlPrice(o.Element("oldprice")?.Value);
                        bool promo = oldPrice.HasValue && oldPrice.Value > 0;

                        string? picture = FirstPicture(o, shopUrl);
                        // Как в FeedChecker: нужны id, название, цена больше нуля и картинка
                        if (id.Length == 0 || name.Length == 0 || !(price.HasValue && price.Value > 0) || string.IsNullOrWhiteSpace(picture))
                            st.Invalid++;
                        else if (opt.PromoOnly && !promo) st.NotPromo++;       // режим «только акции»: нужен oldprice
                        else
                            onOffer(new YmlOffer(id, name, picture, price.Value, promo ? oldPrice : null));

                        if ((st.Count & 255) == 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (onPercent != null)
                            {
                                double p = Math.Floor(fs.Position * 1000.0 / length) / 10.0;   // с точностью до 0,1%
                                if (p != lastPercent) { lastPercent = p; onPercent(p); }
                            }
                        }
                        continue;
                    }
                }
                reader.Read();
            }
            return st;
        }

        // Добавляет оффер в группу товара (создаёт группу при необходимости); cat — код каталога/файла (-1 — нет)
        private static void AddYmlToGroups(List<Group> list, Dictionary<string, Group> index, string key, YmlOffer o, int cat, ulong id)
        {
            if (!index.TryGetValue(key, out var grp))
            {
                grp = new Group();
                index[key] = grp;
                list.Add(grp);
            }

            if (grp.Description.Length == 0) grp.Description = o.Name;
            if (string.IsNullOrWhiteSpace(grp.ImageUrl)) grp.ImageUrl = o.Image;
            if (o.OldPrice.HasValue) grp.OldPrice ??= o.OldPrice;
            grp.Offers.Add(new PriceEntry(o.Price, cat, id, o.OldPrice.HasValue && o.OldPrice.Value > o.Price));
        }

        // Один YML-файл: магазинов и регионов в нём нет — гео задаётся в программе.
        private (List<Group> Groups, int OffersCount, GeoModel Geo) ReadGroupsXml(string path, CancellationToken ct)
        {
            var list = new List<Group>();
            var index = new Dictionary<string, Group>();

            var geo = new GeoModel();
            currentGeo = geo;
            if (opt.RegionMode != RegionMode.Ignore || opt.ShopFilter)
                log.Report("В YML нет данных о магазинах и регионах — разбивка по гео и фильтр по магазинам не применяются.");

            int nextLog = 10;
            var st = ReadYmlFile(path,
                o => AddYmlToGroups(list, index, !string.IsNullOrEmpty(o.Name) ? o.Name : (o.Id.Length > 0 ? o.Id : Guid.NewGuid().ToString("N")),
                                    o, -1, HashId(YmlId(o))),
                ct,
                p =>
                {
                    if (p >= nextLog)
                    {
                        log.Report($"Чтение товаров: {(int)p}%");
                        nextLog = (int)p / 10 * 10 + 10;
                    }
                    percent.Report(Math.Min(39.9, p * 0.4));   // чтение — первые 40% общей шкалы
                });
            if (st.Invalid > 0) CountInvalid("YML", st.Invalid);

            if (st.Count == 0) throw new Exception("В файле нет ни одного <offer>. Это точно YML-фид?");
            if (opt.PromoOnly)
                log.Report($"Только акционные позиции: пропущено без oldprice — {st.NotPromo} из {st.Count}");

            foreach (var g in list) g.Offers.TrimExcess();
            return (list, st.Count, geo);
        }

        // Несколько файлов в один результат (YML и/или JSON).
        // YML: каждый файл — отдельный магазин (как каталог в JSON).
        // JSON: магазины берутся из каталогов (target_shops) внутри файла; если файл вообще без гео — он один магазин.
        // Один и тот же магазин в разных файлах учитывается один раз, один и тот же товар — тоже один раз.
        // Дальше работают обычные механизмы: охват магазинов, разброс цен, один общий Excel и один набор картинок.
        private async Task<(List<Group> Groups, int OffersCount, GeoModel Geo)> ReadGroupsFeedsAsync()
        {
            var feeds = opt.Feeds!;
            WarnNoGeoData();
            var s = new ReadState();
            var list = s.List;
            var index = s.Index;
            var geo = s.Geo;
            currentGeo = geo;
            var shopIndex = s.ShopIndex;
            var regionIndex = s.RegionIndex;
            var catShops = s.CatShops;
            var catRegions = s.CatRegions;
            var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failedFeeds = new List<string>();

            string tmpFeed = Path.Combine(outDir, "_feed.tmp");
            tempJson = tmpFeed;                      // Cleanup удалит временный файл при любом исходе

            int total = feeds.Count, okFeeds = 0, totalOffers = 0, totalNotPromo = 0, totalDupShops = 0, totalSkipped = 0;
            log.Report($"Режим объединения: файлов в списке: {total}");

            PreCheckJson(feeds);

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var feed = feeds[i];
                percent.Report(Math.Min(39.9, i * 40.0 / total));

                // Название должно быть уникальным, иначе два файла склеятся в один магазин
                string label = string.IsNullOrWhiteSpace(feed.Label) ? $"feed_{i + 1}" : feed.Label.Trim();
                string unique = label;
                int k = 2;
                while (!usedLabels.Add(unique)) unique = $"{label} ({k++})";

                // Товары файла собираем отдельно и добавляем в общий набор только если файл прочитан целиком
                var buffer = new List<(string Key, YmlOffer Offer)>();
                YmlStats? st = null;
                JsonFileStats? js = null;
                string? goodPath = null, goodYmlPath = null;
                try
                {
                    string path = feed.Source.Trim();
                    if (IsUrl(path) && opt.Json != null)
                        throw new InvalidOperationException("для сборки JSON нужен локальный файл");
                    if (IsUrl(path))
                    {
                        for (int attempt = 1; ; attempt++)
                        {
                            try
                            {
                                await DownloadJsonAsync(path, tmpFeed, log, ct, quiet: true);
                                break;
                            }
                            catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested)
                            {
                                await Task.Delay(1000 * attempt, ct);
                            }
                        }
                        path = tmpFeed;
                    }
                    else if (!File.Exists(path))
                    {
                        throw new FileNotFoundException("файл не найден");
                    }

                    if (DetectFormat(path) == SourceFormat.Json)
                    {
                        idSalt = $"{i}|";
                        js = ReadJsonFile(s, path, unique, feed, unique, atomic: true,
                                          p => percent.Report(Math.Min(39.9, (i + p / 100.0) * 40.0 / total)));
                        goodPath = path;
                    }
                    else
                    {
                        goodYmlPath = path;
                        st = ReadYmlFile(path, o =>
                        {
                            // Товар определяется только по названию (id у одинаковых товаров бывают разными)
                            string key = !string.IsNullOrEmpty(o.Name) ? o.Name : (o.Id.Length > 0 ? o.Id : Guid.NewGuid().ToString("N"));
                            buffer.Add((key, o));
                        }, ct, p => percent.Report(Math.Min(39.9, (i + p / 100.0) * 40.0 / total)));

                        if (st.Count == 0) throw new InvalidDataException("в файле нет офферов");
                        if (st.Invalid > 0) CountInvalid("YML", st.Invalid);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    log.Report($"[{i + 1}/{total}] {unique}: ПРОПУЩЕН — {ex.Message}");
                    failedFeeds.Add(feed.Region == null ? $"{feed.Source};{feed.Label}" : $"{feed.Source};{feed.Label};{feed.Region}");
                    usedLabels.Remove(unique);
                    continue;
                }
                finally
                {
                    try { if (File.Exists(tmpFeed)) File.Delete(tmpFeed); } catch { }
                }

                // ---------- JSON: файл уже добавлен в общий набор ----------
                if (js != null)
                {
                    okFeeds++;
                    jsonFiles.Add(($"{i}|", goodPath!));
                    totalOffers += js.Offers;
                    totalNotPromo += js.NotPromo;
                    totalDupShops += js.DuplicateCatalogs;
                    totalSkipped += js.SkippedDuplicate;

                    string what = js.WholeFileIsShop
                        ? "в файле нет данных о магазинах — весь файл считается одним магазином"
                        : js.Catalogs == 0 && js.DuplicateCatalogs > 0
                            ? "файл целиком повторяет уже прочитанные"
                            : $"каталогов {js.Catalogs}";
                    if (js.DuplicateCatalogs > 0)
                        what += $", повторных (уже были в других файлах) {js.DuplicateCatalogs} — пропущены";
                    log.Report($"[{i + 1}/{total}] {unique}: {what}, офферов {js.Offers}, новых товаров {js.NewProducts}");

                    if (opt.Economy) GC.Collect();   // слабый ПК: буфер файла больше не нужен
                    continue;
                }

                // ---------- YML: файл — это новый «каталог» с одним магазином (и, если задан, регионом) ----------
                int cat = catShops.Count;
                int shop = Intern(shopIndex, geo.ShopNames, unique);
                var regions = new HashSet<int>();
                if (!string.IsNullOrWhiteSpace(feed.Region))
                    regions.Add(Intern(regionIndex, geo.RegionNames, feed.Region.Trim()));

                catShops.Add(new[] { shop });
                catRegions.Add(regions.ToArray());
                foreach (var r in regions)
                {
                    if (!geo.RegionShops.TryGetValue(r, out var rs)) geo.RegionShops[r] = rs = new HashSet<int>();
                    rs.Add(shop);
                }

                string ymlSalt = $"{i}|";
                foreach (var (key, offer) in buffer) AddYmlToGroups(list, index, key, offer, cat, HashId(ymlSalt + YmlId(offer)));
                if (goodYmlPath != null) { ymlFiles.Add((ymlSalt, goodYmlPath)); ymlCats.Add(cat); }

                okFeeds++;
                totalOffers += st!.Count;
                totalNotPromo += st.NotPromo;
                log.Report($"[{i + 1}/{total}] {unique}: товаров {st.Count}");

                if (opt.Economy && (i & 31) == 31) GC.Collect();   // слабый ПК: не даём куче разрастаться
            }

            if (okFeeds == 0) throw new Exception("Ни один файл списка не удалось прочитать.");

            if (failedFeeds.Count > 0)
            {
                if (previewOnly)
                {
                    log.Report($"⚠ Не прочитано файлов: {failedFeeds.Count} из {total}. Они не участвуют в охвате магазинов.");
                }
                else
                {
                    string failedPath = Path.Combine(outDir, "failed_feeds.txt");
                    File.WriteAllLines(failedPath, failedFeeds);
                    log.Report($"⚠ Не прочитано файлов: {failedFeeds.Count} из {total}. Они не участвуют в охвате магазинов. " +
                               $"Список для повторной загрузки: {failedPath}");
                }
            }
            if (opt.PromoOnly)
                log.Report($"Только акционные позиции: пропущено без oldprice / price_old — {totalNotPromo} из {totalOffers}");
            if (totalDupShops > 0)
                log.Report($"Повторяющихся магазинов пропущено: {totalDupShops} (офферов в них: {totalSkipped}). " +
                           "Каждый магазин и каждый товар учтены один раз.");

            // Магазины и регионы товара: по файлам и каталогам, в которых он встретился
            FinishGeo(s);
            ReportDerivedRegions(s);
            log.Report($"Прочитано файлов: {okFeeds}, магазинов: {geo.UsedShops.Count}" +
                       (geo.UsedRegions.Count > 0 ? $", регионов: {geo.UsedRegions.Count}" : "") +
                       $", уникальных товаров: {list.Count}");

            return (list, totalOffers, geo);
        }

        // Первая непустая картинка оффера (в YML их может быть несколько)
        private static string? FirstPicture(XElement offer, string? shopUrl)
        {
            foreach (var pic in offer.Elements("picture"))
            {
                var url = ResolvePictureUrl(pic.Value, shopUrl);
                if (url != null) return url;
            }
            return null;
        }
    }
}
