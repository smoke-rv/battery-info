using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BatteryInfo
{
    public class TrayApp : ApplicationContext
    {
        const int PollSeconds = 60;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "BatteryInfo";

        readonly SynchronizationContext ui;
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToolStripMenuItem autostartItem;
        readonly Dictionary<string, NotifyIcon> icons = new Dictionary<string, NotifyIcon>();
        readonly Dictionary<string, int> notifiedLevel = new Dictionary<string, int>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread worker;
        volatile bool exiting;
        List<DeviceBattery> current = new List<DeviceBattery>();

        public TrayApp()
        {
            ui = new WindowsFormsSynchronizationContext();

            var refresh = new ToolStripMenuItem("Оновити зараз");
            refresh.Click += delegate { wake.Set(); };
            autostartItem = new ToolStripMenuItem("Запускати з Windows") { CheckOnClick = true };
            autostartItem.Checked = IsAutostart();
            autostartItem.CheckedChanged += delegate { SetAutostart(autostartItem.Checked); };
            var exit = new ToolStripMenuItem("Вихід");
            exit.Click += delegate { Quit(); };
            menu.Items.Add(refresh);
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            menu.Items.Add(new ToolStripMenuItem("Battery Info v" + ver.Major + "." + ver.Minor + "." + ver.Build) { Enabled = false });
            menu.Items.Add(exit);
            menu.Opening += delegate { RebuildDeviceItems(); };

            LoadHidden();
            watcher = new DeviceWatcher(OnDeviceChange);

            worker = new Thread(Loop) { IsBackground = true, Name = "poll" };
            worker.Start();
        }

        // ---- Прихований список пристроїв (зберігається в реєстрі) ----
        const string SettingsKey = @"Software\BatteryInfo";
        readonly HashSet<string> hidden = new HashSet<string>();
        readonly DeviceWatcher watcher;
        volatile bool wakePending;

        void LoadHidden()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    string v = k == null ? null : k.GetValue("Hidden") as string;
                    if (!string.IsNullOrEmpty(v))
                        foreach (string s in v.Split('\n')) if (s.Length > 0) hidden.Add(s);
                }
            }
            catch { }
        }

        void SaveHidden()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    k.SetValue("Hidden", string.Join("\n", new List<string>(hidden).ToArray()));
            }
            catch { }
        }

        /// <summary>Підключили/відключили пристрій — оновлюємось швидше, ніж за таймером.</summary>
        void OnDeviceChange()
        {
            if (wakePending) return;
            wakePending = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(3000); // даємо Windows дописати властивості пристрою
                wakePending = false;
                wake.Set();
            });
        }

        sealed class DeviceWatcher : NativeWindow
        {
            const int WM_DEVICECHANGE = 0x0219;
            readonly Action callback;
            public DeviceWatcher(Action cb)
            {
                callback = cb;
                CreateHandle(new CreateParams());
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_DEVICECHANGE) callback();
                base.WndProc(ref m);
            }
        }

        void Loop()
        {
            while (!exiting)
            {
                var all = new List<DeviceBattery>();
                try { all.AddRange(BluetoothBattery.Read()); } catch { }
                try { all.AddRange(LogitechBattery.Read()); } catch { }
                if (exiting) return;
                ui.Post(delegate { Apply(all); }, null);
                wake.WaitOne(PollSeconds * 1000);
            }
        }

        void Apply(List<DeviceBattery> devices)
        {
            current = devices;
            var alive = new HashSet<string>();
            foreach (DeviceBattery d in devices)
            {
                if (hidden.Contains(d.Key)) continue;
                alive.Add(d.Key);
                NotifyIcon ni;
                if (!icons.TryGetValue(d.Key, out ni))
                {
                    ni = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
                    icons[d.Key] = ni;
                }
                UpdateIcon(ni, d);
                CheckThresholds(ni, d);
            }

            var gone = new List<string>();
            foreach (var kv in icons) if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (string k in gone)
            {
                icons[k].Visible = false;
                icons[k].Dispose();
                icons.Remove(k);
                notifiedLevel.Remove(k);
            }
        }

        void UpdateIcon(NotifyIcon ni, DeviceBattery d)
        {
            Icon old = ni.Icon;
            ni.Icon = MakeIcon(d);
            if (old != null) { DestroyIcon(old.Handle); old.Dispose(); }

            string text = d.Name + ": " + d.Percent + "%";
            if (d.Charging) text += " (заряджається)";
            if (d.Stale) text += "\nостанні дані " + d.LastSeen.ToString("HH:mm");
            ni.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        void CheckThresholds(NotifyIcon ni, DeviceBattery d)
        {
            int last;
            if (!notifiedLevel.TryGetValue(d.Key, out last)) last = 100;

            if (d.Charging || d.Percent > 20) { notifiedLevel[d.Key] = 100; return; }
            if (d.Stale) return;

            int level = d.Percent <= 5 ? 5 : (d.Percent <= 15 ? 15 : 20);
            if (level < last)
            {
                notifiedLevel[d.Key] = level;
                if (level <= 15)
                    ni.ShowBalloonTip(5000, "Низький заряд", d.Name + ": " + d.Percent + "%", ToolTipIcon.Warning);
            }
        }

        void RebuildDeviceItems()
        {
            for (int i = menu.Items.Count - 1; i >= 0; i--)
                if (menu.Items[i].Tag as string == "dev") menu.Items.RemoveAt(i);
            int pos = 0;
            foreach (DeviceBattery d in current)
            {
                string t = d.Name + " — " + d.Percent + "%" + (d.Charging ? " ⚡" : "") + (d.Stale ? " (спить?)" : "");
                var item = new ToolStripMenuItem(t) { Tag = "dev", Checked = !hidden.Contains(d.Key) };
                string key = d.Key;
                item.ToolTipText = "Галочка — показувати іконку в треї";
                item.Click += delegate
                {
                    if (!hidden.Remove(key)) hidden.Add(key);
                    SaveHidden();
                    Apply(current);
                };
                menu.Items.Insert(pos++, item);
            }
            if (current.Count == 0)
                menu.Items.Insert(pos, new ToolStripMenuItem("Пристроїв не знайдено") { Enabled = false, Tag = "dev" });
            else
                menu.Items.Insert(pos, new ToolStripSeparator { Tag = "dev" });
        }

        static string fontFamily;
        static FontStyle fontStyle;

        static bool? hasCondensed;

        /// <summary>
        /// Tahoma Bold — найчіткіший на 16 px (hinting). Для трьох цифр (100) беремо вузьку Bahnschrift,
        /// бо Tahoma не вміщається.
        /// </summary>
        static void PickFont(bool narrow)
        {
            if (hasCondensed == null)
            {
                bool found = false;
                using (var fc = new InstalledFontCollection())
                    foreach (FontFamily ff in fc.Families)
                        if (ff.Name == "Bahnschrift SemiBold Condensed") found = true;
                hasCondensed = found;
            }
            if (narrow && hasCondensed == true) { fontFamily = "Bahnschrift SemiBold Condensed"; fontStyle = FontStyle.Bold; }
            else { fontFamily = "Tahoma"; fontStyle = FontStyle.Bold; }
        }

        /// <summary>Найбільший шрифт, при якому текст влізає в область.</summary>
        static Font FitFont(Graphics g, string s, float w, float h, float maxPx)
        {
            PickFont(s.Length >= 3);
            for (float px = maxPx; px > 6; px -= 0.5f)
            {
                var f = new Font(fontFamily, px, fontStyle, GraphicsUnit.Pixel);
                SizeF m = g.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic);
                if (m.Width <= w && px * 0.74f <= h) return f;
                f.Dispose();
            }
            return new Font(fontFamily, 6f, fontStyle, GraphicsUnit.Pixel);
        }

        internal static Icon MakeIcon(DeviceBattery d)
        {
            return MakeIcon(d, SystemInformation.SmallIconSize.Width);
        }

        internal static Icon MakeIcon(DeviceBattery d, int N)
        {
            const int S = 32;
            float k = N / (float)S;
            Color bg;
            if (d.Stale) bg = Color.FromArgb(110, 110, 110);
            else if (d.Charging) bg = Color.FromArgb(0, 120, 215);
            else if (d.Percent <= 15) bg = Color.FromArgb(200, 40, 40);
            else if (d.Percent <= 30) bg = Color.FromArgb(220, 130, 0);
            else bg = Color.FromArgb(30, 150, 60);

            using (var bmp = new Bitmap(N, N))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(k, k); // фігури малюємо в координатах 32x32, текст — у реальних пікселях
                Color dark = Color.FromArgb(bg.R * 65 / 100, bg.G * 65 / 100, bg.B * 65 / 100);
                RectangleF a32; // область тексту в координатах 32x32
                using (var br = new SolidBrush(bg))
                using (var brDark = new SolidBrush(dark))
                {
                    if (d.Kind == DeviceKind.Mouse)
                    {
                        // корпус мишки (капсула) + розріз кнопок і колесо
                        FillRounded(g, br, new Rectangle(2, 0, 28, 31), 13);
                        g.CompositingMode = CompositingMode.SourceCopy;
                        using (var cut = new Pen(Color.Transparent, 1.6f))
                        {
                            g.DrawLine(cut, 16, 0, 16, 8);    // межа лівої/правої кнопки
                            g.DrawLine(cut, 2, 8, 30, 8);     // межа кнопок і корпусу
                        }
                        g.CompositingMode = CompositingMode.SourceOver;
                        g.FillRectangle(brDark, 14.5f, 1.5f, 3f, 4.5f); // колесо
                        a32 = new RectangleF(2, 9, 28, 19.5f);
                    }
                    else if (d.Kind == DeviceKind.Keyboard)
                    {
                        // клавіатура: широкий корпус з рядком клавіш зверху
                        FillRounded(g, br, new Rectangle(0, 5, 32, 24), 3);
                        for (int kx = 0; kx < 5; kx++)
                            g.FillRectangle(brDark, 3 + kx * 5.6f, 7.5f, 3.4f, 2.6f);
                        a32 = new RectangleF(1, 11, 30, 17);
                    }
                    else if (d.Kind == DeviceKind.Other)
                    {
                        // універсальний пристрій: батарейка
                        FillRounded(g, br, new Rectangle(0, 4, 28, 26), 4);
                        FillRounded(g, brDark, new Rectangle(28, 12, 4, 10), 1);
                        a32 = new RectangleF(1, 5, 27, 24);
                    }
                    else
                    {
                        // навушники: дужка, дві чашки, центральна плашка
                        using (var band = new Pen(bg, 3.2f))
                            g.DrawArc(band, 2.5f, 1.5f, 27f, 26f, 180, 180);
                        FillRounded(g, brDark, new Rectangle(0, 8, 7, 23), 3);
                        FillRounded(g, brDark, new Rectangle(24, 8, 7, 23), 3);
                        FillRounded(g, br, new Rectangle(4, 11, 24, 20), 2);
                        a32 = new RectangleF(3, 12, 26, 19);
                    }
                }
                g.ResetTransform();
                string s = d.Percent >= 100 ? "100" : d.Percent.ToString();
                RectangleF area = new RectangleF(a32.X * k, a32.Y * k, a32.Width * k, a32.Height * k);
                using (var f = FitFont(g, s, area.Width, area.Height, N))
                using (var sf = new StringFormat(StringFormat.GenericTypographic)
                    { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString(s, f, Brushes.White, area, sf);
                    if (s.Length >= 3) // вузький шрифт тонкий — "потовщуємо" подвійним друком
                    {
                        area.Offset(0.5f, 0);
                        g.DrawString(s, f, Brushes.White, area, sf);
                    }
                }

                IntPtr h = bmp.GetHicon();
                Icon tmp = Icon.FromHandle(h);
                Icon copy = (Icon)tmp.Clone();
                DestroyIcon(h);
                return copy;
            }
        }

        static void FillRounded(Graphics g, Brush b, Rectangle r, int rad)
        {
            using (var p = new GraphicsPath())
            {
                int d = rad * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        static bool IsAutostart()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        void Quit()
        {
            exiting = true;
            wake.Set();
            foreach (var ni in icons.Values) { ni.Visible = false; ni.Dispose(); }
            icons.Clear();
            ExitThread();
        }
    }
}
