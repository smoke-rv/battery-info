using System;
using System.Threading;
using System.Windows.Forms;

namespace BatteryInfo
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        public static string LogPath;
        public static readonly System.Text.StringBuilder Trace = new System.Text.StringBuilder();
        public static void Log(string s)
        {
            Trace.AppendLine(s);
            if (LogPath == null) return;
            try { System.IO.File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n"); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // --dump: вивести виявлені пристрої в консоль/файл і вийти (для діагностики)
            if (args.Length > 0 && args[0] == "--dump")
            {
                var sb = new System.Text.StringBuilder();
                LogPath = (args.Length > 1 ? args[1] : "dump.txt") + ".log";
                Log("start");
                foreach (var d in BluetoothBattery.Read())
                    sb.AppendLine("BT  " + d.Name + " [" + d.Kind + "] " + d.Percent + "%  " + d.Key);
                foreach (var d in LogitechBattery.Read())
                    sb.AppendLine("LG  " + d.Name + " [" + d.Kind + "] " + d.Percent + "% charging=" + d.Charging + " stale=" + d.Stale);
                if (LogitechBattery.LastError != null) sb.AppendLine("LG error: " + LogitechBattery.LastError);
                string path = args.Length > 1 ? args[1] : "dump.txt";
                System.IO.File.WriteAllText(path, sb.ToString());
                return;
            }

            // --loop <файл>: 40 опитувань з інтервалом 3 с, пише сирі HID++ відповіді при відхиленні
            if (args.Length > 1 && args[0] == "--loop")
            {
                var o = new System.Text.StringBuilder();
                for (int i = 0; i < 40; i++)
                {
                    Trace.Length = 0;
                    var list = LogitechBattery.Read();
                    string line = i + ": ";
                    foreach (var d in list) line += d.Key + " " + d.Name + " " + d.Percent + "% stale=" + d.Stale + "; ";
                    o.AppendLine(line);
                    bool odd = list.Count != 1 || list[0].Percent != 55;
                    if (odd) o.AppendLine(Trace.ToString());
                    System.IO.File.WriteAllText(args[1], o.ToString());
                    Thread.Sleep(3000);
                }
                return;
            }

            if (args.Length > 1 && args[0] == "--icon")
            {
                AppIcon.WriteIco(args[1]);
                return;
            }

            // --preview <png>: лист з прикладами іконок (для перегляду дизайну)
            if (args.Length > 1 && args[0] == "--preview")
            {
                int[] vals = { 100, 55, 25, 9, 5 };
                int[] sizes = { 16 };
                using (var sheet = new System.Drawing.Bitmap(vals.Length * 100, 4 * 90))
                using (var g = System.Drawing.Graphics.FromImage(sheet))
                {
                    g.Clear(System.Drawing.Color.FromArgb(32, 32, 32));
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    int row = 0;
                    foreach (int sz in sizes)
                        for (int k = 0; k < 4; k++, row++)
                            for (int i = 0; i < vals.Length; i++)
                            {
                                var d = new DeviceBattery { Kind = (DeviceKind)k, Percent = vals[i], Charging = i == 4 };
                                using (var ic = TrayApp.MakeIcon(d, sz))
                                using (var bmp = ic.ToBitmap())
                                    g.DrawImage(bmp, i * 100 + 10, row * 90 + 5, sz * 4, sz * 4); // збільшено ×4
                            }
                    sheet.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                }
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "Local\\BatteryInfoTray", out created))
            {
                if (!created) return;
                // DPI-aware, щоб іконка малювалась у реальному розмірі трею (16/20/24 px) і не розмивалась
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
