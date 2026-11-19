using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace FeedForge;

// Одна выгрузка: отбор товаров, картинки, части (Excel).
public static partial class Converter
{
    private sealed partial class Runner
    {
        // Подробная статистика по товарам выгрузки: сколько уникальных товаров и офферов, как они разложены по гео
        // и как соотносятся цены одного товара в разных гео. Офферы с ошибками сюда не входят (исключены при чтении).
        private void LogProductDetails(SelStats st, bool useShops, int needShops, int shopTotal, Action<string> L)
        {
            if (st.Candidates == 0) return;
            double perProduct = (double)st.Offers / st.Candidates;
            L($"Уникальных товаров (по названию) во всех фидах: {st.Candidates}, офферов с ценой: {st.Offers} " +
              $"(в среднем {perProduct:F1} на товар), акционных товаров: {st.Promo}");
            L($"Товар в одном гео: {st.OneGeo}; в нескольких гео: {st.MultiGeo} " +
              $"(2–5 гео: {st.Geo2to5}, 6–20: {st.Geo6to20}, 21–100: {st.Geo21to100}, больше 100: {st.GeoOver100})");
            if (useShops)
                L($"Охват по магазинам (минимум в {needShops} из {shopTotal}): прошли {st.Candidates - st.ShopDropped}, не прошли {st.ShopDropped}");

            int multi = st.MultiEqual + st.MultiSimilar + st.MultiDropped;
            if (multi == 0) return;
            L($"Из товаров в нескольких гео{(useShops ? ", прошедших охват по магазинам" : "")} ({multi}):");
            L($"  цена во всех гео одинаковая — {st.MultiEqual} (офферов: {st.MultiEqualOffers})");
            if (opt.MaxSpreadPercent >= 100000)
                L($"  цены различаются — {st.MultiSimilar} (офферов: {st.MultiSimilarOffers}); разброс не проверяется");
            else
            {
                string avg = st.MultiSimilar > 0 ? $", средний разброс {st.SimilarSpreadSum / st.MultiSimilar * 100m:F1}%, максимальный {st.SimilarSpreadMax * 100m:F1}%" : "";
                L($"  разница в пределах порога {opt.MaxSpreadPercent}% — {st.MultiSimilar} (офферов: {st.MultiSimilarOffers}{avg})");
                L($"  разброс больше порога (не берутся) — {st.MultiDropped}" +
                  (st.MultiDropped > 0 ? $" (максимальный разброс {st.DroppedSpreadMax * 100m:F1}%)" : ""));
            }
        }

