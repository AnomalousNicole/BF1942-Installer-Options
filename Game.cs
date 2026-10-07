// What BF1942 Options switches, and how. The window is in MainWindow.xaml; this file has no UI.
//
// Setup keeps a copy of every fix the app can turn on, plus the original game files that the extras replace,
// in a library folder inside the game folder, and installs the app in a subfolder of it. What is on is read
// from the game folder (a file counts as "on" when it is identical to its copy in the library), so the window
// always shows the real state, also after the player changed files by hand. An extra that the installer was built without has no copy, and its switch is disabled.
//
// options.json next to the exe (written by build.ps1) holds what differs between installers: the registry
// key Setup keeps its state in, whether a CD key may be generated, the server shortcut's name and address, and
// the community's Discord invite.
//
// The app is a 32-bit process, so HKLM\SOFTWARE is the same 32-bit view Setup writes to, and app.manifest
// asks for administrator rights: the game folder, HKLM and sdbinst (Compatibility Profile) all need them.
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
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

    public Settings Clone() => (Settings)MemberwiseClone();

    public bool SameAs(Settings o) =>
        Renderer == o.Renderer && BF42pp == o.BF42pp && Audio == o.Audio && HiResUI == o.HiResUI &&
        NoSiren == o.NoSiren && Borderless == o.Borderless && Compat == o.Compat &&
        SkipIntro == o.SkipIntro && Font == o.Font;
}

static class Game
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

    public static bool LibraryFound => IsLibrary(Lib);
    public static bool GameFound => HasGame(Dir);
    public static bool Installed => GameFound && LibraryFound;

    public static bool HasGame(string dir) => File.Exists(Path.Combine(dir, "BF1942.exe"));
    static bool IsLibrary(string folder) => Directory.Exists(Path.Combine(folder, LibBF42pp));
    public static string FullPath(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));

    // The folder the window opens with: the one the player last picked, the one the app is installed in, or the
    // one the game's registry entry names (Setup writes it too) - the first that has BF1942.exe
    static string StartFolder()
    {
        string own = Dir;
        foreach (Func<string> dir in new Func<string>[] { () => State(SavedFolder), () => own, EaGameDir })
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

    public static string Version(string name) => State(name).TrimStart('v');

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

    static void Copy(string libRel, string gameRel)
    {
        string dest = G(gameRel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
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
