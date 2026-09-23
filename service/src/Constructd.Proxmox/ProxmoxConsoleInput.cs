using Constructd.Core.Abstractions;
using Constructd.Core.Logic;

namespace Constructd.Proxmox;

internal static class ProxmoxConsoleInput
{
    private static readonly string[] ScanKeys = ("none esc 1 2 3 4 5 6 7 8 9 0 minus equal backspace tab " +
        "q w e r t y u i o p bracket_left bracket_right ret ctrl a s d f g h j k l semicolon apostrophe grave_accent " +
        "shift backslash z x c v b n m comma dot slash shift_r kp_multiply alt spc caps_lock " +
        "f1 f2 f3 f4 f5 f6 f7 f8 f9 f10 num_lock scroll_lock kp_7 kp_8 kp_9 kp_subtract kp_4 kp_5 kp_6 kp_add kp_1 kp_2 kp_3 kp_0 kp_decimal").Split(' ');
    private static readonly IReadOnlyDictionary<int, string> Extended = new Dictionary<int, string>
    {
        [0x1c] = "kp_enter", [0x1d] = "ctrl_r", [0x35] = "kp_divide", [0x37] = "print", [0x38] = "alt_r",
        [0x47] = "home", [0x48] = "up", [0x49] = "pgup", [0x4b] = "left", [0x4d] = "right", [0x4f] = "end",
        [0x50] = "down", [0x51] = "pgdn", [0x52] = "insert", [0x53] = "delete", [0x5b] = "meta_l", [0x5c] = "meta_r", [0x5d] = "compose"
    };
    private static object Key(string key, bool down) => new { type = "key", data = new { down, key = new { type = "qcode", data = key } } };
    internal static object Events(params object[] events) => new { execute = "input-send-event", arguments = new { events } };
    internal static IReadOnlyList<object> Keyboard(KeyboardInput input)
    {
        if (!ConsoleSessionRules.Valid(input)) throw new ConsoleTransportException();
        var commands = new List<object>();
        void Stroke(string key, bool? press = null)
        {
            commands.Add(press is bool down ? Events(Key(key, down)) : Events(Key(key, true), Key(key, false)));
        }
        switch (input.Kind)
        {
            case KeyboardInputKind.CtrlAltDel:
                commands.Add(Events(Key("ctrl", true), Key("alt", true), Key("delete", true), Key("delete", false), Key("alt", false), Key("ctrl", false)));
                break;
            case KeyboardInputKind.Key:
                Stroke(VirtualKey(input.KeyCode!.Value), input.Press);
                break;
            case KeyboardInputKind.Scancodes:
                var codes = input.Scancodes!;
                for (var i = 0; i < codes.Count; i++)
                {
                    var code = codes[i]; var extended = code == 0xe0;
                    if (extended) { if (++i == codes.Count) throw new ConsoleTransportException(); code = codes[i]; }
                    if (!extended && code == 0xe1)
                    {
                        byte[] pause = [0xe1, 0x1d, 0x45, 0xe1, 0x9d, 0xc5];
                        if (i + pause.Length > codes.Count || !codes.Skip(i).Take(pause.Length).SequenceEqual(pause)) throw new ConsoleTransportException();
                        Stroke("pause"); i += pause.Length - 1; continue;
                    }
                    var scan = code & 0x7f;
                    var key = extended ? Extended.GetValueOrDefault(scan) : scan switch
                    { > 0 and < 84 => ScanKeys[scan], 0x56 => "less", 0x57 => "f11", 0x58 => "f12", _ => null };
                    Stroke(key ?? throw new ConsoleTransportException(), (code & 0x80) == 0);
                }
                break;
            case KeyboardInputKind.Text:
                foreach (var ch in input.Text!)
                {
                    var (key, shifted) = TextKey(ch);
                    commands.Add(shifted ? Events(Key("shift", true), Key(key, true), Key(key, false), Key("shift", false)) : Events(Key(key, true), Key(key, false)));
                }
                break;
        }
        return commands;
    }
    private static string VirtualKey(int key) => key switch
    {
        >= 65 and <= 90 => ((char)(key + 32)).ToString(), >= 48 and <= 57 => ((char)key).ToString(),
        >= 112 and <= 123 => "f" + (key - 111), >= 96 and <= 105 => "kp_" + (key - 96),
        8 => "backspace", 9 => "tab", 13 => "ret", 16 or 160 => "shift", 161 => "shift_r",
        17 or 162 => "ctrl", 163 => "ctrl_r", 18 or 164 => "alt", 165 => "alt_r", 19 => "pause", 20 => "caps_lock",
        27 => "esc", 32 => "spc", 33 => "pgup", 34 => "pgdn", 35 => "end", 36 => "home", 37 => "left",
        38 => "up", 39 => "right", 40 => "down", 44 => "print", 45 => "insert", 46 => "delete", 91 => "meta_l", 92 => "meta_r", 93 => "compose",
        106 => "kp_multiply", 107 => "kp_add", 109 => "kp_subtract", 110 => "kp_decimal", 111 => "kp_divide", 144 => "num_lock", 145 => "scroll_lock",
        186 => "semicolon", 187 => "equal", 188 => "comma", 189 => "minus", 190 => "dot", 191 => "slash", 192 => "grave_accent",
        219 => "bracket_left", 220 => "backslash", 221 => "bracket_right", 222 => "apostrophe", 226 => "less",
        _ => throw new ConsoleTransportException()
    };
    private static (string Key, bool Shift) TextKey(char c)
    {
        if (char.IsAsciiLetterOrDigit(c)) return (char.ToLowerInvariant(c).ToString(), char.IsAsciiLetterUpper(c));
        const string plain = "`-=[]\\;',./";
        const string shifted = "~_+{}|:\"<>?";
        string[] keys = ["grave_accent", "minus", "equal", "bracket_left", "bracket_right", "backslash", "semicolon", "apostrophe", "comma", "dot", "slash"];
        if (plain.IndexOf(c) is var p && p >= 0) return (keys[p], false);
        if (shifted.IndexOf(c) is var s && s >= 0) return (keys[s], true);
        if ("!@#$%^&*()".IndexOf(c) is var n && n >= 0) return (((n + 1) % 10).ToString(), true);
        return c switch { ' ' => ("spc", false), '\n' or '\r' => ("ret", false), '\t' => ("tab", false), '\b' => ("backspace", false), _ => throw new ConsoleTransportException() };
    }
    internal static object Mouse(MouseInput input, int width, int height)
    {
        if (!ConsoleSessionRules.Valid(input, width, height)) throw new ConsoleTransportException();
        object Move(string type, string axis, int value) => new { type, data = new { axis, value } };
        object Button(bool down) => new { type = "btn", data = new { down, button = input.Button switch { 1 => "left", 2 => "right", _ => "middle" } } };
        return input.Kind switch
        {
            MouseInputKind.MoveAbsolute => Events(Move("abs", "x", (int)((long)input.X!.Value * 32767 / Math.Max(1, width - 1))), Move("abs", "y", (int)((long)input.Y!.Value * 32767 / Math.Max(1, height - 1)))),
            MouseInputKind.MoveRelative => Events(Move("rel", "x", input.Dx!.Value), Move("rel", "y", input.Dy!.Value)),
            MouseInputKind.Press => Events(Button(true)), MouseInputKind.Release => Events(Button(false)),
            _ => Events(Button(true), Button(false))
        };
    }
}
