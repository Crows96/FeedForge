
namespace FeedForge;

// Картинки для Excel: скачивание, кэш, сжатие.
public static partial class Converter
{
    private sealed partial class Runner
    {
        // ---------- Картинки ----------
        private string CachePath(string displayName) => Path.Combine(cacheDir, nameToCache[displayName]);

        // Картинка считается готовой, если файл скачан и не пустой
        private bool HasImage(string url)
        {
            if (!urlToName.TryGetValue(url, out var n)) return false;
            var fi = new FileInfo(CachePath(n));
            return fi.Exists && fi.Length > 0;
        }

        // Назначает имена файлов и скачивает (и при необходимости сжимает) картинки, которые ещё не обрабатывались
        private async Task EnsureImagesAsync(IEnumerable<string?> urls, Action<int, int>? onProgress)
        {
            var todo = new Dictionary<string, string>();
            foreach (var url in urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct())
            {
                if (urlToName.ContainsKey(url!)) continue;      // уже обработана раньше

                string name = MakeFileName(url!);
                // При сжатии формат меняется, поэтому и расширение в имени (и в Excel) берём новое
                if (opt.Recode.Enabled) name = Path.GetFileNameWithoutExtension(name) + opt.Recode.Extension;
                string unique = name;
                int k = 1;
                while (!usedNames.Add(unique))
                    unique = $"{Path.GetFileNameWithoutExtension(name)}_{k++}{Path.GetExtension(name)}";

                urlToName[url!] = unique;
                nameToCache[unique] = CacheName(url!, unique, opt.Recode.Signature);
                todo[url!] = unique;
            }
            if (todo.Count == 0) return;
            if (onProgress != null) log.Report($"Картинок к скачиванию: {todo.Count}");

            int done = 0;
            int total = todo.Count;
            await Parallel.ForEachAsync(todo,
                new ParallelOptions { MaxDegreeOfParallelism = downloadThreads, CancellationToken = ct },
                async (pair, token) =>
                {
                    string path = CachePath(pair.Value);
                    if (!(File.Exists(path) && new FileInfo(path).Length > 0))   // уже скачано — пропускаем
                    {
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            try
                            {
                                await FetchImageAsync(pair.Key, path, token);
                                break;
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                throw;                       // «Стоп» — не считаем ошибкой скачивания
                            }
                            catch (InvalidDataException ex)
                            {
                                missing.Add($"{pair.Key} -> {ex.Message}");   // не картинка — повторять бессмысленно
                                break;
                            }
                            catch (HttpRequestException ex) when (
                                ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                            {
                                missing.Add($"{pair.Key} -> файла нет на сервере ({(int)ex.StatusCode!.Value})");
                                break;                                        // 404/410 — повторы и докачка не помогут
                            }
                            catch (Exception) when (attempt < 3)
                            {
                                await Task.Delay(500 * attempt, token);
                            }
                            catch (Exception ex)
                            {
                                failed.Add($"{pair.Key} -> {ex.Message}");
                            }
                        }
                    }
                    int d = Interlocked.Increment(ref done);
                    onProgress?.Invoke(d, total);
                });
        }

        // Скачивает одну картинку в кэш. Пишет во временный файл и переименовывает после успеха,
        // поэтому недокачанный файл никогда не принимается за готовый.
        private async Task FetchImageAsync(string url, string path, CancellationToken token)
        {
            string tmp = path + ".part";
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                resp.EnsureSuccessStatusCode();

                if (!opt.Recode.Enabled)
                {
                    // Без сжатия: поток прямо на диск, картинка целиком в памяти не держится
                    await using (var src = await resp.Content.ReadAsStreamAsync(token))
                    await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        await src.CopyToAsync(dst, 81920, token);
                }
                else
                {
                    byte[] bytes = await resp.Content.ReadAsByteArrayAsync(token);

                    // Сжатие — самая тяжёлая для процессора часть, одновременно их идёт ограниченное число
                    byte[] result;
                    await encodeGate.WaitAsync(token);
                    try { result = ImageRecoder.Recode(bytes, opt.Recode).Data; }
                    finally { encodeGate.Release(); }

                    await File.WriteAllBytesAsync(tmp, result, token);
                    Interlocked.Add(ref bytesIn, bytes.Length);
                    Interlocked.Add(ref bytesOut, result.Length);
                }

                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }
}
