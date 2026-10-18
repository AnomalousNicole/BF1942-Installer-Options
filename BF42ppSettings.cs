// BF42++'s own settings, which the BF42++ settings tab shows: their names, defaults and ranges, and how they are
// read from and saved to bf42++.ini in the game folder. The names, defaults and ranges are BF42++'s own
// (src/settings.h in github.com/sila-skandia/bf42plus); the labels and explanations are written for players.
//
// BF42++ reads the file when the game starts, and writes it when a setting is changed in the game's console
// (plus.<name>), so the app only saves it while the game is closed. Only the lines of the settings that changed
// are rewritten: the comments, the buddy colours, the ignore list and anything else in the file stay as they are.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BF1942Options;

enum PlusKind { Switch, Whole, Number, Choice, Color, Colors }

// One setting in bf42++.ini's [general] section. Values are kept as the text the file has for them (see Normalize):
// "on"/"off", a whole number, a number with six decimals like BF42++ writes, one of Choices, or colours.
// Scale: a Number is shown multiplied by it (maxTimeToBusyWait is in seconds in the file, in milliseconds here).
sealed record PlusSetting(string Key, string Card, string Label, PlusKind Kind, string Default, string Help,
                          double Min = 0, double Max = 0, double Scale = 1, string[]? Choices = null)
{
    // The explanation the label's tooltip shows, with BF42++'s own default
    public string Tooltip => Help + "\n\nBF42++'s default: " + Kind switch
    {
        PlusKind.Switch => Default == "on" ? "on" : "off",
        PlusKind.Number => Show(Default),
        PlusKind.Choice => Default.ToUpperInvariant(),
        _ => Default,
    } + ".";

    // The value as the window shows it: a Number scaled and without trailing zeros, a choice in capitals
    public string Show(string value) => Kind switch
    {
        PlusKind.Number => (double.Parse(value, CultureInfo.InvariantCulture) * Scale).ToString("0.###", CultureInfo.InvariantCulture),
        PlusKind.Choice => value.ToUpperInvariant(),
        _ => value,
    };

    // A value typed in the window (or read from the file, with fromFile) as the file has it, or null when BF42++
    // wouldn't take it. BF42++ reads on/off, true/false, yes/no and 1/0 for a switch, and colour names in any case.
    public string? Normalize(string text, bool fromFile = false)
    {
        string s = text.Trim();
        if (fromFile && s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        switch (Kind)
        {
            case PlusKind.Switch:
                string b = s.ToLowerInvariant();
                if (b == "on" || b.StartsWith('t') || b.StartsWith('y') || b.StartsWith('1')) return "on";
                if (b == "off" || b.StartsWith('f') || b.StartsWith('n') || b.StartsWith('0')) return "off";
                return null;
            case PlusKind.Whole:
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= Min && i <= Max
                    ? i.ToString(CultureInfo.InvariantCulture) : null;
            case PlusKind.Number:
                if (!double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return null;
                if (!fromFile) d /= Scale;
                if (d < Min / Scale - 1e-9 || d > Max / Scale + 1e-9) return null;
                return d.ToString("F6", CultureInfo.InvariantCulture);
            case PlusKind.Choice:
                return Choices!.FirstOrDefault(c => c.Equals(s, StringComparison.OrdinalIgnoreCase));
            case PlusKind.Color:
                return PlusColors.IsColor(s) ? s.ToLowerInvariant() : null;
            default:
                string[] list = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return list.Length > 0 && list.All(PlusColors.IsColor) ? string.Join(' ', list).ToLowerInvariant() : null;
        }
    }

    // What the window says when Normalize turns a typed value down
    public string Problem => Kind switch
    {
        PlusKind.Whole => $"{Label}: type a whole number from {Min} to {Max}.",
        PlusKind.Number => $"{Label}: type a number from {Min.ToString(CultureInfo.InvariantCulture)} to {Max.ToString(CultureInfo.InvariantCulture)}.",
        PlusKind.Color => $"{Label}: type a colour name such as yellow or white, or a colour code such as #FF0 or #FFFF00.",
        _ => $"{Label}: this value can't be used.",
    };
}

static class PlusColors
{
    // The colour names BF42++ knows (the web colours, from its util.cpp) and their RGB values, and #RGB / #RRGGBB
    public static readonly Dictionary<string, uint> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aliceblue"] = 0xF0F8FF, ["antiquewhite"] = 0xFAEBD7, ["aqua"] = 0x00FFFF, ["aquamarine"] = 0x7FFFD4,
        ["azure"] = 0xF0FFFF, ["beige"] = 0xF5F5DC, ["bisque"] = 0xFFE4C4, ["black"] = 0x000000,
        ["blanchedalmond"] = 0xFFEBCD, ["blue"] = 0x0000FF, ["blueviolet"] = 0x8A2BE2, ["brown"] = 0xA52A2A,
        ["burlywood"] = 0xDEB887, ["cadetblue"] = 0x5F9EA0, ["chartreuse"] = 0x7FFF00, ["chocolate"] = 0xD2691E,
        ["coral"] = 0xFF7F50, ["cornflowerblue"] = 0x6495ED, ["cornsilk"] = 0xFFF8DC, ["crimson"] = 0xDC143C,
        ["cyan"] = 0x00FFFF, ["darkblue"] = 0x00008B, ["darkcyan"] = 0x008B8B, ["darkgoldenrod"] = 0xB8860B,
        ["darkgray"] = 0xA9A9A9, ["darkgreen"] = 0x006400, ["darkgrey"] = 0xA9A9A9, ["darkkhaki"] = 0xBDB76B,
        ["darkmagenta"] = 0x8B008B, ["darkolivegreen"] = 0x556B2F, ["darkorange"] = 0xFF8C00,
        ["darkorchid"] = 0x9932CC, ["darkred"] = 0x8B0000, ["darksalmon"] = 0xE9967A, ["darkseagreen"] = 0x8FBC8F,
        ["darkslateblue"] = 0x483D8B, ["darkslategray"] = 0x2F4F4F, ["darkslategrey"] = 0x2F4F4F,
        ["darkturquoise"] = 0x00CED1, ["darkviolet"] = 0x9400D3, ["deeppink"] = 0xFF1493, ["deepskyblue"] = 0x00BFFF,
        ["dimgray"] = 0x696969, ["dimgrey"] = 0x696969, ["dodgerblue"] = 0x1E90FF, ["firebrick"] = 0xB22222,
        ["floralwhite"] = 0xFFFAF0, ["forestgreen"] = 0x228B22, ["fuchsia"] = 0xFF00FF, ["gainsboro"] = 0xDCDCDC,
        ["ghostwhite"] = 0xF8F8FF, ["gold"] = 0xFFD700, ["goldenrod"] = 0xDAA520, ["gray"] = 0x808080,
        ["green"] = 0x008000, ["greenyellow"] = 0xADFF2F, ["grey"] = 0x808080, ["honeydew"] = 0xF0FFF0,
        ["hotpink"] = 0xFF69B4, ["indianred"] = 0xCD5C5C, ["indigo"] = 0x4B0082, ["ivory"] = 0xFFFFF0,
        ["khaki"] = 0xF0E68C, ["lavender"] = 0xE6E6FA, ["lavenderblush"] = 0xFFF0F5, ["lawngreen"] = 0x7CFC00,
        ["lemonchiffon"] = 0xFFFACD, ["lightblue"] = 0xADD8E6, ["lightcoral"] = 0xF08080, ["lightcyan"] = 0xE0FFFF,
        ["lightgoldenrodyellow"] = 0xFAFAD2, ["lightgray"] = 0xD3D3D3, ["lightgreen"] = 0x90EE90,
        ["lightgrey"] = 0xD3D3D3, ["lightpink"] = 0xFFB6C1, ["lightsalmon"] = 0xFFA07A, ["lightseagreen"] = 0x20B2AA,
        ["lightskyblue"] = 0x87CEFA, ["lightslategray"] = 0x778899, ["lightslategrey"] = 0x778899,
        ["lightsteelblue"] = 0xB0C4DE, ["lightyellow"] = 0xFFFFE0, ["lime"] = 0x00FF00, ["limegreen"] = 0x32CD32,
        ["linen"] = 0xFAF0E6, ["magenta"] = 0xFF00FF, ["maroon"] = 0x800000, ["mediumaquamarine"] = 0x66CDAA,
        ["mediumblue"] = 0x0000CD, ["mediumorchid"] = 0xBA55D3, ["mediumpurple"] = 0x9370DB,
        ["mediumseagreen"] = 0x3CB371, ["mediumslateblue"] = 0x7B68EE, ["mediumspringgreen"] = 0x00FA9A,
        ["mediumturquoise"] = 0x48D1CC, ["mediumvioletred"] = 0xC71585, ["midnightblue"] = 0x191970,
        ["mintcream"] = 0xF5FFFA, ["mistyrose"] = 0xFFE4E1, ["moccasin"] = 0xFFE4B5, ["navajowhite"] = 0xFFDEAD,
        ["navy"] = 0x000080, ["oldlace"] = 0xFDF5E6, ["olive"] = 0x808000, ["olivedrab"] = 0x6B8E23,
        ["orange"] = 0xFFA500, ["orangered"] = 0xFF4500, ["orchid"] = 0xDA70D6, ["palegoldenrod"] = 0xEEE8AA,
        ["palegreen"] = 0x98FB98, ["paleturquoise"] = 0xAFEEEE, ["palevioletred"] = 0xDB7093,
        ["papayawhip"] = 0xFFEFD5, ["peachpuff"] = 0xFFDAB9, ["peru"] = 0xCD853F, ["pink"] = 0xFFC0CB,
        ["plum"] = 0xDDA0DD, ["powderblue"] = 0xB0E0E6, ["purple"] = 0x800080, ["rebeccapurple"] = 0x663399,
        ["red"] = 0xFF0000, ["rosybrown"] = 0xBC8F8F, ["royalblue"] = 0x4169E1, ["saddlebrown"] = 0x8B4513,
        ["salmon"] = 0xFA8072, ["sandybrown"] = 0xF4A460, ["seagreen"] = 0x2E8B57, ["seashell"] = 0xFFF5EE,
        ["sienna"] = 0xA0522D, ["silver"] = 0xC0C0C0, ["skyblue"] = 0x87CEEB, ["slateblue"] = 0x6A5ACD,
        ["slategray"] = 0x708090, ["slategrey"] = 0x708090, ["snow"] = 0xFFFAFA, ["springgreen"] = 0x00FF7F,
        ["steelblue"] = 0x4682B4, ["tan"] = 0xD2B48C, ["teal"] = 0x008080, ["thistle"] = 0xD8BFD8,
        ["tomato"] = 0xFF6347, ["turquoise"] = 0x40E0D0, ["violet"] = 0xEE82EE, ["wheat"] = 0xF5DEB3,
        ["white"] = 0xFFFFFF, ["whitesmoke"] = 0xF5F5F5, ["yellow"] = 0xFFFF00, ["yellowgreen"] = 0x9ACD32,
    };

    public static bool IsColor(string s) => Names.ContainsKey(s) || Regex.IsMatch(s, "^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$");

    // 0xRRGGBB for a name or code that IsColor takes
    public static uint Rgb(string s)
    {
        if (Names.TryGetValue(s, out uint rgb)) return rgb;
        string hex = s[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        return Convert.ToUInt32(hex, 16);
    }
}

static partial class Game
{
    const string PlusIni = "bf42++.ini";
    const string PlusSection = "general";

    // In the order the tab shows them, card by card
    public static readonly PlusSetting[] PlusSettings =
    {
        new("showIDInKills", "HUD", "Player IDs in kill messages", PlusKind.Switch, "off",
            "Shows each player's ID, the number the server knows them by, next to their name in the kill messages. Handy when you report a player to the server's admins."),
        new("showIDInNametags", "HUD", "Player IDs in name tags", PlusKind.Switch, "off",
            "Shows each player's ID in the name tag above them."),
        new("lowerNametags", "HUD", "Lower name tags up close", PlusKind.Switch, "on",
            "Lowers the name tags above other players bit by bit as you get closer, so a name tag doesn't hide the player you're looking at."),
        new("hitIndicatorTime", "HUD", "Hit indicator time (seconds)", PlusKind.Number, "1.000000",
            "How long the hit indicator, the marker that shows you hit someone, stays on screen. 0 turns it off.", 0, 10),
        new("debugTextColor", "HUD", "Stats text colour", PlusKind.Color, "yellow",
            "The colour of the text that the frame rate and statistics displays (console.showFPS, console.showStats and the like) put on screen. A colour name such as yellow or white, or a colour code such as #FFFF00."),
        new("presetBuddyColors", "HUD", "Buddy colours", PlusKind.Colors, "lime dodgerblue yellow blueviolet",
            "The colours a buddy gets, in this order, when you click ADD BUDDY on the scoreboard again and again. Click + to add a colour, or click a colour to move it or take it out."),

        new("showConnectsInChat", "CHAT AND CONSOLE", "Show who joins", PlusKind.Switch, "off",
            "Shows a message in the chat when a player connects to the server."),
        new("showIDInChat", "CHAT AND CONSOLE", "Player IDs in chat", PlusKind.Switch, "off",
            "Shows player IDs in chat messages, in name changes and when players join or leave."),
        new("showVoteInConsole", "CHAT AND CONSOLE", "Show votes in the console", PlusKind.Switch, "off",
            "Shows in the console (the ~ key) who started a vote and who voted."),
        new("wrapChat", "CHAT AND CONSOLE", "Wrap chat after (characters)", PlusKind.Whole, "0",
            "Splits chat messages longer than this over more lines, so they don't run off the screen. 42 works well; 0 turns it off. Typing chattext 6 in the console then shows six chat lines instead of four.", 0, 200),

        new("enable3DMineMap", "3D MAP MARKERS", "Friendly mines", PlusKind.Switch, "on",
            "Marks your team's mines in the game world, so you don't drive over them. Online it only works on servers that allow it."),
        new("enable3DSupplyMap", "3D MAP MARKERS", "Supply points", PlusKind.Switch, "on",
            "Marks the places to heal, rearm and repair in the game world. Online it only works on servers that allow it."),
        new("enable3DControlPointMap", "3D MAP MARKERS", "Control points", PlusKind.Switch, "on",
            "Marks the control points in the game world. Online it only works on servers that allow it."),

        new("correctedLookSensitivity", "CONTROLS", "Same look speed when moving", PlusKind.Switch, "off",
            "On foot, the game turns you left and right faster while you move than while you stand still. This keeps your mouse sensitivity the same either way. It may feel slower at first."),
        new("stationaryMGInfSensitivity", "CONTROLS", "Infantry sensitivity on fixed MGs", PlusKind.Switch, "off",
            "Uses your infantry mouse sensitivity on stationary machine guns (MG42, Browning) as well."),
        new("dontBlockInputDuringFreeLook", "CONTROLS", "Keep flying during free look", PlusKind.Switch, "on",
            "In aircraft, free look normally stops pitch, roll and yaw, so only the throttle works while you look around. This keeps your keyboard and joystick flight controls working (the mouse still only looks around). Online, the server can then no longer tell which way you look while you fly."),

        new("smootherGameplay", "PERFORMANCE", "Smoother gameplay", PlusKind.Switch, "on",
            "Lets the game time its frames as precisely as the server does, and empties your queue of inputs on the server each time you die. The game can feel smoother and the server reacts to your input sooner. It may cost a little speed on very old PCs."),
        new("fasterMapchange", "PERFORMANCE", "Faster map changes", PlusKind.Switch, "on",
            "Restarts the game faster when the server changes the map."),
        new("maxTimeToBusyWait", "PERFORMANCE", "Busy-wait limit (ms)", PlusKind.Number, "0.001500",
            "With a frame rate limit (game.lockFps), the game keeps the CPU busy while it waits for the next frame, which drains laptop batteries and makes fans loud. This is the longest it may wait that way; the rest of the time it sleeps. Lower it for less CPU load, raise it if the game stutters. Values of 1 or less can make the frame rate less steady; -1 keeps the game's own behaviour.",
            -1, 50, 1000),
        new("minTimerResolution", "PERFORMANCE", "Sleep timer resolution (ms)", PlusKind.Whole, "1",
            "How precisely Windows times the game's sleeps while it waits for the next frame (see the busy-wait limit). Higher values can make the game stutter.", 1, 16),

        new("fixAnimatedMeshLighting", "GRAPHICS AND FILES", "Fix lighting on soldiers and vehicles", PlusKind.Switch, "on",
            "Without it, the lit side of soldiers and vehicles turns with their animation instead of facing the sun. By Ahrkylien."),
        new("screenshotFormat", "GRAPHICS AND FILES", "Screenshot format", PlusKind.Choice, "png",
            "The file type of your screenshots: PNG (sharp, bigger files) or JPG (smaller files).", Choices: new[] { "png", "jpg" }),
        new("recordReplays", "GRAPHICS AND FILES", "Record round replays", PlusKind.Switch, "off",
            "Records every round you play online to the replays folder in the game folder, one file per server you join, to watch later in a replay viewer such as play.bfstats.io. You can also turn it on or off in the console with plus.recordReplays 1 or 0. By sila-skandia."),
        new("disableArchiveOnlyMode", "GRAPHICS AND FILES", "Load unpacked game files", PlusKind.Switch, "off",
            "Lets the game load unpacked files from the game folder as well as its .rfa archives, like the BF1942_r debug version does. For modders; leave it off to play."),
    };

    // The file in the game folder, or Setup's copy in the library when the game folder has none (that is the one
    // Apply puts there when BF42++ is turned on, and the one saving starts from)
    static string PlusFile() => File.Exists(G(PlusIni)) ? G(PlusIni) : L(LibBF42pp + @"\" + PlusIni);

    // Every setting's value; one that is missing or that BF42++ wouldn't take has BF42++'s default, as in the game
    public static Dictionary<string, string> ReadPlus()
    {
        var values = PlusSettings.ToDictionary(s => s.Key, s => s.Default);
        string path = PlusFile();
        if (!File.Exists(path)) return values;
        string[] lines = File.ReadAllLines(path, Encoding.Latin1);
        var (start, end) = PlusSectionLines(lines);
        // The first line of a key counts, as for BF42++
        for (int i = end - 1; i >= start; i--)
            if (PlusLine(lines[i]) is var (key, value) && PlusSettings.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { } s)
                values[s.Key] = s.Normalize(value, fromFile: true) ?? s.Default;
        return values;
    }

    // Writes the settings that differ between from and to, as BF42++ writes them ("name = value")
    static void SavePlus(Dictionary<string, string> from, Dictionary<string, string> to)
    {
        PlusSetting[] changed = PlusSettings.Where(s => from[s.Key] != to[s.Key]).ToArray();
        if (changed.Length == 0) return;
        string path = G(PlusIni);
        if (!File.Exists(path) && File.Exists(L(LibBF42pp + @"\" + PlusIni))) Copy(LibBF42pp + @"\" + PlusIni, PlusIni);
        var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.Latin1).ToList() : new List<string>();
        var (start, end) = PlusSectionLines(lines.ToArray());
        if (start < 0)
        {
            lines.InsertRange(0, new[] { "[" + PlusSection + "]", "" });
            (start, end) = (1, 1);
        }
        foreach (PlusSetting s in changed)
        {
            string line = s.Key + " = " + to[s.Key];
            bool found = false;
            for (int i = start; i < end; i++)
                if (PlusLine(lines[i]) is var (key, _) && key.Equals(s.Key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = line;
                    found = true;
                }
            if (!found)
            {
                // After the section's last setting, before the blank lines that end it
                int at = end;
                while (at > start && lines[at - 1].Trim() == "") at--;
                lines.Insert(at, line);
                end++;
            }
            Log("bf42++.ini: " + line);
        }
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllLines(path, lines, Encoding.Latin1);
    }

    // The lines of the [general] section: from the line after its header to the next section's header (or the end).
    // (-1, -1) when the file has none.
    static (int Start, int End) PlusSectionLines(IReadOnlyList<string> lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (!t.StartsWith('[') || !t.EndsWith(']')) continue;
            if (start >= 0) return (start, i);
            if (t[1..^1].Trim().Equals(PlusSection, StringComparison.OrdinalIgnoreCase)) start = i + 1;
        }
        return start >= 0 ? (start, lines.Count) : (-1, -1);
    }

    // "name = value", or null for a comment, a blank line or anything else
    static (string Key, string Value)? PlusLine(string line)
    {
        string t = line.Trim();
        int eq = t.IndexOf('=');
        if (t == "" || t[0] == ';' || t[0] == '#' || eq <= 0) return null;
        return (t[..eq].Trim(), t[(eq + 1)..].Trim());
    }
}
