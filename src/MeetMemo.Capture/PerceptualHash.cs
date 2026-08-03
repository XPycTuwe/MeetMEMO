using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace MeetMemo.Capture;

/// <summary>
/// Разностный хеш кадра (dHash) для дедупликации автоснимков.
///
/// Считается по яркости соседних пикселей уменьшенной копии, поэтому устойчив к мелкому шуму
/// (курсор, мигающая каретка, антиалиасинг) и реагирует на смену слайда. Никакого распознавания
/// содержимого здесь нет и не должно быть: интерпретация изображений — задача Claude (ТЗ 9.2).
/// </summary>
[SupportedOSPlatform("windows")]
public static class PerceptualHash
{
    private const int HashWidth = 9;
    private const int HashHeight = 8;

    /// <summary>64-битный dHash кадра.</summary>
    public static ulong Compute(Bitmap source)
    {
        using var small = new Bitmap(HashWidth, HashHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, HashWidth, HashHeight);
        }

        var gray = new double[HashHeight, HashWidth];
        var data = small.LockBits(
            new Rectangle(0, 0, HashWidth, HashHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var scan = (byte*)data.Scan0;
                for (var y = 0; y < HashHeight; y++)
                {
                    var row = scan + (long)y * data.Stride;
                    for (var x = 0; x < HashWidth; x++)
                    {
                        var px = row + (long)x * 4;
                        // Веса по восприятию яркости: BGRA-порядок в памяти.
                        gray[y, x] = 0.114 * px[0] + 0.587 * px[1] + 0.299 * px[2];
                    }
                }
            }
        }
        finally
        {
            small.UnlockBits(data);
        }

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < HashHeight; y++)
        {
            for (var x = 0; x < HashWidth - 1; x++)
            {
                if (gray[y, x] > gray[y, x + 1]) hash |= 1UL << bit;
                bit++;
            }
        }

        return hash;
    }

    /// <summary>Число различающихся битов: 0 — кадры визуально идентичны.</summary>
    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
