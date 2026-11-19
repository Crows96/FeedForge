using SkiaSharp;

namespace FeedForge;

public sealed record RecodeResult(byte[] Data, int SrcWidth, int SrcHeight, int Width, int Height);

/// <summary>
/// Перекодирование картинки через SkiaSharp (нативная библиотека Skia): быстрее и экономнее по памяти,
/// чем System.Drawing, потокобезопасно. Уменьшение и заливка фона делаются за один проход.
/// </summary>
public static class ImageRecoder
{
    public static RecodeResult Recode(byte[] source, RecodeOptions o)
    {
        using var src = SKBitmap.Decode(source);
        if (src == null || src.Width <= 0 || src.Height <= 0)
            throw new InvalidDataException("не удалось прочитать картинку (возможно, это не изображение)");

        // Размер: только уменьшаем, меньшие картинки не растягиваем
        int w = src.Width, h = src.Height;
        int longest = Math.Max(w, h);
        if (o.MaxSide > 0 && longest > o.MaxSide)
        {
            double k = (double)o.MaxSide / longest;
            w = Math.Max(1, (int)Math.Round(w * k));
            h = Math.Max(1, (int)Math.Round(h * k));
        }

        // Подгонка под максимальный вес: сначала снижаем качество (JPEG/WebP), затем уменьшаем размер,
        // но не ниже 400 px по короткой стороне (минимум для Едадил). Без MaxBytes — один проход как раньше.
        int quality = Math.Clamp(o.Quality, 1, 100);
        var qualities = new List<int> { quality };
        double[] scales = { 1.0 };
        if (o.MaxBytes > 0)
        {
            if (o.Format != RecodeFormat.Png)
                for (int q = quality - 10; q >= 45; q -= 10) qualities.Add(q);
            scales = new[] { 1.0, 0.85, 0.7, 0.55 };
        }

        byte[]? result = null;
        int rw = w, rh = h;
        foreach (double k in scales)
        {
            int cw = Math.Max(1, (int)Math.Round(w * k)), ch = Math.Max(1, (int)Math.Round(h * k));
            if (k < 1.0 && Math.Min(cw, ch) < 400) break;
            foreach (int q in qualities)
            {
                result = Encode(src, cw, ch, q, o);
                rw = cw;
                rh = ch;
                if (o.MaxBytes <= 0 || result.Length <= o.MaxBytes)
                    return new RecodeResult(result, src.Width, src.Height, rw, rh);
            }
        }
        return new RecodeResult(result!, src.Width, src.Height, rw, rh);   // не удалось уложиться в вес: вернули самый лёгкий вариант
    }

    private static byte[] Encode(SKBitmap src, int w, int h, int quality, RecodeOptions o)
    {
        // Для JPEG и WebP прозрачность заливаем белым, для PNG сохраняем
        bool keepAlpha = o.Format == RecodeFormat.Png;
        var info = new SKImageInfo(w, h, SKImageInfo.PlatformColorType,
                                   keepAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque);

        using var dst = new SKBitmap(info);
        using (var canvas = new SKCanvas(dst))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            canvas.Clear(keepAlpha ? SKColors.Transparent : SKColors.White);
            canvas.DrawBitmap(src, new SKRect(0, 0, w, h), paint);
        }

        var format = o.Format switch
        {
            RecodeFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            RecodeFormat.WebP => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };

        using var image = SKImage.FromBitmap(dst);
        using var data = image.Encode(format, Math.Clamp(quality, 1, 100));
        if (data == null)
            throw new InvalidDataException("не удалось закодировать картинку");
        return data.ToArray();
    }
}