        // ---------- Одна выгрузка: отбор, картинки, части ----------
        private async Task ProcessScopeAsync(Scope scope)
        {
            string tag = scope.Title.Length > 0 ? $"[{scope.Title}] " : "";
            void L(string msg) => log.Report(tag + msg);

            if (scope.Title.Length > 0) log.Report($"=== {scope.Kind}: {scope.Title} ===");

            // --- Отбор: охват по магазинам и разброс цен ---
            bool useShops = false;
            int needShops = 0;
            if (opt.ShopFilter && scope.Shop < 0 && scope.Preselected == null)   // в выгрузке одного магазина охват не нужен; в «регионах в один Excel» отбор уже сделан
            {
                if (scope.ShopSet.Count > 1)
                {
                    needShops = (int)Math.Ceiling(scope.ShopSet.Count * opt.ShopPercent / 100m);
                    useShops = needShops > 0;
                }
                else L("Магазины (target_shops) не заданы или магазин один — фильтр по магазинам не применён.");
            }

            bool tableShown = false;
            if (useShops && scopeCount <= 3)
            {
                LogCoverageTable(scope, needShops, L);
                tableShown = true;
            }

            var (selected, st) = SelectProducts(scope, useShops, needShops);

            if (useShops && selected.Count == 0)
            {
                if (!tableShown) LogCoverageTable(scope, needShops, L);
                // Ничего не подошло: не подставляем «все товары» и ничего не скачиваем, а останавливаемся с понятной ошибкой
                int best = scope.Candidates.Count == 0 ? 0 : scope.Candidates.Max(g => CountIn(g.Shops, scope.ShopSet));
                string where = scope.Title.Length > 0 ? $" ({scope.Kind}: {scope.Title})" : "";
                if (st.ShopDropped == scope.Candidates.Count)
                    throw new InvalidOperationException(
                        $"Ни один товар не встречается минимум в {opt.ShopPercent}% магазинов ({needShops} из {scope.ShopSet.Count}){where}. " +
                        $"Максимальный охват — {best} из {scope.ShopSet.Count} магазинов. Уменьшите процент магазинов. " +
                        "Ничего не скачано.");
                throw new InvalidOperationException(
                    $"Товары, которые есть минимум в {opt.ShopPercent}% магазинов, нашлись ({scope.Candidates.Count - st.ShopDropped}), " +
                    $"но у всех разброс цен между магазинами больше {opt.MaxSpreadPercent}%{where}. " +
                    "Увеличьте допустимый разброс цен или уменьшите процент магазинов. Ничего не скачано.");
            }

            if (useShops)
                L($"Фильтр по магазинам: товар должен быть минимум в {needShops} из {scope.ShopSet.Count} ({opt.ShopPercent}%); " +
                  $"отсеяно: {st.ShopDropped}");
            if (scope.Preselected != null)
            {
                L($"Взято товаров: {selected.Count}");
            }
            else
            {
                L(opt.MaxSpreadPercent >= 100000 ? "Разброс цен не проверяется" : $"Порог разброса цен: {opt.MaxSpreadPercent}%");
                L($"Взято товаров: {selected.Count} " +
                  $"(цена одинаковая: {st.Equal}, разница в пределах порога: {st.Similar}, единственная цена: {st.Single})");
                L($"Пропущено из-за разброса цен: {st.Dropped}");
                if (st.NoPrice > 0) L($"Пропущено без корректной цены: {st.NoPrice}");
                if (scopeCount <= 3) LogProductDetails(st, useShops, needShops, scope.ShopSet.Count, L);   // при многих выгрузках журнал не засоряем
            }

            if (selected.Count == 0)
                throw new InvalidOperationException(
                    "Нет товаров для выгрузки" + (scope.Title.Length > 0 ? $" ({scope.Kind}: {scope.Title})" : "") + ". " +
                    (st.Dropped > 0
                        ? $"У всех товаров ({st.Dropped}) разброс цен больше {opt.MaxSpreadPercent}%. Увеличьте допустимую разницу цен."
                        : "Подходящих товаров с ценой не нашлось."));

            // --- Сборка JSON: картинки не скачиваем, оставляем отобранные офферы и пишем файл ---
            if (opt.Json != null)
            {
                await FinishJsonScopeAsync(scope, selected, L);
                return;
            }

            // --- Картинки (скачиваются в общий кэш, повторно не качаются) ---
            int noImageUrl = 0, imageFailed = 0;

            if (opt.MaxProducts > 0)
            {
                // Лимит (на каждый Excel): берём товары по порядку и качаем картинки партиями,
                // пока не наберём нужное число. Товары без картинки заменяются следующими.
                L($"Лимит: не больше {opt.MaxProducts} товаров");
                var accepted = new List<(Group Group, decimal Price)>();
                int idx = 0;
                while (accepted.Count < opt.MaxProducts && idx < selected.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    int need = opt.MaxProducts - accepted.Count;
                    var batch = selected.GetRange(idx, Math.Min(need, selected.Count - idx));
                    idx += batch.Count;

                    await EnsureImagesAsync(batch.Select(b => b.Group.ImageUrl), null);

                    foreach (var item in batch)
                    {
                        string? url = item.Group.ImageUrl;
                        if (string.IsNullOrWhiteSpace(url)) { noImageUrl++; continue; }
                        if (!HasImage(url)) { imageFailed++; continue; }
                        accepted.Add(item);
                    }
                    Pct(accepted.Count * 80.0 / opt.MaxProducts);
                    L($"Подготовлено товаров: {accepted.Count}/{opt.MaxProducts}");
                }

                if (accepted.Count < opt.MaxProducts)
                    L($"Подходящих товаров меньше лимита: взято {accepted.Count} из {opt.MaxProducts}.");
                selected = accepted;
            }
            else
            {
                await EnsureImagesAsync(selected.Select(s => s.Group.ImageUrl), (d, t) =>
                {
                    Pct(d * 80.0 / t);                              // 0–80% — скачивание
                    if (d % 200 == 0 || d == t) L($"Скачано: {d}/{t}");
                });
            }

            // --- Делим товары на части по размеру картинок ---
            long maxBytes = (long)opt.MaxPartMb * 1024 * 1024;
            var parts = new List<Part>();
            var cur = new Part();
            foreach (var (g, price) in selected)
            {
                // Нет картинки (ссылки нет или скачать не удалось) — товар в Excel не добавляем
                string? url = g.ImageUrl;
                if (string.IsNullOrWhiteSpace(url)) { noImageUrl++; continue; }
                if (!HasImage(url)) { imageFailed++; continue; }

                string file = urlToName[url];
                long size = new FileInfo(CachePath(file)).Length;

                // Картинка, которая уже есть в текущей части, место не занимает
                long add = cur.Images.Contains(file) ? 0 : size;
                if (cur.Rows.Count > 0 && cur.Bytes + add > maxBytes)
                {
                    parts.Add(cur);
                    cur = new Part();
                }

                cur.Rows.Add(new Row(g.Description, file, price, g.DateStart, g.DateEnd, g.OldPrice));
                if (cur.Images.Add(file)) cur.Bytes += size;
            }
            if (cur.Rows.Count > 0) parts.Add(cur);

            if (noImageUrl + imageFailed > 0)
                L($"Не добавлено из-за картинок: {noImageUrl + imageFailed} " +
                  $"(нет ссылки на картинку: {noImageUrl}, не удалось скачать: {imageFailed})");
            if (parts.Count == 0)
            {
                L("Ни один товар не попал в Excel: ни для одного не удалось получить картинку.");
                Pct(100);
                return;
            }
            L($"Частей: {parts.Count} (не больше {opt.MaxPartMb} МБ картинок в каждой)");

            // Удаляем части от предыдущего запуска в этой папке, чтобы не оставалось лишнего
            if (Directory.Exists(scope.Dir))
                foreach (var old in Directory.GetDirectories(scope.Dir, "part_*"))
                    Directory.Delete(old, true);
            Directory.CreateDirectory(scope.Dir);

            // --- Пишем части: Excel + картинки (папка или ZIP) ---
            for (int i = 0; i < parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var part = parts[i];
                string partDir = Path.Combine(scope.Dir, $"part_{i + 1:D3}");
                Directory.CreateDirectory(partDir);
                createdParts.Add(partDir);

                WriteExcel(part, Path.Combine(partDir, "catalog.xlsx"));

                if (opt.MakeZip)
                {
                    string zipPath = Path.Combine(partDir, "images.zip");
                    using var zs = new FileStream(zipPath, FileMode.Create);
                    using var za = new ZipArchive(zs, ZipArchiveMode.Create);
                    foreach (var img in part.Images)
                    {
                        ct.ThrowIfCancellationRequested();
                        za.CreateEntryFromFile(CachePath(img), img, CompressionLevel.NoCompression);   // картинки уже сжаты — архив только упаковывает
                    }
                }
                else
                {
                    string imgDir = Path.Combine(partDir, "images");
                    Directory.CreateDirectory(imgDir);
                    foreach (var img in part.Images)
                    {
                        ct.ThrowIfCancellationRequested();
                        File.Copy(CachePath(img), Path.Combine(imgDir, img), true);
                    }
                }

                L($"part_{i + 1:D3}: товаров {part.Rows.Count}, картинок {part.Images.Count}, " +
                  $"{part.Bytes / 1024.0 / 1024.0:F1} МБ");
                Pct(80 + (i + 1) * 19.0 / parts.Count);             // 80–99% — упаковка
            }
        }

