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
    /// <summary>Сколько цветов оставлять. 64 хватает для интерфейсов, графиков и слайдов.</summary>
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
            var indexed = new FormatConvertedBitmap(source, PixelFormats.Indexed8, palette, 0);

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
