// What BF1942 Options switches, and how. The window is in MainWindow.xaml; this file has no UI.
//
// Setup keeps a copy of every fix the app can turn on, plus the original game files that the extras replace,
// in a library folder inside the game folder, and installs the app in a subfolder of it. What is on is read
// from the game folder (a file counts as "on" when it is identical to its copy in the library), so the window
// always shows the real state, also after the player changed files by hand. An extra that the installer was built without has no copy, and its switch is disabled.
//
// A copy of the app downloaded on its own (BF1942-Installer's build.ps1 -Standalone) brings that library with it, in
// Library next to the exe, so it also works with Battlefield 1942 folders that BF1942-Installer did not install:
// setting one up copies the library to Options there, and from then on the folder works like one Setup installed.
//
// options.json next to the exe (written by build.ps1) holds what differs between installers: the registry
// key Setup keeps its state in, whether a CD key may be generated, the server shortcut's name and address, and
// the community's Discord invite.
//
// The app is a 32-bit process, so HKLM\SOFTWARE is the same 32-bit view Setup writes to, and app.manifest
// asks for administrator rights: the game folder, HKLM and sdbinst (Compatibility Profile) all need them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BF1942Options;

enum Renderer { None, DXVK, DgVoodoo, Unknown }

// What is on in the game folder, or what the player picked in the window
sealed class Settings
{
    public Renderer Renderer;
    public bool BF42pp, Audio, HiResUI, NoSiren, Borderless, Compat, SkipIntro;
    public int Font;   // index into Game.FontNames; -1 = a Font.rfa that Setup did not install
    // The profile the game starts with and its player name. Not switched by Apply: "Use this name" changes them
    // at once, so SameAs leaves them out.
    public string Profile = "", PlayerName = "";
    // BF42++'s own settings in bf42++.ini, by name (BF42ppSettings.cs)
    public Dictionary<string, string> Plus = new();

    public Settings Clone()
    {
        var s = (Settings)MemberwiseClone();
        s.Plus = new Dictionary<string, string>(Plus);
        return s;
    }

    public bool PlusSameAs(Settings o) => Plus.Count == o.Plus.Count && Plus.All(p => o.Plus.TryGetValue(p.Key, out string? v) && v == p.Value);

    public bool SameAs(Settings o) =>
        Renderer == o.Renderer && BF42pp == o.BF42pp && Audio == o.Audio && HiResUI == o.HiResUI &&
        NoSiren == o.NoSiren && Borderless == o.Borderless && Compat == o.Compat &&
        SkipIntro == o.SkipIntro && Font == o.Font && PlusSameAs(o);
}

static partial class Game
{
    // ---- options.json ----

    // Setup's state key (HKLM\SOFTWARE\WOW6432Node\... on 64-bit Windows, as this is a 32-bit process)
    static readonly string StateKey;
    public static readonly bool SerialAllowed;
    public static readonly string ServerShortcut;
    public static readonly string ServerAddress;
    public static readonly string DiscordUrl;
    public static readonly string[]? SeparatePrograms;   // what the installer can add as programs of their own (null: not listed)

