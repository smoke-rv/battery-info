using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BatteryInfo
{
    /// <summary>
    /// Читає заряд пристроїв Logitech, підключених через приймач (Bolt / Unifying),
    /// по протоколу HID++ 2.0 (довгі звіти 0x11, 20 байт).
    /// </summary>
    public static class LogitechBattery
    {
        const ushort VendorLogitech = 0x046D;
        const byte SwId = 0x07;

        // ---- Win32 ----
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public uint cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint req, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ReadFile(SafeFileHandle h, IntPtr buf, uint n, IntPtr read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteFile(SafeFileHandle h, IntPtr buf, uint n, IntPtr written, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out uint n, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateEventW(IntPtr sec, bool manual, bool initial, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool ResetEvent(IntPtr h);

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
        const uint ERROR_IO_PENDING = 997;
        const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;

        // ---- Канал до приймача ----
        sealed class Channel : IDisposable
        {
            readonly SafeFileHandle handle;
            readonly IntPtr ov, ev, buf;
            const int Len = 20;

            public Channel(SafeFileHandle h)
            {
                handle = h;
                ev = CreateEventW(IntPtr.Zero, true, false, null);
                ov = Marshal.AllocHGlobal(32);
                buf = Marshal.AllocHGlobal(Len);
            }

            void PrepOverlapped()
            {
                for (int i = 0; i < 32; i++) Marshal.WriteByte(ov, i, 0);
                Marshal.WriteIntPtr(ov, IntPtr.Size == 8 ? 24 : 16, ev);
                ResetEvent(ev);
            }

            public bool Write(byte[] data)
            {
                Marshal.Copy(data, 0, buf, Len);
                PrepOverlapped();
                if (!WriteFile(handle, buf, Len, IntPtr.Zero, ov))
                {
                    if ((uint)Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
                    if (WaitForSingleObject(ev, 1000) != 0) { CancelIoEx(handle, ov); return false; }
                }
                uint n;
                return GetOverlappedResult(handle, ov, out n, true) && n > 0;
            }

            /// <summary>Читає один звіт; null при таймауті.</summary>
            public byte[] Read(int timeoutMs)
            {
                PrepOverlapped();
                Marshal.WriteInt32(buf, 0); // не критично
                if (!ReadFile(handle, buf, Len, IntPtr.Zero, ov))
                {
                    if ((uint)Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return null;
                    if (WaitForSingleObject(ev, (uint)Math.Max(1, timeoutMs)) != 0)
                    {
                        CancelIoEx(handle, ov);
                        uint dummy;
                        GetOverlappedResult(handle, ov, out dummy, true);
                        return null;
                    }
                }
                uint got;
                if (!GetOverlappedResult(handle, ov, out got, true) || got == 0) return null;
                byte[] res = new byte[Len];
                Marshal.Copy(buf, res, 0, Len);
                return res;
            }

            public void Dispose()
            {
                handle.Dispose();
                CloseHandle(ev);
                Marshal.FreeHGlobal(ov);
                Marshal.FreeHGlobal(buf);
            }
        }

        static List<string> FindCandidatePaths()
        {
            var paths = new List<string>();
            Guid hid;
            HidD_GetHidGuid(out hid);
            IntPtr set = SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return paths;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var d = new SP_DEVICE_INTERFACE_DATA();
                    d.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref d)) break;

                    uint req;
                    SetupDiGetDeviceInterfaceDetailW(set, ref d, IntPtr.Zero, 0, out req, IntPtr.Zero);
                    if (req == 0) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)req);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref d, detail, req, out req, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                        if (path != null && path.ToLowerInvariant().Contains("vid_046d")) paths.Add(path);
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        static Channel OpenLongChannel(string path)
        {
            SafeFileHandle h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) return null;

            IntPtr pp;
            if (!HidD_GetPreparsedData(h, out pp)) { h.Dispose(); return null; }
            HIDP_CAPS caps;
            int rc = HidP_GetCaps(pp, out caps);
            HidD_FreePreparsedData(pp);
            // HID++ long: vendor page 0xFF00, usage 0x02, звіт 20 байт
            if (rc == 0 || caps.UsagePage != 0xFF00 || caps.OutputReportByteLength != 20 || caps.InputReportByteLength != 20)
            { h.Dispose(); return null; }
            return new Channel(h);
        }

        // ---- HID++ 2.0 ----
        static byte[] Transact(Channel ch, byte dev, byte feat, byte fn, params byte[] args)
        {
            byte[] req = new byte[20];
            req[0] = 0x11; req[1] = dev; req[2] = feat; req[3] = (byte)((fn << 4) | SwId);
            for (int i = 0; i < args.Length && i < 16; i++) req[4 + i] = args[i];
            Program.Log("  tx dev=" + dev + " feat=" + feat + " fn=" + fn);
            if (!ch.Write(req)) { Program.Log("  write failed"); return null; }
            Program.Log("  tx ok");

            DateTime end = DateTime.UtcNow.AddMilliseconds(900);
            while (true)
            {
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return null;
                byte[] r = ch.Read(left);
                if (r == null) return null;
                Program.Log("  rx " + BitConverter.ToString(r));
                if (r[1] != dev) continue;
                // помилка HID++ 2.0 або 1.0
                if ((r[2] == 0xFF || r[2] == 0x8F) && r[3] == feat) return null;
                if (r[2] == feat && (r[3] >> 4) == fn && (r[3] & 0x0F) == SwId) return r;
            }
        }

        static readonly Dictionary<string, int> FeatureCache = new Dictionary<string, int>();

        /// <summary>Індекс фічі; -1 = пристрій її не підтримує; -2 = пристрій не відповів.</summary>
        static int FindFeature(Channel ch, byte dev, ushort id, int attempts)
        {
            string ck = dev + ":" + id;
            int cached;
            if (FeatureCache.TryGetValue(ck, out cached)) return cached;

            for (int i = 0; i < attempts; i++)
            {
                byte[] r = Transact(ch, dev, 0x00, 0, (byte)(id >> 8), (byte)(id & 0xFF));
                if (r == null) continue;
                int idx = r[4] == 0 ? -1 : r[4];
                if (idx >= 0) FeatureCache[ck] = idx;
                return idx;
            }
            return -2;
        }

        static byte[] TransactRetry(Channel ch, byte dev, byte feat, byte fn, int attempts)
        {
            for (int i = 0; i < attempts; i++)
            {
                byte[] r = Transact(ch, dev, feat, fn);
                if (r != null) return r;
            }
            return null;
        }

        static string ReadName(Channel ch, byte dev)
        {
            int f = FindFeature(ch, dev, 0x0005, 2);
            if (f < 0) return null;
            byte[] c = Transact(ch, dev, (byte)f, 0);
            if (c == null) return null;
            int count = c[4];
            var sb = new StringBuilder();
            while (sb.Length < count && sb.Length < 48)
            {
                byte[] p = Transact(ch, dev, (byte)f, 1, (byte)sb.Length);
                if (p == null) break;
                int before = sb.Length;
                for (int i = 4; i < 20 && sb.Length < count; i++)
                {
                    if (p[i] == 0) break;
                    sb.Append((char)p[i]);
                }
                if (sb.Length == before) break;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        static int ReadType(Channel ch, byte dev)
        {
            int f = FindFeature(ch, dev, 0x0005, 2);
            if (f < 0) return -1;
            byte[] r = Transact(ch, dev, (byte)f, 2); // getDeviceType
            return r == null ? -1 : r[4];
        }

        static int VoltageToPercent(int mv)
        {
            // груба крива розряду Li-Po
            int[] v = { 3500, 3600, 3700, 3750, 3800, 3850, 3900, 3950, 4000, 4100, 4200 };
            int[] p = { 0, 5, 15, 25, 40, 55, 65, 75, 85, 95, 100 };
            if (mv <= v[0]) return 0;
            for (int i = 1; i < v.Length; i++)
                if (mv <= v[i]) return p[i - 1] + (p[i] - p[i - 1]) * (mv - v[i - 1]) / (v[i] - v[i - 1]);
            return 100;
        }

        static bool ReadBattery(Channel ch, byte dev, bool known, out int percent, out bool charging)
        {
            percent = -1; charging = false;
            // Для вже знайдених пристроїв даємо кілька спроб (мишка може прокидатися зі сну),
            // для порожніх слотів приймача — одну, щоб не гальмувати.
            int attempts = known ? 3 : 1;

            // Важливо: на іншу фічу переходимо ЛИШЕ якщо пристрій відповів, що першої нема.
            // Таймаут не означає "не підтримується" — інакше отримаємо грубе значення (0x1000).
            int f = FindFeature(ch, dev, 0x1004, attempts); // UNIFIED_BATTERY
            if (f == -2) return false;
            if (f >= 0)
            {
                byte[] r = TransactRetry(ch, dev, (byte)f, 1, attempts);
                if (r == null) return false;
                percent = r[4];
                charging = r[6] == 1 || r[6] == 2;
                return true;
            }
            f = FindFeature(ch, dev, 0x1000, attempts); // BATTERY_STATUS
            if (f == -2) return false;
            if (f >= 0)
            {
                byte[] r = TransactRetry(ch, dev, (byte)f, 0, attempts);
                if (r == null) return false;
                percent = r[4];
                charging = r[6] == 1 || r[6] == 2;
                return true;
            }
            f = FindFeature(ch, dev, 0x1001, attempts); // BATTERY_VOLTAGE
            if (f >= 0)
            {
                byte[] r = TransactRetry(ch, dev, (byte)f, 0, attempts);
                if (r == null) return false;
                percent = VoltageToPercent((r[4] << 8) | r[5]);
                charging = (r[6] & 0x80) != 0;
                return true;
            }
            return false;
        }

        // ---- Публічний API ----
        static readonly Dictionary<string, string> NameCache = new Dictionary<string, string>();
        static readonly Dictionary<string, int> TypeCache = new Dictionary<string, int>();
        static readonly Dictionary<string, DeviceBattery> LastKnown = new Dictionary<string, DeviceBattery>();
        static readonly TimeSpan StaleLimit = TimeSpan.FromHours(2);

        public static string LastError;

        public static List<DeviceBattery> Read()
        {
            var result = new List<DeviceBattery>();
            var seen = new HashSet<string>();
            LastError = null;

            foreach (string path in FindCandidatePaths())
            {
                Channel ch = null;
                try
                {
                    Program.Log("open " + path);
                    ch = OpenLongChannel(path);
                    Program.Log(ch == null ? "  not HID++ long" : "  HID++ long OK");
                    if (ch == null) continue;

                    for (byte dev = 1; dev <= 6; dev++)
                    {
                        string key = "lg:" + dev;
                        if (seen.Contains(key)) continue;
                        int pct; bool chg;
                        if (!ReadBattery(ch, dev, LastKnown.ContainsKey(key), out pct, out chg)) continue;

                        string name;
                        if (!NameCache.TryGetValue(key, out name) || name == null)
                        {
                            name = ReadName(ch, dev) ?? "Logitech";
                            NameCache[key] = name;
                        }
                        int type;
                        if (!TypeCache.TryGetValue(key, out type) || type < 0)
                        {
                            type = ReadType(ch, dev);
                            TypeCache[key] = type;
                        }
                        seen.Add(key);
                        var d = new DeviceBattery
                        {
                            Key = key,
                            Name = name,
                            Kind = DeviceClassifier.FromHidpp(type, name),
                            Percent = pct,
                            Charging = chg,
                            LastSeen = DateTime.Now
                        };
                        LastKnown[key] = d;
                        result.Add(d);
                    }
                }
                catch (Exception ex) { LastError = ex.Message; }
                finally { if (ch != null) ch.Dispose(); }
            }

            // пристрій спить / тимчасово недоступний — показуємо останнє відоме значення
            foreach (var kv in LastKnown)
            {
                if (seen.Contains(kv.Key)) continue;
                if (DateTime.Now - kv.Value.LastSeen > StaleLimit) continue;
                var old = kv.Value;
                result.Add(new DeviceBattery
                {
                    Key = old.Key, Name = old.Name, Kind = old.Kind, Percent = old.Percent,
                    Charging = old.Charging, Stale = true, LastSeen = old.LastSeen
                });
            }
            return result;
        }
    }
}
