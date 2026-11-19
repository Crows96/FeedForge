using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace FeedForge;

// ===================== Подключение к S3 и шифрование секретного ключа =====================

/// <summary>Одно подключение к S3: сервер, бакет и ключи. Секретный ключ хранится зашифрованным (Windows DPAPI, для текущего пользователя).</summary>
public sealed class S3Connection
{
    public string Endpoint { get; set; } = "storage.yandexcloud.net";
    public string Region { get; set; } = "ru-central1";
    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretKeyEncrypted { get; set; } = "";

    /// <summary>Только для исходящего подключения: корневая папка программы в бакете (бакет/корень/сеть/...).</summary>
    public string RootFolder { get; set; } = "FeedForge";
    /// <summary>Только для исходящего: базовый адрес публичных ссылок, например https://msk.s3.wgos.org/edadeal.</summary>
    public string PublicBaseUrl { get; set; } = "";

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrEmpty(SecretKeyEncrypted) && !string.IsNullOrWhiteSpace(Bucket);

    public string GetSecret() => Secrets.Unprotect(SecretKeyEncrypted);
    public void SetSecret(string secret) => SecretKeyEncrypted = Secrets.Protect(secret);

    public S3Client CreateClient() => new(Endpoint, Region, AccessKeyId, GetSecret());
}

internal static class Secrets
{
    public static string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    /// <summary>Пустая строка, если ключ не расшифровывается (например, файл настроек перенесён на другой компьютер).</summary>
    public static string Unprotect(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException) { return ""; }
        catch (FormatException) { return ""; }
    }
}

// ===================== Адрес вида s3://бакет/папка/ =====================

public static class S3Path
{
    public static bool IsS3(string value) => value.TrimStart().StartsWith("s3://", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string value, out string bucket, out string key)
    {
        bucket = "";
        key = "";
        value = value.Trim();
        if (!value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase)) return false;
        string rest = value[5..];
        int slash = rest.IndexOf('/');
        bucket = slash < 0 ? rest : rest[..slash];
        key = slash < 0 ? "" : rest[(slash + 1)..];
        return bucket.Length > 0;
    }

    public static bool IsFeedKey(string key) =>
        key.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);
}

// ===================== Подпись запросов AWS Signature V4 =====================

internal static class SigV4
{
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Кодирование по RFC 3986: всё, кроме A-Z a-z 0-9 - _ . ~, превращается в %XX (UTF-8).</summary>
    public static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            bool unreserved = b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';
            if (unreserved) sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static string EncodePath(string path) => string.Join("/", path.Split('/').Select(Encode));

    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> query) =>
        string.Join("&", query
            .Select(kv => (Name: Encode(kv.Key), Value: Encode(kv.Value)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + p.Value));

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();

    /// <summary>Возвращает значение заголовка Authorization. Остальные заголовки (x-amz-date, x-amz-content-sha256) вызывающий ставит сам.</summary>
    public static string Sign(string method, string host, string canonicalPath, string canonicalQuery,
        IReadOnlyDictionary<string, string>? extraHeaders, string payloadHash, DateTime utcNow,
        string region, string service, string accessKey, string secretKey, out string amzDate)
    {
        amzDate = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = utcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };
        if (extraHeaders is not null)
            foreach (var kv in extraHeaders) headers[kv.Key.ToLowerInvariant()] = kv.Value.Trim();

        string canonicalHeaders = string.Concat(headers.Select(kv => kv.Key + ":" + kv.Value + "\n"));
        string signedHeaders = string.Join(";", headers.Keys);
        string canonicalRequest = string.Join("\n", method, canonicalPath, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);

        string scope = $"{date}/{region}/{service}/aws4_request";
        string stringToSign = "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n" +
                              Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        byte[] key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        string signature = Hex(Hmac(key, stringToSign));

        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }
}

// ===================== Клиент S3 (чтение: список и скачивание) =====================

public sealed record S3Object(string Key, long Size, DateTimeOffset? LastModified = null);

public sealed class S3Listing
{
    public List<S3Object> Files { get; } = new();
    /// <summary>Подпапки (прямые), с завершающим «/».</summary>
    public List<string> Folders { get; } = new();
}

public sealed class S3Exception : Exception
{
    public int Status { get; }
    public string Code { get; }

    public S3Exception(int status, string code, string message) : base(Compose(code, message))
    {
        Status = status;
        Code = code;
    }