        private static void WriteExcel(Part part, string path)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Лист1");
            // Колонка price_old добавляется в конец, только если у товаров есть старая цена (YML);
            // для JSON формат таблицы остаётся прежним
            bool hasOld = part.Rows.Any(r => r.OldPrice.HasValue);
            var headers = new List<string> { "description", "image", "price_new", "date_start", "date_end" };
            if (hasOld) headers.Add("price_old");
            for (int c = 0; c < headers.Count; c++) ws.Cell(1, c + 1).Value = headers[c];
            ws.Row(1).Style.Font.Bold = true;

            int row = 2;
            foreach (var r in part.Rows)
            {
                ws.Cell(row, 1).Value = r.Description;
                if (r.ImageFile != null) ws.Cell(row, 2).Value = r.ImageFile;
                ws.Cell(row, 3).Value = r.Price;
                // Даты берём из JSON, если они там есть; иначе ячейки остаются пустыми
                if (r.DateStart.HasValue) ws.Cell(row, 4).Value = r.DateStart.Value;
                if (r.DateEnd.HasValue) ws.Cell(row, 5).Value = r.DateEnd.Value;
                if (hasOld && r.OldPrice.HasValue) ws.Cell(row, 6).Value = r.OldPrice.Value;
                row++;
            }
            ws.Column(4).Style.DateFormat.Format = "dd.mm.yyyy";
            ws.Column(5).Style.DateFormat.Format = "dd.mm.yyyy";
            // AdjustToContents на больших листах очень медленный — задаём ширину колонок явно
            ws.Column(1).Width = 60;
            ws.Column(2).Width = 40;
            ws.Column(3).Width = 12;
            ws.Column(4).Width = 12;
            ws.Column(5).Width = 12;
            if (hasOld) ws.Column(6).Width = 12;
            wb.SaveAs(path);
        }

