namespace FeedForge;

/// <summary>
/// Один общий HttpClient для скачивания файлов по ссылкам и картинок для S3.
/// Клиент S3 (подписанные запросы) остаётся отдельным.
/// </summary>
internal static class SharedHttp
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        return client;
    }
}
