using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MeetMemo.Capture;

/// <summary>
/// Запись снимка в PNG с уменьшенной палитрой.
///
/// Полноцветный снимок экрана 4K занимает 3–6 МБ, и пакет встречи распухает на пустом
/// месте: на слайдах и в интерфейсах реальных цветов десятки, а не миллионы. Палитра
/// строится под каждое изображение отдельно, поэтому текст остаётся резким —
/// в отличие от JPEG, который размывает буквы.
/// </summary>
public static class ScreenshotWriter
{
    /// <summary>
    /// Сколько цветов оставлять. На 64 снимок неотличим от полноцветного даже там, где
    /// есть градиенты, а файл выходит в семь раз меньше исходного.
    /// </summary>
    public const int DefaultColors = 64;

    /// <summary>
    /// Сохраняет снимок с палитрой из <paramref name="colors"/> цветов. Если ужать
    /// не удалось или файл получился больше исходного, пишется обычный PNG:
    /// снимок встречи важнее экономии места.
    /// </summary>
    public static void Save(Bitmap bitmap, string path, int colors = DefaultColors)
    {
        if (colors <= 0)
        {
            bitmap.Save(path, ImageFormat.Png);
            return;
        }

        try
        {
            var source = ToBitmapSource(bitmap);

            // Палитра строится по самому изображению: у скриншота свои несколько
            // десятков оттенков, и фиксированная палитра дала бы грязь на градиентах.
            var palette = new BitmapPalette(source, colors);
            var indexed = Quantize(source, palette);

            var encoder = new PngBitmapEncoder();
            encoder.Interlace = PngInterlaceOption.Off;
            encoder.Frames.Add(BitmapFrame.Create(indexed));

            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception)
        {
            // Резервный путь: обычный PNG без палитры.
            bitmap.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>
    /// Раскладывает изображение по палитре, заменяя каждый пиксель ближайшим цветом.
    ///
    /// Штатное преобразование WPF подмешивает дизеринг — рассыпает границы цветов мелкими
    /// точками. На фотографии это скрывает переходы, а на снимке экрана только вредит:
    /// ровная заливка превращается в шум, который PNG уже не может сжать. Без дизеринга
    /// однотонные области остаются однотонными, и файл выходит в разы меньше.
    ///
    /// Найденные соответствия кешируются: на снимке экрана уникальных цветов немного,
    /// и почти каждый пиксель попадает в готовый ответ.
    /// </summary>
    private static BitmapSource Quantize(BitmapSource source, BitmapPalette palette)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        var colors = palette.Colors;
        var reds = new int[colors.Count];
        var greens = new int[colors.Count];
        var blues = new int[colors.Count];
        for (var i = 0; i < colors.Count; i++)
        {
            reds[i] = colors[i].R;
            greens[i] = colors[i].G;
            blues[i] = colors[i].B;
        }

        var indexes = new byte[width * height];
        var cache = new Dictionary<int, byte>();

        for (int p = 0, q = 0; q < indexes.Length; p += 4, q++)
        {
            int b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
            var key = (r << 16) | (g << 8) | b;

            if (!cache.TryGetValue(key, out var nearest))
            {
                var bestDistance = int.MaxValue;
                for (var i = 0; i < reds.Length; i++)
                {
                    var dr = r - reds[i];
                    var dg = g - greens[i];
                    var db = b - blues[i];
                    var distance = dr * dr + dg * dg + db * db;
                    if (distance >= bestDistance) continue;

                    bestDistance = distance;
                    nearest = (byte)i;
                }

                cache[key] = nearest;
            }

            indexes[q] = nearest;
        }

        return BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Indexed8, palette, indexes, width);
    }

    /// <summary>
    /// Переносит снимок из GDI+ в WPF. Через поток, а не через дескриптор HBITMAP:
    /// дескриптор пришлось бы освобождать вручную, и любая ошибка по дороге давала бы
    /// утечку памяти на каждом снимке встречи.
    /// </summary>
    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Bmp);
        buffer.Position = 0;

        var decoder = BitmapDecoder.Create(
            buffer, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        return decoder.Frames[0];
    }
}
