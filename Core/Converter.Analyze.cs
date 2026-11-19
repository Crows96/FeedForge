namespace FeedForge;

// Анализ сети: читает все файлы без фильтров (без «Только акционные», охвата и разброса цен) и выдаёт отчёт
// с рекомендациями по настройкам, которые есть в программе. Ничего не записывает и не скачивает.
// Офферы с ошибками (нет ключей, нет в каталогах, дубль в каталоге, цена не больше нуля) в расчёты не входят —
// они только перечисляются в отчёте с количеством.
public static partial class Converter
{
    public static async Task<List<string>> AnalyzeNetworkAsync(
        ConvertOptions opt, IProgress<string> log, IProgress<double> percent, CancellationToken ct)
    {
        var all = opt with
        {
            PromoOnly = false,
            ShopFilter = false,
            MaxSpreadPercent = 100000m,
            RegionMode = RegionMode.Ignore,
            MergeSame = false
        };
        // анализ — это только чтение (первые 40% общей шкалы): растягиваем на всю полосу
        var scaled = new DelegateProgress<double>(p => percent.Report(Math.Min(99.9, p * 2.5)));
        var runner = new Runner(all, log, scaled, ct, new List<string>(), previewOnly: true);
        return await runner.AnalyzeAsync(opt);
    }

    private sealed partial class Runner
    {
        public async Task<List<string>> AnalyzeAsync(ConvertOptions cur)
        {
            List<Group> groups;
            int offersCount;
            GeoModel geo;

            if (opt.Feeds != null)
            {
                (groups, offersCount, geo) = await ReadGroupsFeedsAsync();
            }
            else
            {
                string path = opt.JsonSource.Trim();
                var format = DetectFormat(path);
                if (format == SourceFormat.Xml) { ymlFiles.Add(("", path)); ymlAll = true; }
                else jsonFiles.Add(("", path));
                log.Report(format == SourceFormat.Xml ? "Читаю YML (XML)..." : "Читаю JSON...");
                (groups, offersCount, geo) = format == SourceFormat.Xml
                    ? ReadGroupsXml(path, ct)
                    : ReadGroups(path, ct);
            }
            ct.ThrowIfCancellationRequested();
            return BuildAnalysis(cur, groups, offersCount, geo);
        }

        private static string Share(long a, long b) => b <= 0 ? "0%" : $"{a * 100.0 / b:F0}%";

