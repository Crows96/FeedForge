using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace FeedForge;

// Чтение JSON: каталоги, офферы, группировка по товару.
public static partial class Converter
{
    private sealed partial class Runner
    {
        // ---------- Чтение JSON ----------
        // JSON читается потоком (Utf8JsonReader): в памяти одновременно только один оффер, поэтому расход
        // памяти зависит от числа уникальных товаров, а не от размера файла. Два прохода по файлу:
        // сначала каталоги (география), затем офферы.
        // Один и тот же код читает и одиночный файл, и каждый файл в режиме объединения.

        // Всё, что накапливается при чтении: товары, магазины, регионы, каталоги.
        private sealed class ReadState
        {
            public readonly List<Group> List = new();
            public readonly Dictionary<string, Group> Index = new();
            public readonly GeoModel Geo = new();
            public readonly Dictionary<string, int> ShopIndex = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> RegionIndex = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<int[]> CatRegions = new();
            public readonly List<int[]> CatShops = new();
            // Каталоги, уже прочитанные из предыдущих файлов: ключ каталога -> имя файла
            public readonly Dictionary<string, string> CatalogOwners = new(StringComparer.OrdinalIgnoreCase);
            public int DerivedCatalogs;                       // у скольких каталогов регион определён по адресу магазина
            public readonly HashSet<string> UnparsedAddresses = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class JsonFileStats
        {
            public int Catalogs, DuplicateCatalogs, Offers, NotPromo, SkippedDuplicate, NewProducts;
            public bool WholeFileIsShop;     // в файле нет ни магазинов, ни регионов: весь файл считаем одним магазином
        }

        private readonly record struct BufferedOffer(
            string Key, string? Description, string? Image, DateTime? DateStart, DateTime? DateEnd, decimal Price, int Cat, ulong Id = 0,
            decimal? Old = null);

        // Магазины (target_shops) и регионы (target_regions, иначе region) одного каталога.
        // Если регионов нет, а режим регионов включён (deriveFromAddress), регионом считается город из адреса магазина.
        // derived = true, если регионы получены именно так; unparsed получает адреса, из которых город определить не удалось.
        private static void ReadCatalogGeo(JsonElement c, bool deriveFromAddress, bool bySubject, List<string>? unparsed,
                                           out List<string> shops, out List<string> regions, out bool derived)
        {
            derived = false;
            regions = GetStringList(c, "target_regions").Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (regions.Count == 0)
            {
                string? one = GetString(c, "region");
                if (!string.IsNullOrWhiteSpace(one)) regions.Add(one.Trim());
            }
            shops = GetStringList(c, "target_shops").Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Названия из target_regions приводим к официальным по справочнику («Московская обл.» -> «Московская область»);
            // в режиме субъектов РФ город из target_regions заменяется его регионом
            if (regions.Count > 0 && RuGeo.Loaded)
                regions = regions.Select(x => RuGeo.NormalizeRegion(x, bySubject) ?? x)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            if (regions.Count == 0 && deriveFromAddress && shops.Count > 0)
            {
                foreach (var sh in shops)
                {
                    string? city = bySubject ? RuGeo.RegionOfAddress(sh) : CityFromAddress(sh);
                    if (city == null) unparsed?.Add(sh);
                    else if (!regions.Contains(city, StringComparer.OrdinalIgnoreCase)) regions.Add(city);
                }
                derived = true;
            }
        }

