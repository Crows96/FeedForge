using System.Text;
using System.Text.Json;

namespace FeedForge;

public sealed record ImageStats(int Total, int Reused, int Uploaded, int Failed, int Small, int Oversize);

/// <summary>
/// Картинки на S3: постоянная папка сети (<папка>/images/) и реестр _images.json (исходная ссылка → файл на S3, настройки сжатия).
/// Картинка с тем же адресом и теми же настройками сжатия повторно не скачивается, не сжимается и не загружается.
/// Новые: скачать → сжать (до заданного веса) → загрузить на S3 → записать в реестр.
/// </summary>
public sealed class ImagePipeline
{
    private sealed class Entry
    {
        public string Key { get; set; } = "";
        public string Sig { get; set; } = "";
        public long Size { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    private sealed class RegistryFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Entry> Images { get; set; } = new();
    }

    private static readonly HttpClient Http = SharedHttp.Client;

    private const long MaxDownloadBytes = 40L * 1024 * 1024;
    /// <summary>Минимальная короткая сторона картинки по правилам Едадил.</summary>
    public const int MinSide = 400;

    private readonly S3Client client;
    private readonly string bucket;
    private readonly string prefix;        // «папка/сеть/images/»
    private readonly string publicBase;
    private readonly RecodeOptions recode;
    private readonly int threads;
    private readonly IProgress<string> log;
    private readonly object gate = new();
    private RegistryFile? reg;
    private HashSet<string> usedKeys = new(StringComparer.Ordinal);

    public ImagePipeline(S3Client client, string bucket, string prefix, string publicBase,
                         RecodeOptions recode, int threads, IProgress<string> log)
    {
        this.client = client;
        this.bucket = bucket;
        this.prefix = prefix;
        this.publicBase = publicBase.TrimEnd('/');
        this.recode = recode;
        this.threads = Math.Clamp(threads, 1, 64);
        this.log = log;
    }

    private string RegistryKey => prefix + "_images.json";

    public string PublicUrl(string key) => publicBase + "/" + SigV4.EncodePath(key);

    private static string ContentType(RecodeFormat f) => f switch
    {
        RecodeFormat.Jpeg => "image/jpeg",
        RecodeFormat.WebP => "image/webp",
        _ => "image/png"
    };