    private static string Compose(string code, string message)
    {
        string hint = code switch
        {
            "AccessDenied" => "нет прав доступа (проверьте права ключа и имя бакета)",
            "SignatureDoesNotMatch" => "неверный секретный ключ или регион",
            "InvalidAccessKeyId" => "неверный идентификатор ключа",
            "NoSuchBucket" => "бакет не найден",
            "NoSuchKey" => "файл не найден",
            "RequestTimeTooSkewed" => "время на компьютере сильно отличается от серверного",
            "Timeout" => "сервер не ответил вовремя",
            _ => "",
        };
        string text = $"S3: {code}";
        if (hint.Length > 0) text += $" — {hint}";
        if (!string.IsNullOrWhiteSpace(message) && message != code) text += $" ({message})";
        return text;
    }
}

public sealed class S3Client
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly string _scheme;
    private readonly string _authority;
    private readonly string _region;
    private readonly string _accessKey;
    private readonly string _secretKey;

    private static HttpClient CreateHttp()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FeedForge/1.0");
        return client;
    }

    public S3Client(string endpoint, string region, string accessKey, string secretKey)
    {
        endpoint = endpoint.Trim().TrimEnd('/');
        string scheme = "https";
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) { scheme = "http"; endpoint = endpoint[7..]; }
        else if (endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) endpoint = endpoint[8..];

        _scheme = scheme;
        _authority = new Uri($"{scheme}://{endpoint}").Authority; // без порта по умолчанию, как отправит HttpClient
        _region = string.IsNullOrWhiteSpace(region) ? "ru-central1" : region.Trim();
        _accessKey = accessKey.Trim();
        _secretKey = secretKey;
    }

    private Task<HttpResponseMessage> SendAsync(string bucket, string key,
        IReadOnlyList<KeyValuePair<string, string>> query, HttpCompletionOption option, CancellationToken ct)
    {
        string path = "/" + bucket + (key.Length > 0 ? "/" + key : "");
        string canonicalPath = SigV4.EncodePath(path);
        string canonicalQuery = SigV4.CanonicalQuery(query);
        string auth = SigV4.Sign("GET", _authority, canonicalPath, canonicalQuery, null, SigV4.EmptyPayloadHash,
            DateTime.UtcNow, _region, "s3", _accessKey, _secretKey, out string amzDate);

        string url = $"{_scheme}://{_authority}{canonicalPath}" + (canonicalQuery.Length > 0 ? "?" + canonicalQuery : "");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", SigV4.EmptyPayloadHash);
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        return Http.SendAsync(req, option, ct);
    }

    private static S3Exception ErrorFrom(HttpStatusCode status, string body)
    {
        string code = status.ToString(), message = "";
        try
        {
            var root = XDocument.Parse(body).Root;
            code = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Code")?.Value ?? code;
            message = root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value ?? "";
        }
        catch { /* ответ не в формате XML — оставляем код статуса */ }
        return new S3Exception((int)status, code, message);
    }

    private static string? Val(XElement e, string name) =>
        e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;

    /// <summary>Прямое содержимое «папки» (префикса): файлы и подпапки. Листинг постраничный, страницы собираются автоматически.</summary>
    public async Task<S3Listing> ListAsync(string bucket, string prefix, CancellationToken ct)
    {
        var result = new S3Listing();
        string? token = null;
        do
        {
            var query = new List<KeyValuePair<string, string>>
            {
                new("list-type", "2"),
                new("delimiter", "/"),
            };
            if (prefix.Length > 0) query.Add(new("prefix", prefix));
            if (token is not null) query.Add(new("continuation-token", token));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            string xml;
            try
            {
                using var resp = await SendAsync(bucket, "", query, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                xml = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw ErrorFrom(resp.StatusCode, xml);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new S3Exception(0, "Timeout", "Сервер не ответил за 60 секунд");
            }
            catch (HttpRequestException ex)
            {
                throw new S3Exception(0, "Network", "Не удалось подключиться: " + ex.Message);
            }

            var root = XDocument.Parse(xml).Root!;
            foreach (var c in root.Elements().Where(e => e.Name.LocalName == "Contents"))
            {
                string key = Val(c, "Key") ?? "";
                long.TryParse(Val(c, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size);
                if (key.Length == 0 || key == prefix || key.EndsWith('/')) continue; // «маркер папки»
                DateTimeOffset? modified = DateTimeOffset.TryParse(Val(c, "LastModified"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var lm) ? lm : null;
                result.Files.Add(new S3Object(key, size, modified));
            }

            foreach (var p in root.Elements().Where(e => e.Name.LocalName == "CommonPrefixes"))
            {
                var v = Val(p, "Prefix");
                if (!string.IsNullOrEmpty(v)) result.Folders.Add(v);
            }

            bool truncated = string.Equals(Val(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase);
            token = truncated ? Val(root, "NextContinuationToken") : null;
        }
        while (token is not null);

        return result;
    }

    /// <summary>Размер и дата изменения одного файла (через листинг по точному ключу). null — файла нет.</summary>
    public async Task<S3Object?> StatAsync(string bucket, string key, CancellationToken ct)
    {
        var listing = await ListAsync(bucket, key, ct).ConfigureAwait(false);
        return listing.Files.FirstOrDefault(f => f.Key == key);
    }

    /// <summary>Файлы .json / .xml / .yml из S3-папки (только прямые, без вложенных папок), с датой изменения из листинга.</summary>
    public async Task<List<S3Object>> ListFeedFilesAsync(string bucket, string prefix, CancellationToken ct)
    {
        var listing = await ListAsync(bucket, prefix, ct).ConfigureAwait(false);
        return listing.Files.Where(f => S3Path.IsFeedKey(f.Key)).OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    // ---------------- запись и чтение небольших объектов (картинки, JSON, реестр) ----------------

    private async Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string bucket, string key, HttpContent? content,
        string payloadHash, string? contentType, HttpCompletionOption option, CancellationToken ct)
    {
        string canonicalPath = SigV4.EncodePath("/" + bucket + (key.Length > 0 ? "/" + key : ""));
        var extra = contentType != null ? new Dictionary<string, string> { ["content-type"] = contentType } : null;
        string auth = SigV4.Sign(method.Method, _authority, canonicalPath, "", extra, payloadHash,
            DateTime.UtcNow, _region, "s3", _accessKey, _secretKey, out string amzDate);

        var req = new HttpRequestMessage(method, $"{_scheme}://{_authority}{canonicalPath}");
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        req.Headers.TryAddWithoutValidation("Authorization", auth);
        if (content != null)
        {
            if (contentType != null) content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            req.Content = content;
        }
        return await Http.SendAsync(req, option, ct).ConfigureAwait(false);
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException || ex is S3Exception { Status: >= 500 };

    /// <summary>Загружает объект из памяти (картинка, реестр). Перезаписывает существующий. До трёх попыток.</summary>
    public async Task PutBytesAsync(string bucket, string key, byte[] data, string contentType, CancellationToken ct)
    {
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await SendSignedAsync(HttpMethod.Put, bucket, key, new ByteArrayContent(data), hash,
                    contentType, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw ErrorFrom(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return;
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Загружает файл с диска потоком (подпись считается по содержимому). До трёх попыток.</summary>
    public async Task PutFileAsync(string bucket, string key, string path, string contentType, CancellationToken ct)
    {
        string hash;
        await using (var hs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(hs, ct).ConfigureAwait(false)).ToLowerInvariant();

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                using var resp = await SendSignedAsync(HttpMethod.Put, bucket, key, new StreamContent(fs), hash,
                    contentType, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw ErrorFrom(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                return;
            }
            catch (Exception ex) when (attempt < 3 && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Читает небольшой объект целиком; null — объекта нет.</summary>
    public async Task<byte[]?> GetBytesAsync(string bucket, string key, CancellationToken ct)
    {
        using var resp = await SendAsync(bucket, key, Array.Empty<KeyValuePair<string, string>>(),
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
            throw ErrorFrom(resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Скачивает файл потоком. До трёх попыток при обрывах связи и ошибках сервера 5xx.</summary>
    public async Task DownloadAsync(string bucket, string key, string target, Action<long>? onProgress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + ".part";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await SendAsync(bucket, key, Array.Empty<KeyValuePair<string, string>>(),
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw ErrorFrom(resp.StatusCode, body);
                }

                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buf = new byte[1 << 16];
                    long done = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        done += n;
                        onProgress?.Invoke(done);
                    }
                }

                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 3 && (ex is HttpRequestException or IOException || ex is S3Exception { Status: >= 500 }))
            {
                TryDelete(temp);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* не критично */ }
    }
}
