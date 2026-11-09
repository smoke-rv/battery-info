using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BatteryInfo
{
    /// <summary>
    /// Універсальне читання заряду Bluetooth-пристроїв (класичних і BLE).
    /// Windows сама кладе рівень заряду у властивість {104EA319-...} 2 вузла пристрою
    /// (той самий, що показується в Settings → Bluetooth). Шукаємо цю властивість на всіх
    /// вузлах PnP, піднімаємось по ланцюжку батьків до самого BT-пристрою (BTHENUM\DEV_ / BTHLE\DEV_)
    /// і беремо звідти ім'я, стан підключення та клас пристрою.
    /// </summary>
    public static class BluetoothBattery
    {
        const uint DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, uint size, out uint required);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
            out uint type, byte[] buffer, uint size, out uint required, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        static DEVPROPKEY Key(string guid, uint pid)
        {
            return new DEVPROPKEY { fmtid = new Guid(guid), pid = pid };
        }

        static readonly DEVPROPKEY KeyBattery = Key("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static readonly DEVPROPKEY KeyConnected = Key("83DA6326-97A6-4088-9453-A1923F573B29", 15);
        static readonly DEVPROPKEY KeyFriendlyName = Key("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static readonly DEVPROPKEY KeyParent = Key("4340A6C5-93FA-4706-972C-7B648008AA20", 8);
        static readonly DEVPROPKEY KeyClassOfDevice = Key("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 10);

        static byte[] GetProp(IntPtr set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key)
        {
            byte[] buf = new byte[1024];
            uint type, req;
            if (!SetupDiGetDevicePropertyW(set, ref dev, ref key, out type, buf, (uint)buf.Length, out req, 0))
                return null;
            byte[] res = new byte[Math.Min(req, (uint)buf.Length)];
            Array.Copy(buf, res, res.Length);
            return res;
        }

        static string GetString(IntPtr set, ref SP_DEVINFO_DATA dev, DEVPROPKEY key)
        {
            byte[] b = GetProp(set, ref dev, key);
            if (b == null || b.Length < 2) return null;
            return Encoding.Unicode.GetString(b).TrimEnd('\0');
        }

        static bool IsBtNode(string id)
        {
            string u = id.ToUpperInvariant();
            return u.StartsWith(@"BTHENUM\DEV_") || u.StartsWith(@"BTHLE\DEV_");
        }

        static readonly Regex MacRegex = new Regex(@"DEV_([0-9A-F]{12})|&([0-9A-F]{12})_C0|_([0-9A-F]{12})\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string MacOf(string id)
        {
            Match m = MacRegex.Match(id);
            if (!m.Success) return null;
            for (int i = 1; i <= 3; i++) if (m.Groups[i].Success) return m.Groups[i].Value.ToUpperInvariant();
            return null;
        }

        static Rec GetRec(Dictionary<string, Rec> recs, string mac)
        {
            if (mac == null) return null;
            Rec r;
            if (!recs.TryGetValue(mac, out r)) { r = new Rec(); recs[mac] = r; }
            return r;
        }

        static void FillFromNode(IntPtr set, ref SP_DEVINFO_DATA node, Rec r)
        {
            string n = GetString(set, ref node, KeyFriendlyName);
            if (!string.IsNullOrEmpty(n)) r.Name = n;
            byte[] c = GetProp(set, ref node, KeyConnected);
            r.Connected = c != null && c.Length > 0 && c[0] != 0;
            byte[] cod = GetProp(set, ref node, KeyClassOfDevice);
            if (cod != null && cod.Length >= 4) r.Cod = BitConverter.ToUInt32(cod, 0);
        }

        class Rec
        {
            public string Name;
            public int Percent = -1;
            public bool Connected = true;
            public uint Cod;
        }

        public static List<DeviceBattery> Read()
        {
            var result = new List<DeviceBattery>();
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (set == new IntPtr(-1)) return result;

            var recs = new Dictionary<string, Rec>();
            try
            {
                for (uint i = 0; ; i++)
                {
                    var dev = new SP_DEVINFO_DATA();
                    dev.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(set, i, ref dev)) break;

                    var sb = new StringBuilder(512);
                    uint req;
                    if (!SetupDiGetDeviceInstanceIdW(set, ref dev, sb, (uint)sb.Capacity, out req)) continue;
                    string id = sb.ToString();

                    // 1) сам BT-пристрій (BTHENUM\DEV_ / BTHLE\DEV_): ім'я, підключення, клас
                    if (IsBtNode(id))
                    {
                        Rec nr = GetRec(recs, MacOf(id));
                        if (nr != null) FillFromNode(set, ref dev, nr);
                    }

                    // 2) вузол з рівнем заряду
                    byte[] b = GetProp(set, ref dev, KeyBattery);
                    if (b == null || b.Length < 1 || b[0] > 100) continue;

                    // MAC беремо з найближчого BT-предка (BLE HID), а якщо його нема
                    // (класичний HFP — вузол лежить поруч, а не під пристроєм) — з самого instance id
                    string mac = null;
                    string cid = id;
                    SP_DEVINFO_DATA cur = dev;
                    for (int depth = 0; depth < 6 && mac == null; depth++)
                    {
                        if (IsBtNode(cid)) { mac = MacOf(cid); break; }
                        string parent = GetString(set, ref cur, KeyParent);
                        if (string.IsNullOrEmpty(parent)) break;
                        var pd = new SP_DEVINFO_DATA();
                        pd.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                        if (!SetupDiOpenDeviceInfoW(set, parent, IntPtr.Zero, 0, ref pd)) break;
                        cur = pd; cid = parent;
                    }
                    if (mac == null) mac = MacOf(id);
                    Rec r = GetRec(recs, mac);
                    if (r == null) continue;
                    if (r.Percent < 0) r.Percent = b[0];
                    if (r.Name == null) r.Name = GetString(set, ref dev, KeyFriendlyName);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            foreach (var kv in recs)
            {
                Rec r = kv.Value;
                if (!r.Connected || r.Percent < 0) continue;
                string name = string.IsNullOrEmpty(r.Name) ? "Bluetooth-пристрій" : r.Name;
                name = Regex.Replace(name, @"\s*(Hands-Free( AG)?)$", "", RegexOptions.IgnoreCase);

                result.Add(new DeviceBattery
                {
                    Key = "bt:" + kv.Key,
                    Name = name,
                    Kind = DeviceClassifier.FromBluetooth(r.Cod, name),
                    Percent = r.Percent,
                    Charging = false,
                    LastSeen = DateTime.Now
                });
            }
            return result;
        }
    }
}
