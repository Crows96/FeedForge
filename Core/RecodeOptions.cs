namespace FeedForge;

public enum RecodeFormat { Jpeg, WebP, Png }

/// <summary>Настройки сжатия картинок при скачивании.</summary>
public sealed record RecodeOptions(bool Enabled, RecodeFormat Format, int MaxSide, int Quality, int MaxBytes = 0)
{
    /// <summary>Значения по умолчанию: JPEG, до 1000 px по длинной стороне, качество 85.</summary>
    public static RecodeOptions Default => new(true, RecodeFormat.Jpeg, 1000, 85, 1_000_000);

    /// <summary>Картинки скачиваются как есть.</summary>
    public static RecodeOptions Off => new(false, RecodeFormat.Jpeg, 0, 0);

    public string Extension => Format switch
    {
        RecodeFormat.Jpeg => ".jpg",
        RecodeFormat.WebP => ".webp",
        _ => ".png"
    };

    public string FormatName => Format switch
    {
        RecodeFormat.Jpeg => "JPEG",
        RecodeFormat.WebP => "WebP",
        _ => "PNG"
    };

    // Входит в имя файла в кэше: при смене настроек старые файлы кэша не подхватываются
    public string Signature => Enabled ? $"{Format}-{MaxSide}-{Quality}" + (MaxBytes > 0 ? $"-{MaxBytes}" : "") : "orig";

    public string Describe()
    {
        if (!Enabled) return "выключено (картинки как есть)";
        string side = MaxSide > 0 ? $"до {MaxSide} px" : "без уменьшения размера";
        string quality = Format == RecodeFormat.Png ? "" : $", качество {Quality}";
        string weight = MaxBytes > 0 ? $", вес до {MaxBytes / 1000} КБ" : "";
        return $"{FormatName}, {side}{quality}{weight}";
    }
}