    static Game()
    {
        StateKey = @"SOFTWARE\BF1942 Installer";
        SerialAllowed = true;
        ServerShortcut = "";
        ServerAddress = "";
        DiscordUrl = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "options.json")));
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("stateKey", out JsonElement key) && key.GetString() is { Length: > 0 } k) StateKey = k;
            if (root.TryGetProperty("generateSerial", out JsonElement serial)) SerialAllowed = serial.GetBoolean();
            if (root.TryGetProperty("serverShortcut", out JsonElement server)) ServerShortcut = server.GetString() ?? "";
            if (root.TryGetProperty("serverAddress", out JsonElement address)) ServerAddress = address.GetString() ?? "";
            if (root.TryGetProperty("discordUrl", out JsonElement discord)) DiscordUrl = discord.GetString() ?? "";
            if (root.TryGetProperty("separatePrograms", out JsonElement programs) && programs.ValueKind == JsonValueKind.Array)
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (JsonElement p in programs.EnumerateArray())
                    if (p.GetString() is { Length: > 0 } n) names.Add(n);
                SeparatePrograms = names.ToArray();
            }
        }
        catch { }
        UseFolder(StartFolder());
    }

    public static readonly string[] FontNames = { "Original", "1x", "2x", "3x", "3.5x", "4x" };
    public static readonly string[] FontHints = { "800x600 / 1024x768", "1280x720 / 1366x768", "1920x1080",
                                                  "2560x1440", "3440x1440 / 2560x1600", "3840x2160 (4K)" };

    const string FontRfa      = @"Mods\bf1942\Archives\Font.rfa";
    const string MenuRfa      = @"Mods\bf1942\Archives\menu.rfa";
    const string BobRfa       = @"Mods\bf1942\Archives\bf1942\levels\Battle_of_Britain.rfa";
    const string VideoDefault = @"Mods\bf1942\Settings\VideoDefault.con";

    // Folders in the library (Setup's [Files] section uses the same names)
    const string LibBF42pp = "BF42++";
    const string LibAudio  = "DSOAL";
    const string LibDXVK   = "DXVK";
    const string LibDgV    = "dgVoodoo2";
    const string LibBL     = "Borderless1942";
    const string LibCompat = "Compatibility Profile";
    const string LibHiRes  = "Higher resolution UI";
    const string LibSiren  = "Battle of Britain disable siren";
    const string LibOrig   = "Originals";

    // Setup keeps the library in Options in the game folder, and installs the app in Options\App. The player can
    // also pick another game folder (Browse, as the app can be downloaded on its own); the library is then the one
    // in that folder.
    const string LibFolder = "Options";
    public static string Dir { get; private set; } = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\.."));
    public static string Lib { get; private set; } = "";
    const string SavedFolder = "GameFolder";   // in the state key: the folder the player last picked

    // The library a standalone copy of the app brings with it (none in the copy Setup installs)
    static string Bundled => Path.Combine(AppContext.BaseDirectory, "Library");
    public static bool Standalone => IsLibrary(Bundled);

    public static bool LibraryFound => IsLibrary(Lib);
    public static bool GameFound => HasGame(Dir);
    public static bool Installed => GameFound && LibraryFound;

    public static bool HasGame(string dir) => File.Exists(Path.Combine(dir, "BF1942.exe"));
    static bool IsLibrary(string folder) => Directory.Exists(Path.Combine(folder, LibBF42pp));
    public static string FullPath(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    // The folder the window opens with: the one the player last picked, the one the app is installed in (or a
    // standalone copy was extracted in), or the one the game's registry entry names (Setup writes it too) - the
    // first that has BF1942.exe
    static string StartFolder()
    {
        string own = Dir, parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        foreach (Func<string> dir in new Func<string>[] { () => State(SavedFolder), () => own, () => parent, EaGameDir })
        {
            try
            {
                string d = dir();
                if (d != "" && HasGame(d)) return d;
            }
            catch { }
        }
        return own;
    }

    static string EaGameDir()
    {
        using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Electronic Arts\EA GAMES\Battlefield 1942");
        return k?.GetValue("GAMEDIR") as string ?? "";
    }

    public static void UseFolder(string dir)
    {
        Dir = FullPath(dir);
        Lib = Path.Combine(Dir, LibFolder);
    }

    // Remembered for the next start, only for a folder this installer's library is in: the uninstaller removes the
    // state key, and with it this value
    public static void SaveFolder()
    {
        if (LibraryFound) SetState(SavedFolder, Dir);
    }

    static string G(string rel) => Path.Combine(Dir, rel);
    static string L(string rel) => Path.Combine(Lib, rel);
    static string FontFile(int i) => L(@"Fonts\" + FontNames[i] + @"\Font.rfa");

    // Setup creates its shortcuts on the public desktop ({autodesktop} in an administrator install)
    static string DesktopShortcut(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name + ".lnk");
    const string MainShortcut = "Battlefield 1942";
    const string BorderlessShortcut = "Battlefield 1942 (Borderless)";

    // What this installer included. The required fixes are always there; extras the build left out have no copy.
    public static bool BorderlessAvailable => File.Exists(L(LibBL + @"\Borderless1942.exe"));
    public static bool CompatAvailable => File.Exists(L(LibCompat + @"\BF1942.sdb"));
    public static bool HiResAvailable => File.Exists(L(LibHiRes + @"\menu.rfa")) && File.Exists(L(LibOrig + @"\menu.rfa"));
    public static bool SirenAvailable => File.Exists(L(LibSiren + @"\Battle_of_Britain.rfa")) && File.Exists(L(LibOrig + @"\Battle_of_Britain.rfa"));
    // A game folder set up while the extra was on already has no original to turn it off with
    public static bool HiResNoOriginal => File.Exists(L(LibHiRes + @"\menu.rfa")) && !HiResAvailable;
    public static bool SirenNoOriginal => File.Exists(L(LibSiren + @"\Battle_of_Britain.rfa")) && !SirenAvailable;
    public static bool FontAvailable(int i) => File.Exists(FontFile(i));
    public static bool FontMissing => !File.Exists(G(FontRfa));
    public static string Art => Path.Combine(AppContext.BaseDirectory, "cover.bmp");
    public static bool VulkanCheckAvailable => File.Exists(L("VulkanCheck.exe"));
    public static string TroubleshootingPdf => G(@"manual\Battlefield 1942 Troubleshooting.pdf");

    // ---- Registry ----

    public static string State(string name)
    {
        using RegistryKey? k = Registry.LocalMachine.OpenSubKey(StateKey);
        return k?.GetValue(name)?.ToString() ?? "";
    }

    static void SetState(string name, string value)
    {
        using RegistryKey k = Registry.LocalMachine.CreateSubKey(StateKey);
        k.SetValue(name, value);
    }

    // The version of a fix in the library: versions.json in it (Setup and a standalone copy both put it there), or
    // the state key for a library from an older Setup
    public static string Version(string name)
    {
        string v = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(L("versions.json")));
            if (doc.RootElement.TryGetProperty(name, out JsonElement e)) v = e.GetString() ?? "";
        }
        catch { }
        return (v != "" ? v : State(name)).TrimStart('v');
    }

    public static string RendererName(Renderer r) => r switch
    {
        Renderer.DXVK => ("DXVK " + Version("VerDXVK")).Trim(),
        Renderer.DgVoodoo => ("dgVoodoo2 " + Version("VerDgVoodoo2")).Trim(),
        Renderer.None => "None (the game's own Direct3D)",
        _ => "Unknown",
    };

    // ---- Files ----

    static string Hash(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    // True when both files exist and have the same content
    static bool Same(string a, string b) =>
        File.Exists(a) && File.Exists(b) && new FileInfo(a).Length == new FileInfo(b).Length && Hash(a) == Hash(b);

    // The game folder's own files that Apply replaced or removed, kept in Backup in the library (this Apply's, for
    // the message that says so)
    const string LibBackup = "Backup";
    public static readonly List<string> BackedUp = new();

    // Keeps a file of the game folder in Backup before it is replaced or removed, unless it is one of the library's
    // copies (or already there): in a game folder Setup didn't make, d3d8.dll, dsound.dll and the like can be the
    // player's own, from another fix or mod, and could not be had back otherwise
    static void Backup(string gameRel)
    {
        string p = G(gameRel);
        if (!File.Exists(p) || !Directory.Exists(Lib)) return;
        string name = Path.GetFileName(gameRel);
        if (Directory.EnumerateFiles(Lib, name, SearchOption.AllDirectories).Any(f => Same(p, f))) return;
        string dest = L(Path.Combine(LibBackup, gameRel));
        // Another file of the player's own by that name was kept before: this one gets the time in its name
        if (File.Exists(dest))
            dest = Path.Combine(Path.GetDirectoryName(dest)!, Path.GetFileNameWithoutExtension(name) + " (" +
                   DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + ")" + Path.GetExtension(name));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(p, dest, true);
        BackedUp.Add(gameRel);
        Log("Kept the game folder's own " + gameRel + " in " + Path.GetRelativePath(Lib, dest));
    }

    static void Copy(string libRel, string gameRel)
    {
        string dest = G(gameRel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Backup(gameRel);
        if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
        File.Copy(L(libRel), dest, true);
        Log("Copied " + libRel + " to " + gameRel);
    }

    // Only when the game folder has none - keeps a file the player has edited, such as bf42++.ini
    static void CopyIfMissing(string libRel, string gameRel)
    {
        if (!File.Exists(G(gameRel))) Copy(libRel, gameRel);
    }

    // A graphics fix: its DLLs, and its config file only when the game folder has none (it keeps the player's)
    static void CopyFolder(string libFolder)
    {
        foreach (string f in Directory.GetFiles(L(libFolder)))
        {
            string name = Path.GetFileName(f), lib = Path.Combine(libFolder, name);
            if (name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) CopyIfMissing(lib, name);
            else Copy(lib, name);
        }
    }

    static void Remove(string gameRel)
    {
        string p = G(gameRel);
        if (!File.Exists(p)) return;
        Backup(gameRel);
        File.SetAttributes(p, FileAttributes.Normal);
        File.Delete(p);
        Log("Removed " + gameRel);
    }

    // Removes a file of the game folder only when it is the library's copy (one of them)
    static void RemoveIfFrom(string gameRel, params string[] libRels)
    {
        foreach (string libRel in libRels)
            if (Same(G(gameRel), L(libRel))) { Remove(gameRel); return; }
        if (File.Exists(G(gameRel))) Log("Kept " + gameRel + " (not from the installer)");
    }

    public static void Log(string text)
    {
        try { File.AppendAllText(L("Options.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text + "\r\n"); }
        catch { }
    }

    // Replaces every line that starts with Prefix (ignoring case and leading spaces), like Setup's SetConLine.
    // Latin-1 reads and writes every byte unchanged, whatever the file's code page.
    static void SetConLine(string path, string prefix, string line)
    {
        if (!File.Exists(path)) return;
        string[] lines = File.ReadAllLines(path, Encoding.Latin1);
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && lines[i] != line)
            {
                lines[i] = line;
                changed = true;
            }
        if (changed) File.WriteAllLines(path, lines, Encoding.Latin1);
    }

    // ---- What is on now ----

    public static Settings Read()
    {
        var s = new Settings();
        string d3d8 = G("d3d8.dll");
        if (!File.Exists(d3d8)) s.Renderer = Renderer.None;
        // DXVK needs its d3d9.dll as well, and each fix its config file (the player may have edited that, so it
        // only has to be there). Without them (an antivirus, or an Apply cut short) the files count as neither
        // fix, so picking the fix again puts them all back.
        else if (Same(d3d8, L(LibDXVK + @"\d3d8.dll")))
            s.Renderer = Same(G("d3d9.dll"), L(LibDXVK + @"\d3d9.dll")) && HasConfig(LibDXVK, "dxvk.conf") ? Renderer.DXVK : Renderer.Unknown;
        else if (Same(d3d8, L(LibDgV + @"\D3D8.dll")))
            s.Renderer = HasConfig(LibDgV, "dgVoodoo.conf") ? Renderer.DgVoodoo : Renderer.Unknown;
        else s.Renderer = Renderer.Unknown;

        // BF42++'s dsound.dll loads .\dsound_next.dll when it is there, so DSOAL sits behind it under that
        // name. Without BF42++, DSOAL is the dsound.dll itself. DSOAL also needs its OpenAL driver: without it
        // the switch shows Off, so turning it on puts the driver back.
        s.BF42pp = Same(G("dsound.dll"), L(LibBF42pp + @"\dsound.dll"));
        s.Audio = Same(G(s.BF42pp ? "dsound_next.dll" : "dsound.dll"), L(LibAudio + @"\dsound_next.dll")) &&
                  Same(G("dsoal-aldrv.dll"), L(LibAudio + @"\dsoal-aldrv.dll"));

        s.Font = -1;
        for (int i = 0; i < FontNames.Length; i++)
            if (Same(G(FontRfa), FontFile(i))) { s.Font = i; break; }

        s.HiResUI = Same(G(MenuRfa), L(LibHiRes + @"\menu.rfa"));
        s.NoSiren = Same(G(BobRfa), L(LibSiren + @"\Battle_of_Britain.rfa"));
        s.Borderless = File.Exists(G("Borderless1942.exe"));
        s.Compat = File.Exists(G("BF1942.sdb"));
        s.SkipIntro = ReadSkipIntro();
        s.Profile = ActiveProfile();
        s.PlayerName = ConValue(G(Path.Combine(SettingsDir, "Profiles", s.Profile, "GeneralOptions.con")), "game.setPlayerName");
        s.Plus = ReadPlus();
        return s;
    }

    // In the game folder, when the library has one
    static bool HasConfig(string libFolder, string name) =>
        File.Exists(G(name)) || !File.Exists(L(libFolder + @"\" + name));

    // From the state key, or from our desktop shortcut when an older Setup didn't write it
    public static bool ReadSkipIntro()
    {
        string skip = State("SkipIntro");
        if (skip != "") return skip == "1";
        string main = DesktopShortcut(MainShortcut);
        return OurShortcut(main) && Regex.IsMatch(ShortcutArguments(main), @"\+restart\s+1(?!\d)", RegexOptions.IgnoreCase);
    }

    // ---- Changing it ----

    // The game or Borderless1942, when it runs from anywhere - or only from this game folder: a copy of the game
    // in another folder doesn't use these files
    public static string? RunningProgram(bool thisFolderOnly)
    {
        foreach (string name in new[] { "BF1942", "Borderless1942" })
        {
            Process[] running = Process.GetProcessesByName(name);
            try
            {
                foreach (Process p in running)
                {
                    string? path = ProcessPath(p.Id);
                    // A path that can't be read counts as this folder's, to be safe
                    if (!thisFolderOnly || path == null || path.StartsWith(Dir + @"\", StringComparison.OrdinalIgnoreCase))
                        return name + ".exe";
                }
            }
            finally { foreach (Process p in running) p.Dispose(); }
        }
        return null;
    }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);

    // The exe a process runs, also a 64-bit one (Process.MainModule can't read those from this 32-bit process)
    public static string? ProcessPath(int processId)
    {
        IntPtr process = OpenProcess(0x1000, false, processId);   // PROCESS_QUERY_LIMITED_INFORMATION
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            int size = name.Capacity;
            return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString(0, size) : null;
        }
        finally { CloseHandle(process); }
    }

    public static void Apply(Settings from, Settings to)
    {
        BackedUp.Clear();
        if (to.Renderer != from.Renderer && to.Renderer != Renderer.Unknown)
        {
            // Only the DLLs go. dxvk.conf and dgVoodoo.conf stay, with the player's own settings, for when that fix
            // is picked again: only DXVK reads dxvk.conf and only dgVoodoo2 reads dgVoodoo.conf.
            foreach (string f in new[] { "d3d8.dll", "d3d9.dll" }) Remove(f);
            if (to.Renderer == Renderer.DXVK) CopyFolder(LibDXVK);
            if (to.Renderer == Renderer.DgVoodoo) CopyFolder(LibDgV);
            SetState("Renderer", RendererName(to.Renderer));
        }

        if (to.BF42pp != from.BF42pp || to.Audio != from.Audio)
        {
            // dsound.dll is BF42++'s, or DSOAL's without BF42++; dsound_next.dll is DSOAL's behind BF42++. A file
            // that already is the right one isn't touched, everything is copied before anything goes, and
            // dsound_next.dll before dsound.dll, so a copy that fails can't turn the other switch off.
            string? dsound = to.BF42pp ? LibBF42pp + @"\dsound.dll" : to.Audio ? LibAudio + @"\dsound_next.dll" : null;
            string? dsoundNext = to.BF42pp && to.Audio ? LibAudio + @"\dsound_next.dll" : null;
            if (to.BF42pp)
            {
                Copy(LibBF42pp + @"\bf42++BlackScreen.exe", "bf42++BlackScreen.exe");
                CopyIfMissing(LibBF42pp + @"\bf42++.ini", "bf42++.ini");
            }
            if (to.Audio)
            {
                Copy(LibAudio + @"\dsoal-aldrv.dll", "dsoal-aldrv.dll");
                CopyIfMissing(LibAudio + @"\alsoft.ini", "alsoft.ini");
            }
            if (dsoundNext != null && !Same(G("dsound_next.dll"), L(dsoundNext))) Copy(dsoundNext, "dsound_next.dll");
            if (dsound != null && !Same(G("dsound.dll"), L(dsound))) Copy(dsound, "dsound.dll");
            // Only the copies from the library go: a dsound.dll or dsound_next.dll of the player's own stays
            if (dsound == null) RemoveIfFrom("dsound.dll", LibBF42pp + @"\dsound.dll", LibAudio + @"\dsound_next.dll");
            if (dsoundNext == null) RemoveIfFrom("dsound_next.dll", LibAudio + @"\dsound_next.dll");
            if (!to.BF42pp) Remove("bf42++BlackScreen.exe");   // bf42++.ini stays, with the player's settings
            if (!to.Audio) Remove("dsoal-aldrv.dll");          // alsoft.ini stays too
        }

        if (to.Font != from.Font && to.Font >= 0)
            Copy(@"Fonts\" + FontNames[to.Font] + @"\Font.rfa", FontRfa);

        if (to.HiResUI != from.HiResUI)
            Copy(to.HiResUI ? LibHiRes + @"\menu.rfa" : LibOrig + @"\menu.rfa", MenuRfa);

        if (to.NoSiren != from.NoSiren)
            Copy(to.NoSiren ? LibSiren + @"\Battle_of_Britain.rfa" : LibOrig + @"\Battle_of_Britain.rfa", BobRfa);

        if (to.Compat != from.Compat)
        {
            if (to.Compat)
            {
                Copy(LibCompat + @"\BF1942.sdb", "BF1942.sdb");
                // Not registered means not on, so the file must not stay behind and make the switch show On
                try { Sdbinst("-q \"" + G("BF1942.sdb") + "\""); }
                catch { Remove("BF1942.sdb"); throw; }
            }
            else
            {
                // sdbinst fails for a profile that is not registered (any more, e.g. after a cleaner tool); the file
                // goes either way, so the switch can always be turned off
                try { Sdbinst("-q -u \"" + G("BF1942.sdb") + "\""); }
                catch (Exception ex) { Log("Ignored: " + ex.Message); }
                Remove("BF1942.sdb");
            }
        }

        if (to.Borderless != from.Borderless)
        {
            // The exe first and the .con line next, so a copy that an antivirus blocks doesn't leave the game
            // windowed while the switch shows Off; the shortcut last, as the switch doesn't depend on it
            if (to.Borderless) Copy(LibBL + @"\Borderless1942.exe", "Borderless1942.exe");
            else Remove("Borderless1942.exe");
            // Borderless1942 needs the game to run windowed; without it the game goes back to fullscreen
            SetConLine(G(VideoDefault), "renderer.setFullScreen", "renderer.setFullScreen " + (to.Borderless ? "0" : "1"));
            if (to.Borderless)
                SaveShortcut(DesktopShortcut(BorderlessShortcut), G("Borderless1942.exe"), BorderlessArguments(to.SkipIntro));
            else if (OurShortcut(DesktopShortcut(BorderlessShortcut)))
                DeleteShortcut(DesktopShortcut(BorderlessShortcut));
        }

        if (to.SkipIntro != from.SkipIntro)
        {
            SetState("SkipIntro", to.SkipIntro ? "1" : "0");
            // Only this game folder's shortcuts that exist (a server shortcut always skips the intro), keeping any
            // other arguments the player added
            string main = DesktopShortcut(MainShortcut);
            if (OurShortcut(main)) SetShortcutArguments(main, WithSkipIntro(ShortcutArguments(main), to.SkipIntro));
            string bl = DesktopShortcut(BorderlessShortcut);
            if (to.Borderless && OurShortcut(bl)) SetShortcutArguments(bl, WithSkipIntro(ShortcutArguments(bl), to.SkipIntro));
        }

        // Last, as turning BF42++ on above puts bf42++.ini in the game folder when it has none
        if (!to.PlusSameAs(from)) SavePlus(from.Plus, to.Plus);
    }

    // sdbinst.exe registers the compatibility database. From a 32-bit process, Sysnative is the real System32.
    static void Sdbinst(string args)
    {
        string sys = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative")
            : Environment.SystemDirectory;
        var psi = new ProcessStartInfo(Path.Combine(sys, "sdbinst.exe"), args) { UseShellExecute = false, CreateNoWindow = true };
        using Process p = Process.Start(psi)!;
        p.WaitForExit();
        Log("sdbinst " + args + " - exit code " + p.ExitCode);
        if (p.ExitCode != 0) throw new Exception("sdbinst.exe " + args + " failed with exit code " + p.ExitCode + ".");
    }

    // ---- Screen resolution (the same as Setup: real pixels, ignoring Windows display scaling) ----

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hDC, int index);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    public static (int Width, int Height, int Refresh) PrimaryDisplay()
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        int w = GetDeviceCaps(dc, 118);    // DESKTOPHORZRES
        int h = GetDeviceCaps(dc, 117);    // DESKTOPVERTRES
        int r = GetDeviceCaps(dc, 116);    // VREFRESH
        ReleaseDC(IntPtr.Zero, dc);
        if (w <= 0 || h <= 0) { w = GetSystemMetrics(0); h = GetSystemMetrics(1); }
        if (r <= 1) r = 60;
        return (w, h, r);
    }

    static string BorderlessArguments(bool skipIntro)
    {
        var (w, h, _) = PrimaryDisplay();
        return "-width " + w + " -height " + h + (skipIntro ? " +restart 1" : "");
    }

    // A shortcut's arguments with "+restart 1" (skip the intro videos) added at the end or taken out, and with
    // Borderless1942's -width and -height set, keeping whatever else the player added (such as +game XPack1)
    static string WithSkipIntro(string args, bool skip)
    {
        string rest = Regex.Replace(args, @"\s*\+restart\s+\d+", "", RegexOptions.IgnoreCase).Trim();
        return skip ? (rest + " +restart 1").Trim() : rest;
    }

    static string WithSize(string args, int width, int height)
    {
        string rest = Regex.Replace(args, @"\s*-(width|height)\s+\d+", "", RegexOptions.IgnoreCase).Trim();
        return ("-width " + width + " -height " + height + " " + rest).Trim();
    }

    // Sets game.setGameDisplayMode in every Video*.con under Mods, and the Borderless1942 shortcut's size
    public static string SetDisplayMode()
    {
        var (w, h, r) = PrimaryDisplay();
        string mode = "game.setGameDisplayMode " + w + " " + h + " 32 " + r;
        foreach (string f in Directory.GetFiles(G("Mods"), "Video*.con", SearchOption.AllDirectories))
            SetConLine(f, "game.setGameDisplayMode", mode);
        string bl = DesktopShortcut(BorderlessShortcut);
        if (File.Exists(G("Borderless1942.exe")) && OurShortcut(bl))
            SetShortcutArguments(bl, WithSize(ShortcutArguments(bl), w, h));
        Log("Display mode: " + mode);
        return w + " x " + h + " at " + r + " Hz";
    }

    // ---- Shortcuts (WScript.Shell, through late binding) ----
    // The trimmer warns about the reflection calls below, but they go through COM's IDispatch, not through .NET
    // metadata it could remove (BuiltInComInteropSupport is on in the project), so the warnings don't apply.

    const string ComLateBinding = "WScript.Shell is a COM object; late binding goes through IDispatch, which trimming does not affect";

    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = ComLateBinding)]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = ComLateBinding)]
    static object Shortcut(string path)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell")!;
        object shell = Activator.CreateInstance(t)!;
        try { return t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path })!; }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = ComLateBinding)]
    static void Set(object o, string name, object value) =>
        o.GetType().InvokeMember(name, BindingFlags.SetProperty, null, o, new[] { value });

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = ComLateBinding)]
    static void Save(object o, string path)
    {
        o.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, o, null);
        Log("Shortcut saved: " + path);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = ComLateBinding)]
    static string ShortcutArguments(string path)
    {
        if (!File.Exists(path)) return "";
        try
        {
            object o = Shortcut(path);
            return o.GetType().InvokeMember("Arguments", BindingFlags.GetProperty, null, o, null) as string ?? "";
        }
        catch { return ""; }
    }

    // Whether a shortcut belongs to this game folder: every install on the PC uses the same shortcut names
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = ComLateBinding)]
    static bool OurShortcut(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            object o = Shortcut(path);
            string target = o.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, o, null) as string ?? "";
            return target.StartsWith(Dir + @"\", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void SetShortcutArguments(string path, string args)
    {
        object o = Shortcut(path);
        Set(o, "Arguments", args);
        Save(o, path);
    }

    static void SaveShortcut(string path, string target, string args)
    {
        object o = Shortcut(path);
        Set(o, "TargetPath", target);
        Set(o, "Arguments", args);
        Set(o, "WorkingDirectory", Dir);
        Set(o, "IconLocation", G("BF1942.exe") + ",0");
        Save(o, path);
    }

    static void DeleteShortcut(string path)
    {
        if (!File.Exists(path)) return;
        File.Delete(path);
        Log("Shortcut removed: " + path);
    }

    // ---- Player name (the same as Setup's "Set your player name") ----
    // The name goes in a copy of the profile the game starts with, in Settings\Profiles\<name>, and the game is
    // switched to it. The game names a profile after its folder and runs its files with "run Profiles/<name>/...",
    // without quotes, so the folder name has no spaces or characters Windows doesn't allow.

    public const int MaxPlayerName = 32;
    const string SettingsDir = @"Mods\bf1942\Settings";

    // The value after Command on its first line in the file, without quotes ("" if there is none)
    static string ConValue(string path, string command)
    {
        if (!File.Exists(path)) return "";
        foreach (string line in File.ReadAllLines(path, Encoding.Latin1))
        {
            string s = line.Trim();
            if (s.StartsWith(command + " ", StringComparison.OrdinalIgnoreCase))
                return s[(command.Length + 1)..].Trim().Replace("\"", "");
        }
        return "";
    }

    // The profile the game starts with (Settings\Profile.con), or Custom, the one the game folder comes with
    static string ActiveProfile()
    {
        string p = ConValue(G(SettingsDir + @"\Profile.con"), "game.setProfile");
        return p == "" || !ProfileExists(p) ? "Custom" : p;
    }

    // The profile folder for a player name, e.g. "[MG] Nicole" -> "[MG]_Nicole"
    public static string ProfileFolderName(string name)
    {
        var s = new StringBuilder(name.Trim());
        for (int i = 0; i < s.Length; i++)
            if (" \\/:*?\"<>|".Contains(s[i])) s[i] = '_';
        // Windows drops a trailing dot, and doesn't allow device names
        if (s.Length > 0 && s[^1] == '.') s[^1] = '_';
        string folder = s.ToString();
        return Regex.IsMatch(folder, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase) ? folder + "_" : folder;
    }

    public static bool ProfileExists(string profile) => Directory.Exists(G(Path.Combine(SettingsDir, "Profiles", profile)));

    // null when the name can be used
    public static string? PlayerNameError(string name)
    {
        if (name == "") return "Type the player name you want to use in the game.";
        if (name.Length > MaxPlayerName) return $"The player name can have up to {MaxPlayerName} characters.";
        if (name.Contains('"')) return "The player name can't contain a double quote (\").";
        foreach (char c in name)
            if (c < ' ' || c > '~')
                return "The player name can only use English letters, numbers, spaces and the symbols on the keyboard " +
                       "(the game's settings files don't keep other characters).";
        return null;
    }

    // Copies the profile the game starts with to Profiles\<name> (a profile with that name that is already there is
    // kept as it is), sets the player name in it and makes it the one the game starts with. Returns the profile.
    public static string SetPlayerName(string name)
    {
        name = name.Trim();
        if (PlayerNameError(name) is { } error) throw new Exception(error);
        string settings = G(SettingsDir), oldProfile = ActiveProfile(), newProfile = ProfileFolderName(name);
        string newDir = Path.Combine(settings, "Profiles", newProfile);
        if (!Directory.Exists(newDir))
        {
            Log("Creating the profile " + newProfile + " from " + oldProfile);
            CopyDir(Path.Combine(settings, "Profiles", oldProfile), newDir);
        }
        string options = Path.Combine(newDir, "GeneralOptions.con");
        string line = "game.setPlayerName \"" + name + "\"";
        if (ConValue(options, "game.setPlayerName") != "") SetConLine(options, "game.setPlayerName", line);
        else File.AppendAllText(options, "\r\n" + line + "\r\n", Encoding.Latin1);
        if (!string.Equals(oldProfile, newProfile, StringComparison.Ordinal))
        {
            SwitchProfileInDir(settings, oldProfile, newProfile);
            SwitchProfileInDir(Path.Combine(settings, "Default"), oldProfile, newProfile);
        }
        Log("Player name " + name + " set in the profile " + newProfile + ", which the game now starts with");
        return newProfile;
    }

    static void CopyDir(string source, string dest, bool overwrite = false)
    {
        Directory.CreateDirectory(dest);
        if (!Directory.Exists(source)) return;
        foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite);
        foreach (string d in Directory.GetDirectories(source)) CopyDir(d, Path.Combine(dest, Path.GetFileName(d)), overwrite);
    }

    // Points the .con files in a folder that run a profile's files (Profile.con, Video.con, ...) at another profile
    static void SwitchProfileInDir(string dir, string oldProfile, string newProfile)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string f in Directory.GetFiles(dir, "*.con"))
        {
            string before = File.ReadAllText(f, Encoding.Latin1);
            string s = Regex.Replace(before, Regex.Escape("Profiles/" + oldProfile + "/"), ("Profiles/" + newProfile + "/").Replace("$", "$$"), RegexOptions.IgnoreCase);
            s = Regex.Replace(s, Regex.Escape("rem ** " + oldProfile + " **"), ("rem ** " + newProfile + " **").Replace("$", "$$"), RegexOptions.IgnoreCase);
            if (s != before) File.WriteAllText(f, s, Encoding.Latin1);
        }
        string profileCon = Path.Combine(dir, "Profile.con");
        SetConLine(profileCon, "game.setProfile", "game.setProfile \"" + newProfile + "\"");
    }

    // ---- Setting up a game folder (a standalone copy of the app) ----

    public static long BundledSize => Directory.GetFiles(Bundled, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    // Copies the app's library to Options in the game folder, so the folder works like one Setup installed. Nothing
    // in the game changes until Apply. Originals has the game's own menu.rfa and Battle_of_Britain.rfa, for turning
    // the extras that replace them off again: this folder's, unless they are the modified ones already, or else the
    // ones the library brings (from the game build.ps1 was run with, like Setup's). Other files of the folder's own
    // that Apply replaces or removes (a Font.rfa, d3d8.dll, dsound.dll, ...) go in Backup then (Game.Backup).
    // BF42++'s folder, which makes the folder count as set up, is copied last, so a copy that was cut short is
    // simply done again.
    public static void SetUp()
    {
        Directory.CreateDirectory(Lib);
        Log("Setting up this game folder from " + Bundled);
        foreach (string f in Directory.GetFiles(Bundled)) File.Copy(f, L(Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(Bundled).OrderBy(d => Path.GetFileName(d) == LibBF42pp))
        {
            string name = Path.GetFileName(d);
            if (name == LibBF42pp)
            {
                KeepOriginal(MenuRfa, L(LibHiRes + @"\menu.rfa"));
                KeepOriginal(BobRfa, L(LibSiren + @"\Battle_of_Britain.rfa"));
            }
            // Like Setup: Borderless1942 only runs on 64-bit Windows
            if (name == LibBL && !Environment.Is64BitOperatingSystem) continue;
            CopyDir(d, L(name), overwrite: true);
            Log("Copied " + name + " to the library");
        }
    }

    // Copies a game file to Originals, unless it is missing, already there, or the modified copy that replaces it
    // (or there is no modified copy in the library, so the original is never needed)
    static void KeepOriginal(string gameRel, string modified)
    {
        string dest = L(Path.Combine(LibOrig, Path.GetFileName(gameRel)));
        if (!File.Exists(G(gameRel)) || Same(G(gameRel), dest)) return;
        if (!File.Exists(modified) || Same(G(gameRel), modified)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(G(gameRel), dest, true);
        Log("Kept the game folder's own " + gameRel + " in " + LibOrig);
    }

    // ---- CD key (the same rules as Setup: 22 characters, A-Z and 0-9) ----

    const string ErgcKey = @"SOFTWARE\Electronic Arts\EA GAMES\Battlefield 1942\ergc";
    const string SerialChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static bool HasValidSerial()
    {
        using RegistryKey? k = Registry.LocalMachine.OpenSubKey(ErgcKey);
        string serial = (k?.GetValue("") as string ?? "").Trim();
        if (serial.Length != 22) return false;
        foreach (char c in serial.ToUpperInvariant())
            if (SerialChars.IndexOf(c) < 0) return false;
        return true;
    }

    // Registers a random serial. SerialCreated tells the uninstaller to remove it again, as it does for one Setup made.
    public static void GenerateSerial()
    {
        var chars = new char[22];
        for (int i = 0; i < chars.Length; i++) chars[i] = SerialChars[RandomNumberGenerator.GetInt32(SerialChars.Length)];
        using (RegistryKey k = Registry.LocalMachine.CreateSubKey(ErgcKey))
            k.SetValue("", new string(chars));
        SetState("SerialCreated", "1");
        Log("Generated a CD key");
    }

    // ---- Graphics card check (the same helper Setup runs) ----

    // Exit code 0 = DXVK supported, 1 = not supported, anything else = the check failed
    public static int RunVulkanCheck(out string details)
    {
        var psi = new ProcessStartInfo(L("VulkanCheck.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using Process p = Process.Start(psi)!;
        details = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Log("VulkanCheck exit code " + p.ExitCode);
        return p.ExitCode;
    }

    // ---- Starting things as the signed-in user ----
    // The app runs as administrator, and whatever it starts directly would too. Explorer runs as the user,
    // so the game is started through a shortcut that Explorer opens, and folders, the guide and links through Explorer.

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = ComLateBinding)]
    public static void Play(bool skipIntro, string server)
    {
        string args = skipIntro || server != "" ? "+restart 1" : "";
        if (server != "") args += " +joinServer " + server;
        // With Borderless1942 on, the game is set to run in a window, so it starts through Borderless1942 like its
        // desktop shortcut does (Borderless1942 passes the + arguments on to the game)
        string exe = File.Exists(G("Borderless1942.exe")) ? "Borderless1942.exe" : "BF1942.exe";
        if (exe == "Borderless1942.exe")
        {
            var (w, h, _) = PrimaryDisplay();
            args = WithSize(args, w, h);
        }
        string lnk = L("Play.lnk");
        object o = Shortcut(lnk);
        Set(o, "TargetPath", G(exe));
        Set(o, "Arguments", args);
        Set(o, "WorkingDirectory", Dir);
        o.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, o, null);
        Log("Play: " + exe + " " + args);
        OpenAsUser(lnk);
    }

    public static void OpenAsUser(string path) =>
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + path + "\""));
}
