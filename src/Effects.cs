// =================================================================================================
// Effects.cs: pixel effects: the glow in an app icon's own shape, the rim that traces its edge, and the
// soft bloom around bubbles. Pure functions on bitmaps; IconSet and StripWindow cache the results.
// =================================================================================================

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DeskHud
{
    /// Pixel effects for app icons: a glow in the icon's own shape and colours, and a rim that traces
    /// its edge. Each is computed once per icon and cached by IconCache.
    static class Effects
    {
        /// The icon blurred and brightened, `pad` pixels bigger on every side, to draw behind it.
        public static Bitmap Halo(Bitmap icon, int pad)
        {
            int w = icon.Width + 2 * pad, h = icon.Height + 2 * pad;
            float[][] channels = Load(icon, pad, w, h);
            var scratch = new float[Math.Max(w, h)];
            int radius = Math.Max(1, pad / 2);
            // Three box blurs come close to a gaussian.
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (float[] channel in channels)
                {
                    Blur(channel, scratch, w, h, radius, true);
                    Blur(channel, scratch, w, h, radius, false);
                }
            }

            // Blurring spreads the light thin; lift it back so the glow reads at a glance.
            const float lift = 1.8f;
            float[] a = channels[0], r = channels[1], g = channels[2], b = channels[3];
            var pixels = new int[w * h];
            for (int i = 0; i < pixels.Length; i++)
            {
                float alpha = Math.Min(255, a[i] * lift);
                float scale = a[i] > 0 ? alpha / a[i] : 0;
                pixels[i] = Pack(alpha, Math.Min(alpha, r[i] * scale), Math.Min(alpha, g[i] * scale), Math.Min(alpha, b[i] * scale));
            }
            return Store(pixels, w, h);
        }

        /// The icon's silhouette grown by a couple of pixels and filled with `colour`. Drawn under the
        /// icon, only the outer edge shows, so the outline follows the app's shape.
        public static Bitmap Rim(Bitmap icon, int pad, Color colour)
        {
            int w = icon.Width + 2 * pad, h = icon.Height + 2 * pad;
            float[] a = Load(icon, pad, w, h)[0];
            int grow = Math.Max(1, (int)Math.Round(pad / 3.5));
            var pixels = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float alpha = 0;
                    for (int dy = -grow; dy <= grow; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= h) continue;
                        for (int dx = -grow; dx <= grow; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= w || dx * dx + dy * dy > grow * grow + 1) continue;
                            alpha = Math.Max(alpha, a[yy * w + xx]);
                        }
                    }
                    pixels[y * w + x] = Pack(alpha, colour.R * alpha / 255, colour.G * alpha / 255, colour.B * alpha / 255);
                }
            }
            return Store(pixels, w, h);
        }

        /// Soft white light around a pill `height` pixels tall, reaching `reach` pixels out. The pill is only
        /// two pixels longer than a circle, so the column at the centre is a flat slice of the straight part:
        /// stretching it (StripWindow.Glow) fits the light to a bubble of any width.
        public static Bitmap PillBloom(int height, int reach)
        {
            int w = 2 * reach + height + 2, h = 2 * reach + height;
            float radius = height / 2f, cy = reach + radius, x1 = reach + radius, x2 = x1 + 2;
            var a = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float dx = px < x1 ? px - x1 : px > x2 ? px - x2 : 0, dy = py - cy;
                    float inside = radius + 0.5f - (float)Math.Sqrt(dx * dx + dy * dy);
                    a[y * w + x] = Math.Max(0, Math.Min(1, inside)) * 255;
                }
            }
            var scratch = new float[Math.Max(w, h)];
            int blur = Math.Max(1, reach / 2);
            for (int pass = 0; pass < 3; pass++)
            {
                Blur(a, scratch, w, h, blur, true);
                Blur(a, scratch, w, h, blur, false);
            }
            var pixels = new int[w * h];
            for (int i = 0; i < pixels.Length; i++)
            {
                float alpha = Math.Min(255, a[i]);
                pixels[i] = Pack(alpha, alpha, alpha, alpha);
            }
            return Store(pixels, w, h);
        }

        /// Premultiplied alpha, red, green and blue as floats, with the icon centred in a padded canvas.
        static float[][] Load(Bitmap icon, int pad, int w, int h)
        {
            var channels = new[] { new float[w * h], new float[w * h], new float[w * h], new float[w * h] };
            var source = new int[icon.Width * icon.Height];
            var data = icon.LockBits(new Rectangle(0, 0, icon.Width, icon.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            Marshal.Copy(data.Scan0, source, 0, source.Length);
            icon.UnlockBits(data);

            for (int y = 0; y < icon.Height; y++)
            {
                for (int x = 0; x < icon.Width; x++)
                {
                    int p = source[y * icon.Width + x];
                    int i = (y + pad) * w + x + pad;
                    channels[0][i] = (p >> 24) & 0xFF;
                    channels[1][i] = (p >> 16) & 0xFF;
                    channels[2][i] = (p >> 8) & 0xFF;
                    channels[3][i] = p & 0xFF;
                }
            }
            return channels;
        }

        /// One box blur along rows or columns, treating everything outside the canvas as transparent.
        static void Blur(float[] channel, float[] scratch, int w, int h, int radius, bool rows)
        {
            int length = rows ? w : h, lines = rows ? h : w, step = rows ? 1 : w;
            float norm = 1f / (2 * radius + 1);
            for (int line = 0; line < lines; line++)
            {
                int start = rows ? line * w : line;
                float sum = 0;
                for (int k = 0; k <= radius && k < length; k++) sum += channel[start + k * step];
                for (int k = 0; k < length; k++)
                {
                    scratch[k] = sum * norm;
                    int add = k + radius + 1, remove = k - radius;
                    if (add < length) sum += channel[start + add * step];
                    if (remove >= 0) sum -= channel[start + remove * step];
                }
                for (int k = 0; k < length; k++) channel[start + k * step] = scratch[k];
            }
        }

        static Bitmap Store(int[] pixels, int w, int h)
        {
            var bitmap = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);
            return bitmap;
        }

        static int Pack(float a, float r, float g, float b)
        {
            return ((int)a << 24) | ((int)r << 16) | ((int)g << 8) | (int)b;
        }
    }
}
