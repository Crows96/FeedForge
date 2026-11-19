using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FeedForge;

/// <summary>Результат лёгкой проверки источника: без скачивания тела файла.</summary>
public sealed record SourceProbe(DateTimeOffset? LastModified, long? Size, string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>Локальный путь к файлу источника и признак, что взят из кэша без скачивания.</summary>
public sealed record CachedSource(string Path, bool FromCache, DateTimeOffset? LastModified);

/// <summary>Источник после раскрытия S3-папок в отдельные файлы; Known — данные листинга (дата и размер) без лишних запросов.</summary>
public sealed record ExpandedSource(NetworkSource Source, SourceProbe? Known);

/// <summary>
/// Постоянный кэш исходных файлов: по папке на сеть (по slug) и manifest.json с ETag, датой изменения и размером.
/// Ссылки http(s): условный запрос (If-None-Match / If-Modified-Since). Файлы S3: сравниваются размер и дата изменения из листинга.
/// Локальные файлы не копируются, используются как есть.
/// </summary>
public sealed class SourceCache
{
    private static readonly HttpClient Http = SharedHttp.Client;

    private sealed class Entry
    {
        public string File { get; set; } = "";
        public string? ETag { get; set; }
        public DateTimeOffset? LastModified { get; set; }
        public long Size { get; set; }
        public DateTime FetchedUtc { get; set; }
    }

    private sealed class Manifest
    {
        public Dictionary<string, Entry> Entries { get; set; } = new();
    }

    private readonly string root;
    private readonly AppSettings settings;

    public SourceCache(string cacheRoot, AppSettings settings)
    {
        root = cacheRoot;
        this.settings = settings;
    }

    private string DirFor(string slug) => Path.Combine(root, slug);
    private string ManifestPath(string slug) => Path.Combine(DirFor(slug), "manifest.json");

    private Manifest LoadManifest(string slug)
    {
        try
        {
            string p = ManifestPath(slug);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(p), JsonIo.Options) ?? new Manifest();
        }
        catch { }
        return new Manifest();
    }

    private void SaveManifest(string slug, Manifest m)
    {
        try { JsonIo.WriteAtomic(ManifestPath(slug), m); } catch { }
    }

    // ---------------------------------------------------------------- S3

    private (S3Client Client, string Bucket, string Key) S3For(string source)
    {
        if (!S3Path.TryParse(source, out var bucket, out var key))
            throw new InvalidOperationException("Неверный адрес S3: " + source);
        var conn = settings.FindInput(bucket)
                   ?? throw new InvalidOperationException(
                       $"Для бакета «{bucket}» нет подключения. Добавьте его в Настройки → S3 (входящие).");
        return (conn.CreateClient(), bucket, key);
    }

    /// <summary>Раскрывает S3-папки (адрес оканчивается на «/») в список файлов .json/.xml/.yml; остальные источники остаются как есть.</summary>
    public async Task<List<ExpandedSource>> ExpandAsync(IEnumerable<NetworkSource> sources, CancellationToken ct)
    {
        var result = new List<ExpandedSource>();
        foreach (var s in sources)
        {
            string src = s.Source.Trim();
            if (!(S3Path.IsS3(src) && src.EndsWith('/')))
            {
                result.Add(new ExpandedSource(s, null));
                continue;
            }

            var (client, bucket, prefix) = S3For(src);
            var files = await client.ListFeedFilesAsync(bucket, prefix, ct);
            foreach (var f in files)
            {
                result.Add(new ExpandedSource(
                    new NetworkSource
                    {
                        Source = $"s3://{bucket}/{f.Key}",
                        Label = s.Label ?? Path.GetFileNameWithoutExtension(f.Key),
                        Region = s.Region
                    },
                    new SourceProbe(f.LastModified, f.Size, null)));
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- проверка без скачивания

    public async Task<SourceProbe> ProbeAsync(string source, CancellationToken ct)
    {
        source = source.Trim();
        try
        {
            if (S3Path.IsS3(source))
            {
                var (client, bucket, key) = S3For(source);
                var st = await client.StatAsync(bucket, key, ct);
                return st == null ? new SourceProbe(null, null, "файл не найден в S3") : new SourceProbe(st.LastModified, st.Size, null);
            }

            if (!Converter.IsUrl(source))
            {
                if (!File.Exists(source)) return new SourceProbe(null, null, "файл не найден");
                var fi = new FileInfo(source);
                return new SourceProbe(new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero), fi.Length, null);
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));

            using (var head = new HttpRequestMessage(HttpMethod.Head, source))
            using (var r = await Http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cts.Token))
            {
                if (r.IsSuccessStatusCode)
                    return new SourceProbe(r.Content.Headers.LastModified, r.Content.Headers.ContentLength, null);
            }

            // часть серверов не отвечает на HEAD: читаем только заголовки обычного GET
            using var get = new HttpRequestMessage(HttpMethod.Get, source);
            using var r2 = await Http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!r2.IsSuccessStatusCode) return new SourceProbe(null, null, $"HTTP {(int)r2.StatusCode}");
            return new SourceProbe(r2.Content.Headers.LastModified, r2.Content.Headers.ContentLength, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SourceProbe(null, null, ex.Message);
        }
    }

    // ---------------------------------------------------------------- получение файла

    /// <param name="known">Данные листинга S3 (дата и размер), если они уже есть: лишний запрос не делается.</param>
    public async Task<CachedSource> GetAsync(string slug, string source, bool forceRefresh,
                                             IProgress<string> log, CancellationToken ct, SourceProbe? known = null)
    {
        source = source.Trim();

        if (!S3Path.IsS3(source) && !Converter.IsUrl(source))
        {
            if (!File.Exists(source)) throw new FileNotFoundException("Файл не найден: " + source);
            return new CachedSource(source, false, new DateTimeOffset(File.GetLastWriteTimeUtc(source), TimeSpan.Zero));
        }

        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return S3Path.IsS3(source)
                    ? await FetchS3Async(slug, source, forceRefresh, log, ct, known)
                    : await FetchAsync(slug, source, forceRefresh, log, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                last = ex;
                if (attempt < 3)
                {
                    log.Report($"Не удалось скачать ({ex.Message}), повтор {attempt + 1} из 3...");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }
        }
        throw new Exception("Не удалось скачать файл: " + last?.Message);
    }

    private async Task<CachedSource> FetchS3Async(string slug, string source, bool forceRefresh,
                                                  IProgress<string> log, CancellationToken ct, SourceProbe? known)
    {
        var (client, bucket, key) = S3For(source);
        DateTimeOffset? modified = known?.LastModified;
        long? size = known?.Size;
        if (known == null || !known.Ok)
        {
            var st = await client.StatAsync(bucket, key, ct) ?? throw new FileNotFoundException("Файл не найден в S3: " + source);
            modified = st.LastModified;
            size = st.Size;
        }

        Directory.CreateDirectory(DirFor(slug));
        var man = LoadManifest(slug);
        man.Entries.TryGetValue(source, out var entry);
        string? cachedPath = entry != null ? Path.Combine(DirFor(slug), entry.File) : null;

        if (!forceRefresh && entry != null && cachedPath != null && File.Exists(cachedPath) &&
            entry.LastModified == modified && entry.Size == size)
        {
            log.Report("Файл в S3 не менялся, беру из кэша.");
            return new CachedSource(cachedPath, true, modified);
        }

        string fileName = entry?.File ?? MakeFileName(source);
        string target = Path.Combine(DirFor(slug), fileName);
        log.Report("Скачиваю из S3...");
        long lastLogged = 0, total = 0;
        await client.DownloadAsync(bucket, key, target, done =>
        {
            total = done;
            if (done - lastLogged >= 25L * 1024 * 1024)
            {
                lastLogged = done;
                log.Report($"Скачано {done / 1024.0 / 1024.0:F0} МБ...");
            }
        }, ct);

        log.Report($"Файл скачан: {total / 1024.0 / 1024.0:F1} МБ");
        man.Entries[source] = new Entry
        {
            File = fileName, LastModified = modified, Size = size ?? total, FetchedUtc = DateTime.UtcNow
        };
        SaveManifest(slug, man);
        return new CachedSource(target, false, modified);
    }

    private async Task<CachedSource> FetchAsync(string slug, string source, bool forceRefresh,
                                                IProgress<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(DirFor(slug));
        var man = LoadManifest(slug);
        man.Entries.TryGetValue(source, out var entry);
        string? cachedPath = entry != null ? Path.Combine(DirFor(slug), entry.File) : null;
        bool haveCached = cachedPath != null && File.Exists(cachedPath);

        using var req = new HttpRequestMessage(HttpMethod.Get, source);
        if (haveCached && !forceRefresh)
        {
            if (!string.IsNullOrEmpty(entry!.ETag)) req.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);
            else if (entry.LastModified is { } lm) req.Headers.IfModifiedSince = lm;
        }

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (resp.StatusCode == HttpStatusCode.NotModified && haveCached)
        {
            log.Report("Файл не менялся, беру из кэша.");
            return new CachedSource(cachedPath!, true, entry!.LastModified);
        }
        resp.EnsureSuccessStatusCode();

        string? etag = resp.Headers.ETag?.ToString();
        var modified = resp.Content.Headers.LastModified;
        long? length = resp.Content.Headers.ContentLength;

        // сервер не умеет условные запросы, но файл тот же (ETag или дата и размер совпали): тело не читаем
        if (haveCached && !forceRefresh && entry != null &&
            ((etag != null && etag == entry.ETag) ||
             (modified != null && modified == entry.LastModified && length != null && length == entry.Size)))
        {
            log.Report("Файл не менялся, беру из кэша.");
            return new CachedSource(cachedPath!, true, entry.LastModified);
        }

        string fileName = entry?.File ?? MakeFileName(source);
        string target = Path.Combine(DirFor(slug), fileName);
        string temp = target + ".part";

        log.Report("Скачиваю файл...");
        long total = 0, lastLogged = 0;
        try
        {
            using (var src = await resp.Content.ReadAsStreamAsync(ct))
            using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf.AsMemory(), ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    total += read;
                    if (total - lastLogged >= 25L * 1024 * 1024)
                    {
                        lastLogged = total;
                        log.Report($"Скачано {total / 1024.0 / 1024.0:F0} МБ...");
                    }
                }
            }
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }

        log.Report($"Файл скачан: {total / 1024.0 / 1024.0:F1} МБ");
        man.Entries[source] = new Entry
        {
            File = fileName, ETag = etag, LastModified = modified, Size = total, FetchedUtc = DateTime.UtcNow
        };
        SaveManifest(slug, man);
        return new CachedSource(target, false, modified);
    }

    private static string MakeFileName(string source)
    {
        string name = "feed";
        string ext = ".json";
        try
        {
            string path = S3Path.TryParse(source, out _, out var key) ? key : new Uri(source).AbsolutePath;
            var seg = path.Split('/').LastOrDefault(s => s.Length > 0);
            if (!string.IsNullOrEmpty(seg))
            {
                seg = Uri.UnescapeDataString(seg);
                name = Path.GetFileNameWithoutExtension(seg);
                string e = Path.GetExtension(seg).ToLowerInvariant();
                if (e is ".json" or ".xml" or ".yml") ext = e;
            }
        }
        catch { }

        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 40) name = name[..40];
        if (string.IsNullOrWhiteSpace(name)) name = "feed";

        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(source)))[..8].ToLowerInvariant();
        return $"{name}_{hash}{ext}";
    }

    // ---------------------------------------------------------------- обслуживание

    public void Clear(string slug)
    {
        try
        {
            if (Directory.Exists(DirFor(slug))) Directory.Delete(DirFor(slug), true);
        }
        catch { }
    }

    public long SizeBytes(string slug)
    {
        try
        {
            return Directory.Exists(DirFor(slug))
                ? new DirectoryInfo(DirFor(slug)).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }
}