    private static bool IsHttp(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- реестр

    private async Task LoadRegistryAsync(CancellationToken ct)
    {
        if (reg != null) return;
        var bytes = await client.GetBytesAsync(bucket, RegistryKey, ct);
        reg = new RegistryFile();
        if (bytes != null)
        {
            try { reg = JsonSerializer.Deserialize<RegistryFile>(bytes, JsonIo.Options) ?? new RegistryFile(); }
            catch { log.Report("Реестр картинок на S3 повреждён — начинаю его заново."); }
        }
        usedKeys = new HashSet<string>(reg.Images.Values.Select(e => e.Key), StringComparer.Ordinal);
    }

    private async Task SaveRegistryAsync(CancellationToken ct)
    {
        byte[] data;
        lock (gate) data = JsonSerializer.SerializeToUtf8Bytes(reg!, JsonIo.Options);
        await client.PutBytesAsync(bucket, RegistryKey, data, "application/json", ct);
    }

    /// <summary>Сколько картинок из списка ещё не лежит на S3 с текущими настройками сжатия (для предрасчёта).</summary>
    public async Task<int> CountPendingAsync(IReadOnlyCollection<string> urls, CancellationToken ct)
    {
        await LoadRegistryAsync(ct);
        return urls.Count(u => IsHttp(u) && !(reg!.Images.TryGetValue(u, out var e) && e.Sig == recode.Signature));
    }

    // ---------------------------------------------------------------- обработка

    public async Task<(Dictionary<string, string> Map, ImageStats Stats, HashSet<string> Small)> ProcessAsync(
        IReadOnlyCollection<string> urls, Action<double>? percent, CancellationToken ct)
    {
        await LoadRegistryAsync(ct);

        var map = new Dictionary<string, string>();
        var smallUrls = new HashSet<string>(StringComparer.Ordinal);
        var todo = new List<string>();
        int reused = 0;
        foreach (var u in urls)
        {
            if (!IsHttp(u)) continue;
            if (reg!.Images.TryGetValue(u, out var e) && e.Sig == recode.Signature)
            {
                map[u] = PublicUrl(e.Key);
                reused++;
                if (e.W > 0 && e.H > 0 && Math.Min(e.W, e.H) < MinSide) smallUrls.Add(u);
            }
            else todo.Add(u);
        }

        if (todo.Count > 0)
            log.Report($"Картинки: на S3 уже {reused}, нужно загрузить {todo.Count} (потоков {threads}).");

        int done = 0, uploaded = 0, failed = 0, small = 0, over = 0, logged = 0;
        using var sem = new SemaphoreSlim(threads);

        async Task OneAsync(string url)
        {
            await sem.WaitAsync(ct);
            try
            {
                byte[] src = await DownloadAsync(url, ct);
                var r = await Task.Run(() => ImageRecoder.Recode(src, recode), ct);
                if (Math.Min(r.Width, r.Height) < MinSide)
                {
                    Interlocked.Increment(ref small);
                    lock (gate) smallUrls.Add(url);
                }
                if (recode.MaxBytes > 0 && r.Data.Length > recode.MaxBytes) Interlocked.Increment(ref over);

                string key;
                lock (gate)
                {
                    key = NameFor(url);
                    usedKeys.Add(key);
                }
                await client.PutBytesAsync(bucket, key, r.Data, ContentType(recode.Format), ct);
                lock (gate)
                {
                    reg!.Images[url] = new Entry { Key = key, Sig = recode.Signature, Size = r.Data.Length, W = r.Width, H = r.Height };
                    map[url] = PublicUrl(key);
                }
                Interlocked.Increment(ref uploaded);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                if (Interlocked.Increment(ref logged) <= 10)
                    log.Report($"Картинка не обработана ({ex.Message}): {url}");
            }
            finally
            {
                sem.Release();
                percent?.Invoke(Interlocked.Increment(ref done) * 100.0 / Math.Max(1, todo.Count));
            }
        }

        try
        {
            await Task.WhenAll(todo.Select(OneAsync));
        }
        finally
        {
            // реестр сохраняем даже при остановке: уже загруженные картинки не будут загружаться повторно
            if (uploaded > 0) await SaveRegistryAsync(CancellationToken.None);
        }

        var stats = new ImageStats(urls.Count, reused, uploaded, failed, smallUrls.Count, over);
        log.Report($"Картинки: всего {stats.Total}, уже были на S3 {reused}, загружено {uploaded}, ошибок {failed}" +
                   (smallUrls.Count > 0 ? $", меньше {MinSide} px: {smallUrls.Count}" : "") +
                   (over > 0 ? $", тяжелее лимита: {over}" : ""));
        return (map, stats, smallUrls);
    }

    private static async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(90));
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength > MaxDownloadBytes)
                    throw new InvalidDataException("файл больше 40 МБ");
                return await resp.Content.ReadAsByteArrayAsync(cts.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or IOException or TaskCanceledException))
            {
                last = ex;
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
        throw new Exception(last?.Message ?? "не удалось скачать");
    }

    // Читаемое имя из адреса картинки; при совпадении с уже занятым добавляется короткий хэш адреса
    // (детерминированно: имя фиксируется в реестре и больше не пересчитывается).
    private string NameFor(string url)
    {
        string name = "image";
        try
        {
            var seg = new Uri(url).AbsolutePath.Split('/').LastOrDefault(x => x.Length > 0);
            if (!string.IsNullOrEmpty(seg)) name = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(seg));
        }
        catch { }

        var sb = new StringBuilder();
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ',' or '(' or ')' ? c : '_');
        name = sb.ToString().Trim('_', '.', ' ');
        if (name.Length == 0) name = "image";
        if (name.Length > 80) name = name[..80];

        string key = prefix + name + recode.Extension;
        if (!usedKeys.Contains(key)) return key;

        ulong h = 14695981039346656037UL;
        foreach (char ch in url) { h ^= ch; h *= 1099511628211UL; }
        return prefix + name + "_" + h.ToString("x16")[..6] + recode.Extension;
    }
}
