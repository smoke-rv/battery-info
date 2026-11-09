using System;
using System.Text.RegularExpressions;

namespace BatteryInfo
{
    public enum DeviceKind { Headphones, Mouse, Keyboard, Other }

    public class DeviceBattery
    {
        public string Key;          // стабільний ідентифікатор
        public string Name;
        public DeviceKind Kind;
        public int Percent;         // 0..100, -1 = невідомо
        public bool Charging;
        public bool Stale;          // значення застаріле (пристрій спить / недоступний)
        public DateTime LastSeen;
    }

    public static class DeviceClassifier
    {
        static readonly Regex MouseRx = new Regex(@"mouse|master|anywhere|trackball|ergo|\bmx\s?(vertical|anywhere)|pebble m|\bm\d{3}\b|мишк", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex KeyboardRx = new Regex(@"keyboard|keys|\bk\d{3}\b|клавіатур", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex HeadphonesRx = new Regex(@"headphone|headset|earbud|buds|airpods|pods|\bwh-|\bwf-|\bwi-|jbl|bose|beats|sennheiser|momentum|навушник", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>cod = Bluetooth Class of Device (0 якщо невідомий).</summary>
        public static DeviceKind FromBluetooth(uint cod, string name)
        {
            uint major = (cod >> 8) & 0x1F;
            if (major == 4) return DeviceKind.Headphones;          // audio/video
            if (major == 5)                                        // peripheral (HID)
            {
                uint kind = (cod >> 6) & 3;
                if (kind == 2) return DeviceKind.Mouse;
                if (kind == 1 || kind == 3) return DeviceKind.Keyboard;
                return DeviceKind.Other;                           // геймпад, джойстик...
            }
            return FromName(name);
        }

        public static DeviceKind FromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return DeviceKind.Other;
            if (MouseRx.IsMatch(name)) return DeviceKind.Mouse;
            if (KeyboardRx.IsMatch(name)) return DeviceKind.Keyboard;
            if (HeadphonesRx.IsMatch(name)) return DeviceKind.Headphones;
            return DeviceKind.Other;
        }

        /// <summary>HID++ тип пристрою (feature 0x0005 getDeviceType).</summary>
        public static DeviceKind FromHidpp(int type, string name)
        {
            switch (type)
            {
                case 0: case 2: return DeviceKind.Keyboard;   // keyboard, numpad
                case 3: case 4: case 5: return DeviceKind.Mouse; // mouse, trackpad, trackball
                case 8: return DeviceKind.Headphones;         // headset
                default: return FromName(name);
            }
        }
    }
}