        private static readonly Regex SettlementPrefixRx = new(
            @"^(?:г|город|пгт|рп|р\.п|пос|посёлок|поселок|п|с|село|ст-ца|станица|х|хутор|аул|дер|деревня)(?:\.\s*|\s+)(?<n>\p{L}.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SettlementSuffixRx = new(
            @"^(?<n>\p{L}.*?)\s+(?:г|гор)\.?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SkipPartRx = new(
            @"^(?:россия|рф|russia|\d{5,6}|\d.*)$|обл(?:\.|асть|\b)|край\b|респ(?:\.|ублика|\b)|автоном|\bао\b|округ|район|р-н",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex StreetPartRx = new(
            @"^(?:ул|улица|пр|пр-т|просп|проспект|пер|переулок|б-р|бульвар|ш|шоссе|наб|набережная|пл|площадь|тракт|проезд|аллея|линия|мкр|микрорайон|тц|трц|тк|д|дом|к|корп|стр|вл|кв|пом|литер|лит)(?:\.|\s|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Город из адреса: «г. Москва, пр. Ленинградский, д. 77» -> «Москва». null, если определить не удалось.</summary>
        public static string? CityFromAddress(string address)
        {
            var parts = address.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (parts.Count == 0) return null;

            // 1. Явный населённый пункт: «г. Химки», «город Химки», «г Химки», «Химки г.», «пгт Янтарный»
            foreach (var p in parts)
            {
                var m = SettlementPrefixRx.Match(p);
                if (!m.Success) m = SettlementSuffixRx.Match(p);
                if (m.Success) return CleanCity(m.Groups["n"].Value);
            }

            // 2. Без пометки («Москва, ул. Ленина, 5»): первая часть, которая не страна, не индекс, не область и не улица
            foreach (var p in parts)
            {
                if (SkipPartRx.IsMatch(p) || StreetPartRx.IsMatch(p)) continue;
                return CleanCity(p);
            }
            return null;
        }

        private static string? CleanCity(string s)
        {
            s = Regex.Replace(s, @"\s+", " ").Trim(' ', '.', ',', ';');
            return s.Length == 0 ? null : s;
        }

        // Каталог из другого файла считается повтором, только если совпадает всё: id, магазины и регионы.
        // Без id, магазинов и регионов каталог повтором не считается никогда.
        private static string? BuildCatalogKey(string? id, List<string> shops, List<string> regions)
        {
            if (string.IsNullOrWhiteSpace(id) && shops.Count == 0 && regions.Count == 0) return null;
            return (id?.Trim() ?? "") + "\u0001" +
                   string.Join("|", shops.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) + "\u0001" +
                   string.Join("|", regions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }

        // Читает один JSON-файл в общее состояние.
        // atomic = true (режим объединения): товары файла сначала копятся отдельно и попадают в общий набор только
        // после чтения всего файла, поэтому битый файл ничего не портит и просто пропускается.
        // atomic = false (один файл): товары сразу идут в общий набор — так меньше памяти на очень больших файлах.
        private JsonFileStats ReadJsonFile(ReadState s, string path, string fileName, FeedSource? feed,
                                           string? label, bool atomic, Action<double>? onPercent)
        {
            var st = new JsonFileStats();
            var geo = s.Geo;

            int baseCat = s.CatShops.Count;       // индекс первого каталога этого файла в общем списке
            int baseMulti = geo.Multi.Count;      // индекс первого «составного» кода (оффер в нескольких каталогах)

            var localCats = new List<(string[] Shops, string[] Regions, bool Derived)>();
            bool deriveRegions = opt.RegionMode is RegionMode.AllRegions or RegionMode.PerRegion or RegionMode.RegionsMerged;
            var unparsed = new List<string>();
            var localMulti = new List<int[]>();           // коды каталогов уже в общей нумерации
            var ownedKeys = new List<string>();
            var offerCats = new Dictionary<ulong, int>(); // хэш id оффера -> каталог (общая нумерация) или составной код
            var catPositions = new List<int>();           // каталог в файле по порядку -> общий индекс каталога (-1 — повтор из другого файла)
            HashSet<ulong>? skipped = null;               // офферы каталогов, которые уже были в предыдущих файлах
            bool anyGeo = false;

            // Проход 1: каталоги
            ScanArray(path, "catalogs", c =>
            {
                ReadCatalogGeo(c, deriveRegions, BySubject, unparsed, out var shops, out var regions, out var derived);
                string? key = BuildCatalogKey(GetString(c, "id"), shops, regions);
                bool dup = key != null && s.CatalogOwners.ContainsKey(key);

                int ci = baseCat + localCats.Count;
                catPositions.Add(dup ? -1 : ci);
                if (dup)
                {
                    st.DuplicateCatalogs++;
                }
                else
                {
                    localCats.Add((shops.ToArray(), regions.ToArray(), derived));
                    if (derived) s.DerivedCatalogs++;
                    if (key != null) ownedKeys.Add(key);
                    if (shops.Count > 0 || regions.Count > 0) anyGeo = true;
                }

                if (c.TryGetProperty("offers", out var ids) && ids.ValueKind == JsonValueKind.Array)
                {
                    foreach (var idEl in ids.EnumerateArray())
                    {
                        if (idEl.ValueKind != JsonValueKind.String) continue;
                        string? oid = idEl.GetString();
                        if (string.IsNullOrEmpty(oid)) continue;

                        ulong h = HashId(oid);

                        if (dup)
                        {
                            (skipped ??= new HashSet<ulong>()).Add(h);
                            continue;
                        }

                        if (!offerCats.TryGetValue(h, out var code))
                        {
                            offerCats[h] = ci;
                        }
                        else if (code == ci)
                        {
                            // повтор id внутри одного каталога
                        }
                        else if (code >= 0)
                        {
                            localMulti.Add(new[] { code, ci });
                            offerCats[h] = -(baseMulti + localMulti.Count - 1) - 2;
                        }
                        else
                        {
                            int mi = -code - 2 - baseMulti;
                            var old = localMulti[mi];
                            if (Array.IndexOf(old, ci) < 0)
                            {
                                var arr = new int[old.Length + 1];
                                old.CopyTo(arr, 0);
                                arr[^1] = ci;
                                localMulti[mi] = arr;
                            }
                        }
                    }
                }
            }, ct);

            // В режиме объединения файл без всякой географии — это один магазин (как YML-файл)
            int? wholeFileCat = null;
            if (feed != null && !anyGeo && st.DuplicateCatalogs == 0)
            {
                st.WholeFileIsShop = true;
                st.DuplicateCatalogs = 0;
                skipped = null;
                localCats.Clear();
                localMulti.Clear();
                ownedKeys.Clear();
                localCats.Add((new[] { (label ?? fileName).Trim() },
                               string.IsNullOrWhiteSpace(feed.Region) ? Array.Empty<string>() : new[] { feed.Region.Trim() }, false));
                wholeFileCat = baseCat;
            }
            if (st.WholeFileIsShop) for (int pi = 0; pi < catPositions.Count; pi++) catPositions[pi] = baseCat;
            catPos[idSalt] = catPositions;
            st.Catalogs = localCats.Count;
            foreach (var u in unparsed) s.UnparsedAddresses.Add(u);

            // Каталоги переносим в общее состояние (магазины и регионы получают общие номера)
            void CommitCatalogs()
            {
                foreach (var m in localMulti) geo.Multi.Add(m);
                foreach (var (shopNames, regionNames, derived) in localCats)
                {
                    var shops = shopNames.Select(n => Intern(s.ShopIndex, geo.ShopNames, n)).Distinct().ToArray();
                    var regions = regionNames.Select(n => Intern(s.RegionIndex, geo.RegionNames, n)).Distinct().ToArray();
                    s.CatShops.Add(shops);
                    s.CatRegions.Add(regions);

                    if (derived)
                    {
                        // Регион получен из адресов: каждый магазин относим только к городу из его собственного адреса
                        foreach (var sn in shopNames)
                        {
                            string? city = BySubject ? RuGeo.RegionOfAddress(sn) : CityFromAddress(sn);
                            if (city == null) continue;
                            int r = Intern(s.RegionIndex, geo.RegionNames, city);
                            if (!geo.RegionShops.TryGetValue(r, out var rs)) geo.RegionShops[r] = rs = new HashSet<int>();
                            rs.Add(Intern(s.ShopIndex, geo.ShopNames, sn));
                        }
                        continue;
                    }
                    foreach (var r in regions)
                    {
                        if (!geo.RegionShops.TryGetValue(r, out var rs)) geo.RegionShops[r] = rs = new HashSet<int>();
                        rs.UnionWith(shops);
                    }
                }
                foreach (var k in ownedKeys) s.CatalogOwners.TryAdd(k, fileName);
            }

            if (!atomic) CommitCatalogs();

            // Проход 2: офферы
            var buffer = atomic ? new List<BufferedOffer>() : null;
            var localSeen = atomic ? new HashSet<string>() : null;   // у первого оффера товара в файле берём текст и картинку

            bool found = ScanArray(path, "offers", o =>
            {
                st.Offers++;

                // Как в FeedChecker: оффер без description, id, image или price_new в обработку не берётся совсем
                string? lacks = FirstMissingRequiredKey(o);
                if (lacks != null) { CountInvalid($"нет или пусто: {lacks}"); return; }

                // Старая цена (price_old): признак акционного оффера. Режим «только акции» оффер без неё не учитывает совсем —
                // ни в ценах, ни в охвате магазинов и регионов
                var oldVal = GetDecimal(o, "price_old");
                if (!(oldVal.HasValue && oldVal.Value > 0)) oldVal = null;
                if (opt.PromoOnly && !oldVal.HasValue) { st.NotPromo++; return; }

                string id = GetString(o, "id") ?? "";
                ulong h = id.Length > 0 ? HashId(idSalt + id) : 0UL;   // с «солью» файла: идентификатор оффера в результате
                ulong hc = id.Length > 0 ? HashId(id) : 0UL;            // без соли: так оффер сопоставлен каталогу внутри файла

                // Оффер каталога, который уже был в предыдущем файле, второй раз не берём
                if (skipped != null && id.Length > 0 && skipped.Contains(hc) && !offerCats.ContainsKey(hc))
                {
                    st.SkippedDuplicate++;
                    return;
                }

                // Товар определяется только по названию (description), как в FeedChecker: id у одинаковых товаров
                // бывают разными. Без названия товар определяется по id, а без обоих — не склеивается ни с чем.
                string descText = GetString(o, "description") ?? "";
                string key = descText.Length > 0 ? descText : (id.Length > 0 ? id : Guid.NewGuid().ToString("N"));

                int cat = wholeFileCat ?? ((id.Length > 0 && offerCats.TryGetValue(hc, out var cc)) ? cc : -1);

                // Как в FeedChecker: если в файле есть каталоги, а оффер не указан ни в одном из них — это ошибка,
                // такой оффер в обработку не берётся (не попадает ни в цены, ни в охват, ни в число товаров)
                if (cat == -1 && catPositions.Count > 0)
                {
                    CountInvalid("не указан ни в одном каталоге");
                    return;
                }
                var price = GetDecimal(o, "price_new");
                decimal p = price.HasValue && price.Value > 0 ? price.Value : 0m;
                if (p <= 0) { CountInvalid("price_new не больше нуля"); return; }

                if (buffer == null)
                {
                    if (!s.Index.TryGetValue(key, out var grp))
                    {
                        grp = new Group();
                        s.Index[key] = grp;
                        s.List.Add(grp);
                        st.NewProducts++;
                    }

                    // Текст, ссылку и даты читаем только пока они не найдены — лишних строк не создаём
                    if (grp.Description.Length == 0) grp.Description = descText;
                    if (string.IsNullOrWhiteSpace(grp.ImageUrl)) grp.ImageUrl = GetString(o, "image");
                    if (!grp.DateStart.HasValue) grp.DateStart = GetDate(o, "date_start");
                    if (!grp.DateEnd.HasValue) grp.DateEnd = GetDate(o, "date_end");
                    grp.Offers.Add(new PriceEntry(p, cat, h, oldVal.HasValue));
                    if (oldVal.HasValue) grp.OldPrice ??= oldVal;
                }
                else
                {
                    string? desc = null, img = null;
                    DateTime? ds = null, de = null;
                    if (localSeen!.Add(key))
                    {
                        desc = descText.Length > 0 ? descText : null;
                        img = GetString(o, "image");
                        ds = GetDate(o, "date_start");
                        de = GetDate(o, "date_end");
                    }
                    buffer.Add(new BufferedOffer(key, desc, img, ds, de, p, cat, h, oldVal));
                }
            }, ct, onPercent);

            if (!found) throw new Exception("В JSON нет массива \"offers\".");

            if (atomic)
            {
                if (st.Offers == 0) throw new InvalidDataException("в файле нет офферов");

                CommitCatalogs();
                foreach (var b in buffer!)
                {
                    if (!s.Index.TryGetValue(b.Key, out var grp))
                    {
                        grp = new Group();
                        s.Index[b.Key] = grp;
                        s.List.Add(grp);
                        st.NewProducts++;
                    }
                    if (grp.Description.Length == 0 && b.Description != null) grp.Description = b.Description;
                    if (string.IsNullOrWhiteSpace(grp.ImageUrl)) grp.ImageUrl = b.Image;
                    if (!grp.DateStart.HasValue) grp.DateStart = b.DateStart;
                    if (!grp.DateEnd.HasValue) grp.DateEnd = b.DateEnd;
                    grp.Offers.Add(new PriceEntry(b.Price, b.Cat, b.Id, b.Old.HasValue));
                    if (b.Old.HasValue) grp.OldPrice ??= b.Old;
                }
                buffer!.Clear();
            }

            return st;
        }

        // Магазины и регионы товара считаем один раз по уникальным каталогам его офферов.
        // Учитываем только магазины и регионы, в которых реально есть товары (пустые каталоги не считаем).
        private void FinishGeo(ReadState s)
        {
            s.Geo.CatShops = s.CatShops.ToArray();
            s.Geo.CatRegions = s.CatRegions.ToArray();
            currentGeo = s.Geo;
            int dupInCatalog = 0;

            foreach (var g in s.List)
            {
                ct.ThrowIfCancellationRequested();
                var seen = new HashSet<int>();
                var offers = g.Offers;
                int w = 0;
                for (int i = 0; i < offers.Count; i++)
                {
                    var e = offers[i];
                    if (e.Cat != -1)
                    {
                        // тот же товар (название) уже есть в этом каталоге — ошибка данных: второй оффер не берётся
                        if (!seen.Add(e.Cat)) { dupInCatalog++; continue; }
                        AddGeo(g, s.Geo, e.Cat);
                    }
                    offers[w++] = e;
                }
                if (w < offers.Count) offers.RemoveRange(w, offers.Count - w);

                g.Offers.TrimExcess();
                s.Geo.UsedShops.UnionWith(g.Shops);
                s.Geo.UsedRegions.UnionWith(g.Regions);
            }
            if (dupInCatalog > 0) CountInvalid("дубль товара в каталоге", dupInCatalog);
        }

        // Сообщает, если регионы определялись по адресам магазинов
        private void ReportDerivedRegions(ReadState s)
        {
            if (s.DerivedCatalogs == 0) return;
            log.Report($"Регионы определены по адресам магазинов (target_regions в файлах нет): найдено {s.Geo.RegionNames.Count} " +
                       (BySubject ? "(субъекты РФ по справочнику)." : "(города)."));
            if (s.UnparsedAddresses.Count > 0)
                log.Report($"⚠ Не удалось определить {(BySubject ? "регион" : "город")} у {s.UnparsedAddresses.Count} адресов, они не попадут ни в один регион. " +
                           $"Например: {string.Join(" | ", s.UnparsedAddresses.Take(3))}");
        }

        private void WarnNoGeoData()
        {
            if (opt.RegionBySubject && !RuGeo.Loaded)
                log.Report("⚠ Справочник регионов (ru_geo.tsv) не встроен в программу: регионами останутся города из адресов. " +
                           "Добавьте ru_geo.tsv в проект как EmbeddedResource.");
        }

        private (List<Group> Groups, int OffersCount, GeoModel Geo) ReadGroups(string jsonPath, CancellationToken ct)
        {
            WarnNoGeoData();
            var s = new ReadState();
            currentGeo = s.Geo;

            int nextLog = 10;
            var st = ReadJsonFile(s, jsonPath, Path.GetFileName(jsonPath), null, null, atomic: false, p =>
            {
                if (p >= nextLog)
                {
                    log.Report($"Чтение товаров: {(int)p}%");
                    nextLog = (int)p / 10 * 10 + 10;
                }
                percent.Report(Math.Min(39.9, p * 0.4));   // чтение — первые 40% общей шкалы
            });

            if (opt.PromoOnly)
                log.Report($"Только акционные позиции: пропущено без price_old — {st.NotPromo} из {st.Offers}");

            FinishGeo(s);
            ReportDerivedRegions(s);
            return (s.List, st.Offers, s.Geo);
        }

        // Быстрая проверка локальных JSON перед долгим чтением: читаются только каталоги (они обычно в начале файла),
        // поэтому это занимает секунды. Сообщает о битых файлах, о повторяющихся магазинах и о числе магазинов.
        // Ссылки не проверяются: ради этого пришлось бы скачивать их дважды.
        private void PreCheckJson(IReadOnlyList<FeedSource> feeds)
        {
            var owners = new Dictionary<string, (int Index, string Name)>(StringComparer.OrdinalIgnoreCase);
            var shopNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var regionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int files = 0, bad = 0, cats = 0, dups = 0;
            bool deriveRegions = opt.RegionMode is RegionMode.AllRegions or RegionMode.PerRegion or RegionMode.RegionsMerged;

            for (int i = 0; i < feeds.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string path = feeds[i].Source.Trim();
                if (IsUrl(path) || !File.Exists(path)) continue;

                try { if (DetectFormat(path) != SourceFormat.Json) continue; }
                catch { continue; }

                files++;
                int fileNo = i;
                string name = Path.GetFileName(path);
                int fileCats = 0, fileDups = 0;
                var firstFiles = new HashSet<string>();

                try
                {
                    bool found = ScanArray(path, "catalogs", c =>
                    {
                        ReadCatalogGeo(c, deriveRegions, BySubject, null, out var shops, out var regions, out _);
                        string? key = BuildCatalogKey(GetString(c, "id"), shops, regions);
                        fileCats++;
                        if (key != null)
                        {
                            if (owners.TryGetValue(key, out var first))
                            {
                                if (first.Index != fileNo)     // повтор внутри одного файла повтором не считаем
                                {
                                    fileDups++;
                                    firstFiles.Add(first.Name);
                                }
                            }
                            else owners[key] = (fileNo, name);
                        }
                        foreach (var sh in shops) shopNames.Add(sh);
                        foreach (var rg in regions) regionNames.Add(rg);
                    }, ct);

                    if (!found)
                        log.Report($"  Проверка: {name} — нет массива catalogs, файл будет считаться одним магазином.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    bad++;
                    log.Report($"  ⚠ Проверка: {name} — не читается ({ex.Message}). Файл будет пропущен.");
                    continue;
                }

                cats += fileCats;
                dups += fileDups;
                if (fileDups > 0)
                    log.Report($"  Проверка: {name} — повторяющихся магазинов {fileDups} из {fileCats} " +
                               $"(уже есть в: {string.Join(", ", firstFiles.Take(3))}); они будут пропущены.");
            }

            if (files == 0) return;

            log.Report($"Проверка JSON: файлов {files}, каталогов {cats}, разных магазинов {shopNames.Count}" +
                       (regionNames.Count > 0 ? $", регионов {regionNames.Count}" : "") +
                       (dups > 0 ? $", повторов {dups} (пропущу)" : ", повторов нет") +
                       (bad > 0 ? $", не читаются: {bad}" : ""));
            if (opt.ShopFilter && shopNames.Count > 1 &&
                opt.RegionMode is not (RegionMode.PerRegion or RegionMode.RegionsMerged or RegionMode.PerShop))
            {
                int need = (int)Math.Ceiling(shopNames.Count * opt.ShopPercent / 100m);
                log.Report($"Фильтр по магазинам: товар должен быть минимум в {need} из {shopNames.Count} магазинов ({opt.ShopPercent}%).");
            }
        }
    }
}