        private List<string> BuildAnalysis(ConvertOptions cur, List<Group> groups, int offersCount, GeoModel geo)
        {
            var R = new List<string>();
            var cj = cur.Json;
            int limit = Math.Max(0, cj?.LimitOffers ?? 0);
            var limitMode = cj?.LimitMode ?? LimitMode.WholeFile;
            bool common = cj?.Common == true;
            bool preferPromo = cj?.PreferPromo == true;
            bool allowRepeats = cj?.AllowRepeats ?? true;

            // ---------- подсчёт (только офферы без ошибок) ----------
            long offers = 0, promoOffers = 0;
            int products = 0, promoProducts = 0;
            var perCat = new Dictionary<int, int[]>();        // каталог -> [офферов, акционных офферов]
            var prod = new List<(int Cov, int Geos, decimal Spread)>();
            int totalShops = geo.UsedShops.Count, totalRegions = geo.UsedRegions.Count;
            bool byShops = totalShops > 1;
            bool byRegions = !byShops && totalRegions > 1;
            int totalGeo = byShops ? totalShops : byRegions ? totalRegions : 0;
            string unit = byShops ? "магазинов" : "регионов";
            var geos = new HashSet<int>();

            foreach (var g in groups)
            {
                decimal min = decimal.MaxValue, max = 0;
                bool any = false, anyPromo = false;
                geos.Clear();
                foreach (var e in g.Offers)
                {
                    if (e.Price <= 0) continue;
                    any = true;
                    offers++;
                    if (e.Price < min) min = e.Price;
                    if (e.Price > max) max = e.Price;
                    if (e.Promo) { promoOffers++; anyPromo = true; }
                    foreach (var ck in CatalogsOf(e))
                    {
                        if (ck == -1) continue;
                        geos.Add(ck);
                        if (!perCat.TryGetValue(ck, out var a)) perCat[ck] = a = new int[2];
                        a[0]++;
                        if (e.Promo) a[1]++;
                    }
                }
                if (!any) continue;
                products++;
                if (anyPromo) promoProducts++;
                int cov = byShops ? g.Shops.Count : byRegions ? g.Regions.Count : 0;
                prod.Add((cov, geos.Count, (max - min) / min));
            }

            int catalogs = perCat.Count;
            int noShops = 0, noRegions = 0;
            foreach (var ck in perCat.Keys)
            {
                if (ck < 0 || ck >= geo.CatShops.Length) continue;
                if (geo.CatShops[ck].Length == 0) noShops++;
                if (ck < geo.CatRegions.Length && geo.CatRegions[ck].Length == 0) noRegions++;
            }

            // ---------- 1. Общее и ошибки ----------
            R.Add("АНАЛИЗ СЕТИ (читаются все офферы, текущие фильтры не применяются)");
            R.Add($"Записей в исходных файлах: {offersCount}");
            if (invalidTotal > 0)
            {
                R.Add($"Ошибки в данных: {invalidTotal} — в расчёты ниже не входят:");
                foreach (var kv in invalidBy.OrderByDescending(x => x.Value))
                    R.Add($"  • {kv.Key}: {kv.Value}");
            }
            else R.Add("Ошибки в данных: не найдено");

            if (products == 0)
            {
                R.Add("Офферов без ошибок нет — анализировать нечего.");
                return R;
            }
            R.Add($"Без ошибок: офферов {offers}, уникальных товаров (по названию) {products}, " +
                  $"в среднем {(double)offers / products:F1} оффера на товар");

            // ---------- 2. Гео ----------
            if (catalogs > 0)
            {
                string g1 = $"Каталогов: {catalogs}";
                if (totalShops > 0) g1 += $", магазинов (target_shops): {totalShops}";
                if (totalRegions > 0) g1 += $", регионов (target_regions): {totalRegions}";
                R.Add(g1);
                if (noShops > 0 && totalShops > 0) R.Add($"  каталогов без target_shops: {noShops}");
                if (noRegions > 0 && totalRegions > 0) R.Add($"  каталогов без target_regions: {noRegions}");
            }
            else R.Add("Каталогов в данных нет (гео не определено).");

            // ---------- 3. Акции ----------
            R.Add("");
            R.Add($"АКЦИИ: офферов с price_old: {promoOffers} из {offers} ({Share(promoOffers, offers)}), " +
                  $"товаров с акцией: {promoProducts} из {products} ({Share(promoProducts, products)})");
            var promoPer = perCat.Values.Select(a => a[1]).OrderBy(x => x).ToList();
            if (promoPer.Count > 0 && promoOffers > 0)
            {
                R.Add($"  акционных офферов на каталог: минимум {promoPer[0]}, среднее {promoPer.Average():F0}, максимум {promoPer[^1]}; " +
                      $"каталогов, где их меньше 10: {promoPer.Count(x => x < 10)}");
                if (limit > 0)
                    R.Add($"  каталогов, где акционных меньше лимита ({limit}): {promoPer.Count(x => x < limit)} из {promoPer.Count}");
            }

            // ---------- 4. Офферов на каталог (лимит) ----------
            var perCatCounts = perCat.Values.Select(a => a[0]).OrderBy(x => x).ToList();
            if (perCatCounts.Count > 0)
            {
                R.Add("");
                R.Add($"ОФФЕРОВ НА КАТАЛОГ: минимум {perCatCounts[0]}, среднее {perCatCounts.Average():F0}, " +
                      $"медиана {perCatCounts[perCatCounts.Count / 2]}, максимум {perCatCounts[^1]}");
                if (limit > 0)
                    R.Add($"  каталогов, где офферов больше лимита ({limit}): {perCatCounts.Count(x => x > limit)} из {perCatCounts.Count}");
            }

            // ---------- 5. Охват и цены ----------
            int Need(int pct) => Math.Max(1, (int)Math.Ceiling(totalGeo * pct / 100m));
            int CountAt(int pct, decimal thr)
            {
                int need = Need(pct), n = 0;
                foreach (var p in prod)
                    if (p.Cov >= need && p.Spread <= thr) n++;
                return n;
            }

            var multi = prod.Where(p => p.Geos >= 2).ToList();
            decimal[] spreadSteps = { 0m, 0.01m, 0.02m, 0.05m, 0.10m, 0.20m, 0.50m };
            decimal recSpread = 0.50m;
            if (multi.Count > 0)
            {
                R.Add("");
                R.Add($"ЦЕНЫ: товаров, которые есть больше чем в одном гео (каталоге): {multi.Count} из {products}");
                var parts = new List<string>();
                foreach (var t in spreadSteps)
                {
                    int n = multi.Count(p => p.Spread <= t);
                    parts.Add(t == 0m ? $"одинаковая {n} ({Share(n, multi.Count)})" : $"до {t * 100:F0}% — {n} ({Share(n, multi.Count)})");
                }
                R.Add("  разброс цены между гео: " + string.Join("; ", parts));
                int over = multi.Count(p => p.Spread > 0.50m);
                if (over > 0) R.Add($"  разброс больше 50%: {over}");
                foreach (var t in spreadSteps)
                    if (t > 0m && multi.Count(p => p.Spread <= t) * 10L >= multi.Count * 9L) { recSpread = t; break; }
            }

            if (totalGeo > 0)
            {
                var tiers = new[] { 100, 95, 80, 50 };
                var thr = new[] { 0m, 0.01m, 0.05m, 0.10m, 0.20m };
                R.Add("");
                R.Add($"ОХВАТ ({unit}: {totalGeo}) И РАЗБРОС ЦЕН — сколько товаров подходит:");
                R.Add($"  {"Товар есть минимум в",-26}{"цена одинак.",13}{"до 1%",8}{"до 5%",8}{"до 10%",8}{"до 20%",8}{"любой",8}");
                foreach (var t in tiers)
                {
                    string label = $"{t}% {unit}";
                    var c = thr.Select(x => CountAt(t, x)).ToArray();
                    R.Add($"  {label,-26}{c[0],13}{c[1],8}{c[2],8}{c[3],8}{c[4],8}{CountAt(t, decimal.MaxValue),8}");
                }
            }

            // ---------- 6. Рекомендации ----------
            R.Add("");
            R.Add("РЕКОМЕНДАЦИИ (по настройкам, которые есть в программе):");
            int n1 = 0;
            void Rec(string text) => R.Add($"  {++n1}. {text}");

            // акции
            if (promoOffers == 0)
            {
                Rec("Акционных офферов нет (ни у одного нет price_old): «Только акционные» и «Сначала акционные» выключите" +
                    (cur.PromoOnly ? " — сейчас «Только акционные» включено, результат будет пустым." : "."));
            }
            else if (promoOffers == offers)
            {
                Rec("Все офферы акционные: «Только акционные» ничего не отсекает, можно оставить как есть.");
            }
            else
            {
                string s = $"Акционных {Share(promoOffers, offers)} офферов: «Только акционные» оставит {promoOffers} из {offers}";
                bool lowLimit = limit > 0 && promoPer.Count(x => x < limit) > 0;
                if (cur.PromoOnly && lowLimit)
                    s += $"; в {promoPer.Count(x => x < limit)} каталогах акционных меньше лимита {limit}, и они останутся неполными. " +
                         "Чтобы добирать обычными — выключите «Только акционные» и включите «Сначала акционные».";
                else if (!cur.PromoOnly && lowLimit && !preferPromo)
                    s += $". В {promoPer.Count(x => x < limit)} каталогах акционных меньше лимита {limit}: включите «Сначала акционные» — акционные пойдут первыми, остальное доберётся обычными.";
                else if (!cur.PromoOnly && !preferPromo)
                    s += ". Если нужны первыми именно акции — включите «Сначала акционные».";
                else s += ".";
                Rec(s);
            }

            // режим сборки
            if (totalGeo > 0 && products > 0)
            {
                int eqAll = CountAt(100, 0m), eq95 = CountAt(95, 0m);
                if (eqAll * 2 >= products)
                    Rec($"«Общий каталог для всей России» подойдёт: {eqAll} из {products} товаров ({Share(eqAll, products)}) есть во всех {unit} с одной ценой. " +
                        $"В общем каталоге будет по одному офферу на товар.");
                else
                    Rec($"Лучше «Как в исходных файлах»: во всех {unit} с одной ценой только {eqAll} из {products} товаров ({Share(eqAll, products)}), " +
                        $"в {(byShops ? "95% магазинов" : "95% регионов")} — {eq95}. Общий каталог потеряет часть товаров." +
                        (common ? " Сейчас выбран общий каталог." : ""));
            }

            // разброс цен
            if (multi.Count > 0)
            {
                string cs = cur.MaxSpreadPercent >= 100000m ? "сейчас не проверяется" : $"сейчас {cur.MaxSpreadPercent}%";
                Rec($"«Макс. разброс цен»: рекомендую {recSpread * 100:F0}% — при нём остаётся {Share(multi.Count(p => p.Spread <= recSpread), multi.Count)} " +
                    $"товаров, которые есть в нескольких гео ({cs}). Работает в режимах «Общий каталог» и «Свои настройки».");
            }

            // охват
            if (totalGeo > 0)
            {
                int target = limit > 0 ? limit : 1;
                int recPct = 0;
                foreach (var t in new[] { 100, 95, 80, 50 })
                    if (CountAt(t, recSpread) >= target) { recPct = t; break; }
                string cs = cur.ShopFilter ? $"сейчас {cur.ShopPercent}%" : "сейчас выключен";
                if (recPct > 0)
                    Rec($"«Охват магазинов»: рекомендую минимум {recPct}% — при разбросе до {recSpread * 100:F0}% подходит {CountAt(recPct, recSpread)} товаров" +
                        (limit > 0 ? $" (лимит {limit})" : "") + $" ({cs}).");
                else
                    Rec($"«Охват магазинов»: даже при 50% подходит только {CountAt(50, recSpread)} товаров — лимит {limit} не наберётся ({cs}).");
            }

            // лимит
            if (limit > 0 && perCatCounts.Count > 0)
            {
                int over = perCatCounts.Count(x => x > limit);
                if (limitMode == LimitMode.PerCatalog)
                {
                    if (over == 0) Rec($"Лимит {limit} на каждый каталог не работает: максимум офферов в каталоге {perCatCounts[^1]}.");
                    else Rec($"Лимит {limit} на каждый каталог срежет офферы в {over} каталогах из {perCatCounts.Count}.");
                }
                else if (limitMode == LimitMode.WholeFile && catalogs > 1)
                {
                    Rec($"Лимит {limit} задан «на весь результат», а каталогов {catalogs}: в среднем получится около {Math.Max(1, limit / catalogs)} офферов на каталог. " +
                        $"Если лимит нужен на каждый каталог — выберите «На каждый каталог».");
                }
            }

            // повторы товара между каталогами
            if (catalogs > 1)
            {
                double avgGeos = multi.Count > 0 ? multi.Average(p => p.Geos) : 1;
                if (!allowRepeats && multi.Count > 0)
                    Rec($"«Один товар может быть в разных каталогах» выключено: из {multi.Count} товаров, которые есть в нескольких каталогах " +
                        $"(в среднем в {avgGeos:F1}), каждый останется только в одном. Обычно это включают, иначе в остальных каталогах товара не будет.");
            }

            if (n1 == 0) Rec("Особых замечаний нет: текущие настройки подходят.");
            return R;
        }
    }
}
