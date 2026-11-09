using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace BatteryInfo
{
    /// <summary>Малює іконку застосунку (для exe) і зберігає її як .ico. Запуск: BatteryInfo.exe --icon app.ico</summary>
    static class AppIcon
    {
        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void Draw(Graphics g, int s)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.ScaleTransform(s / 256f, s / 256f);

            using (var p = Round(new RectangleF(8, 8, 240, 240), 54))
            using (var br = new SolidBrush(Color.FromArgb(30, 41, 59)))
                g.FillPath(br, p);

            Color light = Color.FromArgb(226, 232, 240);
            using (var p = Round(new RectangleF(34, 74, 172, 108), 20))
            using (var pen = new Pen(light, 14f))
                g.DrawPath(pen, p);
            using (var p = Round(new RectangleF(210, 108, 18, 40), 6))
            using (var br = new SolidBrush(light))
                g.FillPath(br, p);

            using (var br = new SolidBrush(Color.FromArgb(34, 197, 94)))
                for (int i = 0; i < 3; i++)
                    using (var p = Round(new RectangleF(54 + i * 49, 94, 39, 68), 8))
                        g.FillPath(br, p);
        }

        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 32, 48, 256 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                using (var ms = new MemoryStream())
                {
                    Draw(g, s);
                    bmp.Save(ms, ImageFormat.Png);
                    pngs.Add(ms.ToArray());
                }

            using (var bw = new BinaryWriter(File.Create(path)))
            {
                bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    bw.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    bw.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((short)1); bw.Write((short)32);
                    bw.Write(pngs[i].Length); bw.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] b in pngs) bw.Write(b);
            }
        }
    }
}