        // Таблица для подбора настроек: сколько товаров проходит при разном проценте магазинов
        // и сколько из них с одной ценой (цена совпадает во всех магазинах) / с разбросом в допустимых пределах.
        private void LogCoverageTable(Scope scope, int needShops, Action<string> L)
        {
            int total = scope.ShopSet.Count;
            if (total <= 1 || scope.Candidates.Count == 0) return;

            decimal limit = opt.MaxSpreadPercent / 100m;
            var rows = new List<(int Cov, bool Equal, bool InSpread)>(scope.Candidates.Count);
            foreach (var g in scope.Candidates)
            {
                var prices = ScopePrices(g, scope);
                if (prices.Count == 0) continue;
                decimal min = prices.Min(), max = prices.Max();
                rows.Add((CountIn(g.Shops, scope.ShopSet), max == min, (max - min) / min <= limit));
            }

            var percents = new SortedSet<decimal>(new decimal[] { 100, 95, 90, 80, 70, 50, 30, 10, opt.ShopPercent });
            L($"Охват по магазинам (всего магазинов: {total}, товаров с ценой: {rows.Count}). " +
              $"Допустимая разница цен: {opt.MaxSpreadPercent}%:");
            foreach (var p in percents.Reverse())
            {
                int need = Math.Max(1, (int)Math.Ceiling(total * p / 100m));
                int pass = 0, eq = 0, sp = 0;
                foreach (var r in rows)
                {
                    if (r.Cov < need) continue;
                    pass++;
                    if (r.Equal) eq++;
                    if (r.InSpread) sp++;
                }
                string mark = p == opt.ShopPercent ? "  ◄ выбрано" : "";
                L($"   минимум {p,3:0.##}% ({need} из {total}): товаров {pass}, с одной ценой {eq}, " +
                  $"в пределах разброса {sp}{mark}");
            }
        }
    }
}
