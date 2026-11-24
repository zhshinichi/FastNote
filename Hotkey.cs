using System;
using System.Collections.Generic;

namespace FastNote
{
    /// <summary>
    /// The global hotkey, read from layout.ini ("Hotkey=Ctrl+Alt+Q"). Needs at least
    /// one of Ctrl / Alt / Shift / Win plus one key: A-Z, 0-9, F1-F24, Space, or any
    /// WPF Key name. Anything unreadable falls back to Alt+Q.
    /// </summary>
    public sealed class Hotkey
    {
        public const string Default = "Alt+Q";
        public const uint NoRepeat = 0x4000;

        public uint Modifiers { get; }
        public uint VirtualKey { get; }
        public string Text { get; }

        private Hotkey(uint modifiers, uint virtualKey, string text)
        {
            Modifiers = modifiers;
            VirtualKey = virtualKey;
            Text = text;
        }

        public static Hotkey Parse(string value)
        {
            if (TryParse(value, out var hotkey))
                return hotkey;
            TryParse(Default, out hotkey);
            return hotkey;
        }

        public static bool TryParse(string value, out Hotkey hotkey)
        {
            hotkey = null;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            uint modifiers = 0, key = 0;
            var names = new List<string>();
            foreach (var raw in value.Split('+'))
            {
                var part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        modifiers |= 0x0002; names.Add("Ctrl"); continue;
                    case "alt":
                        modifiers |= 0x0001; names.Add("Alt"); continue;
                    case "shift":
                        modifiers |= 0x0004; names.Add("Shift"); continue;
                    case "win":
                    case "windows":
                        modifiers |= 0x0008; names.Add("Win"); continue;
                }
                if (key != 0 || part.Length == 0)
                    return false;
                key = KeyCode(part, out var name);
                if (key == 0)
                    return false;
                names.Add(name);
            }
            if (key == 0 || modifiers == 0)
                return false;
            hotkey = new Hotkey(modifiers, key, string.Join("+", names));
            return true;
        }

        private static uint KeyCode(string part, out string name)
        {
            name = part.ToUpperInvariant();
            if (part.Length == 1)
            {
                var c = char.ToUpperInvariant(part[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                    return c;
            }
            if (name.Length > 1 && name[0] == 'F' && int.TryParse(name.Substring(1), out var number) &&
                number >= 1 && number <= 24)
                return (uint)(0x70 + number - 1);
            if (name == "SPACE")
            {
                name = "Space";
                return 0x20;
            }
            if (Enum.TryParse<System.Windows.Input.Key>(part, true, out var wpfKey) &&
                wpfKey != System.Windows.Input.Key.None)
            {
                name = wpfKey.ToString();
                return (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(wpfKey);
            }
            return 0;
        }
    }
}
