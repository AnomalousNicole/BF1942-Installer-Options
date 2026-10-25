// The programs and Windows components the installer adds besides the game folder's files: the DirectX and Visual C++
// runtimes, DirectPlay, DataField42, Battlefield Rich Presence (with the .NET 8 Desktop Runtime) and PunkBuster. The
// app installs and removes them the way Setup and its uninstaller do (installer\BF1942-Installer.iss in
// BF1942-Installer), from installers in a Programs folder with programs.json naming each one's file and version:
// - the library's (Options\Programs in the game folder, which Setup installs and Set up copies; a standalone copy's
//   Library\Programs before then): DataField42, Battlefield Rich Presence and Punkbuster42, so every game folder can
//   add and remove them, whatever copy of the app runs
// - a standalone copy's own, next to the exe: Microsoft's runtimes, which the build downloads. Without them a card
//   says running the installer again installs it.
//
// The runtimes and DirectPlay are shared with other games and programs, so like Setup's uninstaller the app never
// removes them, and DirectX June 2010 has no uninstaller at all.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace BF1942Options;

// What the Programs tab shows for one program: whether it is there, a line about it, and what its buttons can do
sealed record ProgramState(bool Installed, string Text, bool CanInstall, string InstallLabel, bool CanRemove);

sealed class ProgramInfo
{
    public required string Id, Name, Credit, Description;
    public string Version = "";
    public string? Installer;   // full path to its installer, null when no Programs folder has it
    public bool Win64Only, NeedsGame, Shared;
}

static partial class Game
{
    const string LibPrograms = "Programs";

    // In the order the tab shows them
    public static readonly ProgramInfo[] Programs =
    {
        new ProgramInfo { Id = "directx", Name = "DirectX End-User Runtime", Credit = "Microsoft", Shared = true,
            Description = "The June 2010 DirectX libraries Battlefield 1942 and its fixes use. Installed only when missing." },
        new ProgramInfo { Id = "vcredist", Name = "Visual C++ Redistributable (x86)", Credit = "Microsoft", Shared = true,
            Description = "The Visual C++ runtime BF42++ and the other fixes need." },
        new ProgramInfo { Id = "directplay", Name = "DirectPlay", Credit = "Microsoft", Shared = true, Installer = "",
            Description = "The Windows feature the game uses for online and LAN games." },
        new ProgramInfo { Id = "datafield42", Name = "DataField42", Credit = "Ahrkylien", Win64Only = true, NeedsGame = true,
            Description = "Downloads the maps and mods a server needs when you join it." },
        new ProgramInfo { Id = "richpresence", Name = "Battlefield Rich Presence", Credit = "Gametools Network", Win64Only = true,
            Description = "Shows the server and map you are playing on in Discord. Needs the .NET 8 Desktop Runtime from Microsoft." },
        new ProgramInfo { Id = "punkbuster42", Name = "PunkBuster", Credit = "Even Balance, installed by the BF1942 community's Punkbuster42", NeedsGame = true,
            Description = "The anti-cheat some servers require. Its own installer opens: follow it, then finish the PunkBuster Services window." },
    };

    // The .NET 8 Desktop Runtime is installed with Battlefield Rich Presence, so it has no card of its own
    static readonly ProgramInfo DotNet8 = new() { Id = "dotnet8", Name = ".NET 8 Desktop Runtime (x64)", Credit = "Microsoft", Description = "", Shared = true };

    // The Programs folders, in the order an installer is looked for in: next to the exe, the game folder's library,
    // and a standalone copy's library (before Set up copies it)
    static string[] ProgramsDirs => new[] { Path.Combine(AppContext.BaseDirectory, LibPrograms), L(LibPrograms), Path.Combine(Bundled, LibPrograms) };

    // Each program's installer and version from the first Programs folder that has it. Again for every game folder.
    static void FindInstallers()
    {
        foreach (ProgramInfo p in Programs.Append(DotNet8))
        {
            if (p.Id == "directplay") continue;   // part of Windows: DISM turns it on
            p.Installer = null;
            p.Version = "";
        }
        foreach (string dir in ProgramsDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string json = Path.Combine(dir, "programs.json");
                if (!File.Exists(json)) continue;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
                foreach (ProgramInfo p in Programs.Append(DotNet8))
                {
                    if (p.Installer != null || !doc.RootElement.TryGetProperty(p.Id, out JsonElement e)) continue;
                    if (!e.TryGetProperty("file", out JsonElement f) || f.GetString() is not { Length: > 0 } file) continue;
                    string path = Path.Combine(dir, file);
                    if (!File.Exists(path)) continue;
                    p.Installer = path;
                    p.Version = e.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "" : "";
                }
            }
            catch { }
        }
    }

    // ---- What is there now (slow: run it off the UI thread) ----

    public static ProgramState CheckProgram(ProgramInfo p)
    {
        if (p.Win64Only && !Environment.Is64BitOperatingSystem)
            return new ProgramState(false, "Only available on 64-bit Windows.", false, "Install", false);
        ProgramState s = p.Id switch
        {
            "directx" => CheckDirectX(),
            "vcredist" => CheckVCRedist(p),
            "directplay" => CheckDirectPlay(),
            "datafield42" => CheckDataField42(),
            "richpresence" => CheckRichPresence(),
            "punkbuster42" => CheckPunkBuster(),
            _ => new ProgramState(false, "", false, "Install", false),
        };
        // Without its installer (the copy Setup installs has none), there is nothing to install or repair it with:
        // it says where to get it
        if (p.Installer == null)
        {
            if (s.CanInstall && !s.Installed)
                s = s with { Text = s.Text + " " + (Standalone ? "Not included in this download."
                    : s.InstallLabel == "Repair" ? "Running the installer again repairs it."
                    : p.Shared ? "Running the installer again installs it."
                    : SeparatePrograms == null || SeparatePrograms.Contains(p.Name) ? "Running the installer again (Custom) adds it."
                    : "Not included in this installer.") };
            s = s with { CanInstall = false };
        }
        if (p.NeedsGame && !GameFound) s = s with { Text = s.Text + " It goes in the game folder: click Browse and pick it first.", CanInstall = false, CanRemove = false };
        return s;
    }

    // The 32-bit system folder: SysWOW64 on 64-bit Windows, System32 on 32-bit
    static string SystemX86 => Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
    // The real System32, also from this 32-bit process (System32 is redirected to SysWOW64 for it)
    static string System64 => Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative")
        : Environment.SystemDirectory;

    static readonly string[] DirectXFiles = { "d3dx9_43.dll", "d3dx10_43.dll", "d3dx11_43.dll", "D3DCompiler_43.dll",
                                              "d3dcsx_43.dll", "xinput1_3.dll", "XAudio2_7.dll", "X3DAudio1_7.dll", "XAPOFX1_5.dll" };

    // Present when the June 2010 runtime's newest 32-bit DLLs are there (like Setup's DirectXNeeded)
    static ProgramState CheckDirectX()
    {
        int missing = DirectXFiles.Count(f => !File.Exists(Path.Combine(SystemX86, f)));
        return missing == 0
            ? new ProgramState(true, "Installed.", false, "Install", false)
            : new ProgramState(false, $"Not installed ({missing} of its files are missing).", true, "Install", false);
    }

    const string VCKey = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86";

    static bool VCFilesPresent => File.Exists(Path.Combine(SystemX86, "msvcp140.dll")) && File.Exists(Path.Combine(SystemX86, "vcruntime140.dll"));

    // The version installed (from its registry entry), or null when it isn't registered
    static System.Version? VCInstalled()
    {
        using RegistryKey? k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(VCKey);
        if (k?.GetValue("Installed") is not int installed || installed != 1) return null;
        return k.GetValue("Major") is int major && k.GetValue("Minor") is int minor && k.GetValue("Bld") is int bld
            ? new System.Version(major, minor, bld) : null;
    }

    static System.Version? VCBundled(ProgramInfo p)
    {
        if (p.Installer == null) return null;
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(p.Installer);
        return new System.Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    // Like Setup: registered and at least the version this download has, with its DLLs there (an entry can outlive
    // them, after a cleaner tool or a failed update: then it is repaired)
    static ProgramState CheckVCRedist(ProgramInfo p)
    {
        System.Version? installed = VCInstalled(), bundled = VCBundled(p);
        if (installed == null) return new ProgramState(false, "Not installed.", true, "Install", false);
        if (!VCFilesPresent) return new ProgramState(false, $"Version {installed} is registered, but its files are missing.", true, "Repair", false);
        if (bundled != null && installed < bundled)
            return new ProgramState(true, $"Version {installed} is installed. This download has {bundled}.", true, "Update", false);
        return new ProgramState(true, $"Version {installed} is installed.", false, "Install", false);
    }

    // Get-WindowsOptionalFeature says Enabled, Disabled or DisabledWithPayloadRemoved, in English on every Windows.
    // It needs the 64-bit PowerShell on 64-bit Windows.
    static ProgramState CheckDirectPlay()
    {
        string state = "";
        try
        {
            string ps = Path.Combine(System64, @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(ps, "-NoProfile -NonInteractive -Command \"(Get-WindowsOptionalFeature -Online -FeatureName DirectPlay).State\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using Process proc = Process.Start(psi)!;
            state = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
        }
        catch (Exception ex) { Log("DirectPlay state query failed: " + ex.Message); }
        return state switch
        {
            "Enabled" => new ProgramState(true, "Turned on.", false, "Turn on", false),
            "" => new ProgramState(false, "Its state could not be read. Turning it on is safe either way.", true, "Turn on", false),
            _ => new ProgramState(false, "Turned off.", true, "Turn on", false),
        };
    }

    // ---- DataField42 (one game folder per PC, like Setup's DataField42Dir) ----

    const string DataField42Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DataField42_is1";

    static string? DataField42Value(string name)
    {
        foreach (RegistryKey root in new[] { RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), Registry.CurrentUser })
        {
            using RegistryKey? k = root.OpenSubKey(DataField42Key);
            if (k?.GetValue(name) is string v && v != "") return v;
        }
        return null;
    }

    // BF1942.exe as it was before DataField42 patched it (in the library's Backup folder, made when needed)
    static string BeforeDataField42 => Path.Combine(Lib, LibBackup, "Before DataField42", "BF1942.exe");

    static string DataField42Dir => DataField42Value("Inno Setup: App Path") is { } dir ? FullPath(dir) : "";
    static bool DataField42Here => string.Equals(DataField42Dir, Dir, StringComparison.OrdinalIgnoreCase);

    static ProgramState CheckDataField42()
    {
        string dir = DataField42Dir;
        if (dir == "") return new ProgramState(false, "Not installed.", true, "Install", false);
        if (DataField42Here) return new ProgramState(true, "Installed in this game folder.", false, "Install", true);
        // Its installer stops with a message box when it is installed already, and it serves one folder
        return new ProgramState(false, "Installed for another game folder: " + dir + ". It can be in one folder at a time.", false, "Install", false);
    }

    // ---- Battlefield Rich Presence (an MSI) and .NET 8 ----

    // An installed program in Apps by (part of) its name: its uninstall key name (the ProductCode of an MSI)
    static string? FindUninstallEntry(string namePart)
    {
        var roots = new List<RegistryKey> { RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), Registry.CurrentUser };
        if (Environment.Is64BitOperatingSystem) roots.Insert(0, RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64));
        foreach (RegistryKey root in roots)
        {
            using RegistryKey? uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (string name in uninstall.GetSubKeyNames())
            {
                using RegistryKey? k = uninstall.OpenSubKey(name);
                if (k?.GetValue("DisplayName") is string display && display.Contains(namePart, StringComparison.OrdinalIgnoreCase)) return name;
            }
        }
        return null;
    }

    static ProgramState CheckRichPresence()
    {
        // .NET 8 comes from Microsoft: only a standalone copy brings its installer
        if (FindUninstallEntry("Battlefield Rich Presence") == null)
            return DotNet8Present() || DotNet8.Installer != null
                ? new ProgramState(false, "Not installed.", true, "Install", false)
                : new ProgramState(false, "Not installed. It needs the .NET 8 Desktop Runtime (x64) first: get it from Microsoft at dotnet.microsoft.com/download/dotnet/8.0, then come back here.", false, "Install", false);
        return DotNet8Present()
            ? new ProgramState(true, "Installed.", false, "Repair", true)
            : new ProgramState(false, "Installed, but the .NET 8 Desktop Runtime it needs is missing.", true, "Repair", true);
    }

    static string ProgramFiles64 => Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    // Like Setup: the .NET host and both shared frameworks of an 8.0 version, as an uninstall can leave empty folders
    static bool DotNet8Present()
    {
        string dotnet = Path.Combine(ProgramFiles64, "dotnet");
        string fxr = Path.Combine(dotnet, @"host\fxr"), desktop = Path.Combine(dotnet, @"shared\Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(fxr) || !Directory.Exists(desktop)) return false;
        if (!Directory.GetDirectories(fxr).Any(d => File.Exists(Path.Combine(d, "hostfxr.dll")))) return false;
        return Directory.GetDirectories(desktop, "8.0.*").Any(d =>
        {
            string ver = Path.GetFileName(d);
            return File.Exists(Path.Combine(d, "Microsoft.WindowsDesktop.App.deps.json")) &&
                   File.Exists(Path.Combine(dotnet, @"shared\Microsoft.NETCore.App", ver, "Microsoft.NETCore.App.deps.json"));
        });
    }

    // ---- PunkBuster (Punkbuster42 puts pb in the game folder and adds the PunkBuster Services) ----

    const string PBGameKey = "Punkbuster for Battlefield 1942";
    const string PBServicesKey = "PunkBusterSvc";

    static bool PBServicesInstalled
    {
        get
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PnkBstrA");
            return k != null;
        }
    }

    static ProgramState CheckPunkBuster()
    {
        bool here = File.Exists(G(@"pb\pbcl.dll")), services = PBServicesInstalled;
        if (here && services) return new ProgramState(true, "Installed in this game folder.", true, "Repair", true);
        if (here) return new ProgramState(false, "In this game folder, but the PunkBuster Services are missing.", true, "Repair", true);
        return new ProgramState(false, "Not installed in this game folder.", true, "Install", false);
    }

    // ---- Installing and removing (each logged to Options.log, or nowhere before the folder is set up) ----

    public static void InstallProgram(ProgramInfo p)
    {
        Log("Installing " + p.Name);
        switch (p.Id)
        {
            case "directx":
                // DXSETUP.exe runs from the folder with its .cab files
                RunAndCheck(p.Installer!, "/silent", Path.GetDirectoryName(p.Installer!)!);
                break;
            case "vcredist":
                // Registered as current but its DLLs gone: /install would do nothing, so it repairs
                bool repair = VCInstalled() is { } v && VCBundled(p) is { } b && v >= b && !VCFilesPresent;
                RunAndCheck(p.Installer!, (repair ? "/repair" : "/install") + " /quiet /norestart");
                if (!VCFilesPresent)
                    throw new Exception("The Visual C++ Redistributable could not restore msvcp140.dll and vcruntime140.dll. Open Settings > Apps > Installed apps, " +
                                        "find \"Microsoft Visual C++ Redistributable (x86)\", choose Modify and then Repair.");
                break;
            case "directplay":
                RunAndCheck(Path.Combine(System64, "dism.exe"), "/online /enable-feature /featurename:DirectPlay /all /norestart /quiet");
                break;
            case "datafield42":
                if (DataField42Dir != "" && !DataField42Here)
                    throw new Exception("DataField42 is installed for " + DataField42Dir + ". Remove it there first: it can be in one game folder at a time.");
                // It patches BF1942.exe to start DataField42 for a missing map, and its uninstaller leaves it so (Setup's
                // uninstaller deletes the whole folder), so Remove puts this copy back
                if (!File.Exists(BeforeDataField42))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(BeforeDataField42)!);
                    File.Copy(G("BF1942.exe"), BeforeDataField42);
                    Log("Kept BF1942.exe in " + Path.GetRelativePath(Lib, BeforeDataField42));
                }
                RunAndCheck(p.Installer!, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=\"" + Dir + "\"");
                // Its installer starts DataField42 when it finishes, also when silent - as administrator, as it ran from
                // this app. The game starts it by itself when a map or mod is missing.
                CloseDataField42();
                break;
            case "richpresence":
                if (!DotNet8Present())
                {
                    if (DotNet8.Installer == null) throw new Exception("The .NET 8 Desktop Runtime (x64) it needs is missing. Get it from Microsoft at https://dotnet.microsoft.com/download/dotnet/8.0 and try again.");
                    Log("Installing " + DotNet8.Name);
                    // The version is registered but its files are gone: /install would do nothing, so it repairs
                    using RegistryKey? reg = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
                    bool dnRepair = reg?.GetValue(DotNet8.Version) != null;
                    RunAndCheck(DotNet8.Installer, (dnRepair ? "/repair" : "/install") + " /quiet /norestart");
                    if (!DotNet8Present()) throw new Exception("The .NET 8 Desktop Runtime could not be installed. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and try again.");
                }
                if (FindUninstallEntry("Battlefield Rich Presence") == null)
                    RunAndCheck(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), "/i \"" + p.Installer + "\" /qn /norestart");
                break;
            case "punkbuster42":
                // Its own installer, with windows to follow; it installs in the folder it runs in
                using (Process proc = Process.Start(new ProcessStartInfo(p.Installer!) { UseShellExecute = false, WorkingDirectory = Dir })!)
                    proc.WaitForExit();
                // It exits while its "PunkBuster Services" window (pbsvc.exe) is still open
                Thread.Sleep(2000);
                foreach (Process svc in Process.GetProcessesByName("pbsvc"))
                    using (svc) svc.WaitForExit();
                if (!File.Exists(G(@"pb\pbcl.dll"))) throw new Exception("PunkBuster isn't in the game folder. Run it again and pick " + Dir + " as the game folder if it asks.");
                break;
        }
        Log(p.Name + " installed");
    }

    public static void RemoveProgram(ProgramInfo p)
    {
        Log("Removing " + p.Name);
        switch (p.Id)
        {
            case "datafield42":
                // Only from this game folder; it is closed first, so its uninstaller can delete it
                if (!DataField42Here || DataField42Value("UninstallString") is not { } cmd) return;
                CloseDataField42();
                RunAndCheck(cmd.Trim('"'), "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART");
                // Inno Setup uninstallers start a copy of themselves from %TEMP% and exit: wait until it is done
                for (int i = 0; i < 120 && DataField42Value("UninstallString") != null; i++) Thread.Sleep(500);
                // BF1942.exe without DataField42's patch, and DataField42's settings and logs, which its uninstaller leaves
                if (File.Exists(BeforeDataField42))
                {
                    File.SetAttributes(G("BF1942.exe"), FileAttributes.Normal);
                    File.Copy(BeforeDataField42, G("BF1942.exe"), true);
                    File.Delete(BeforeDataField42);
                    Directory.Delete(Path.GetDirectoryName(BeforeDataField42)!);
                    Log("Put back BF1942.exe as it was before DataField42");
                }
                // Installed by Setup or by the player, so there is no copy from before
                else if (UndoDataField42Patch()) Log("Took DataField42's patch out of BF1942.exe");
                if (Directory.Exists(G("DataField42"))) Directory.Delete(G("DataField42"), true);
                break;
            case "richpresence":
                // An MSI: its uninstall key is its ProductCode. .NET 8 stays, as other programs use it.
                if (FindUninstallEntry("Battlefield Rich Presence") is { } code && code.StartsWith('{'))
                    RunAndCheck(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), "/x " + code + " /qn /norestart");
                break;
            case "punkbuster42":
                RemovePunkBuster();
                break;
        }
        Log(p.Name + " removed");
    }

    // Exit codes that mean done: 3010 = done, restart Windows to finish; 1638 = a newer version is installed (VC++)
    static void RunAndCheck(string exe, string args, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        using Process p = Process.Start(psi)!;
        p.WaitForExit();
        Log(Path.GetFileName(exe) + " " + args + " - exit code " + p.ExitCode);
        if (p.ExitCode == 3010) RestartNeeded = true;
        else if (p.ExitCode != 0 && p.ExitCode != 1638)
            throw new Exception(Path.GetFileName(exe) + " failed with exit code " + p.ExitCode + ".");
    }

    public static bool RestartNeeded;

    // DataField42's patch to BF1942.exe (Bf1942ClientPatches.cs in Ahrkylien/BF1942-DataField42): bytes it writes over
    // the 1.61 BF1942.exe, at these offsets, and the game's own bytes there. It makes the game start DataField42.exe
    // when a map or mod is missing.
    static readonly (int Offset, string Patched, string Original)[] DataField42Patch =
    {
        (0x9494F, "E8BC75F7FF9090",
                  "8D8C2490000000"),
        (0x0BF10, "8D8C246CFFFFFF5550535152575689E989E581EC000300006A00A1AC1E97008D88D4070000FF15DC308C00508D460C508D8FD4020000FF15DC308C0089C131DBC68500FEFFFF228A04194388841D00FEFFFF84C075F1C6841D00FEFFFF22C6841D01FEFFFF008D8500FEFFFF508D8F2C020000FF15DC308C005031DBC68500FFFFFF228A83D0528C004388841D00FFFFFF84C075EEC6841D00FFFFFF22C6841D01FFFFFF008D8500FFFFFF5068F0BF400068E0BF400068E0BF40006A02FF15F8348C0083C41489EC5E5F5A595B585DC3446174614669656C6434322E657865006D617000",
                  "81ECF80000006846388C008D4C2404FF1534318C006846388C008D4C2420FF1534318C008D442400506A008D4C2424516A048D4C2448E85562170083C04068B84F8C0050FF15A4318C0083C4088D4C2438E80A6317008D4C241CFF151C318C008D4C2400FF151C318C0083C8FF81C4F8000000C210009090909090909090909081ECF80000006846388C008D4C2404FF1534318C006846388C008D4C2420FF1534318C008D442400506A008D4C2424516A048D4C2448E8D561170083C04068B84F8C0050FF15A4318C0083C4088D4C2438E88A6217008D4C241CFF151C318C008D4C2400"),
        (0x94EF5, "E8F671F7FF9090909090",
                  "C787B00200000B000000"),
        (0x0C0F0, "C787B00200000B0000005550535152575689E989E581EC000200006A00568D8FD4020000FF15DC308C0089C131DBC68500FEFFFF228A04194388841D00FEFFFF84C075F1C6841D00FEFFFF22C6841D01FEFFFF008D8500FEFFFF508D8F2C020000FF15DC308C005031DBC68500FFFFFF228A83D0528C004388841D00FFFFFF84C075EEC6841D00FFFFFF22C6841D01FFFFFF008D8500FFFFFF5068BEC1400068AEC1400068AEC140006A02FF15F8348C0083C41489EC5E5F5A595B585DC3446174614669656C6434322E657865006D6F6400",
                  "81ECF80000006846388C008D4C2404FF1534318C006846388C008D4C2420FF1534318C008D442400506A008D4C2424516A048D4C2448E87560170083C04068F04F8C0050FF15A4318C0083C4088D4C2438E82A6117008D4C241CFF151C318C008D4C2400FF151C318C0083C8FF81C4F8000000C214009090909090909090909081ECF80000006846388C008D4C2404FF1534318C006846388C008D4C2420FF1534318C008D442400506A008D4C2424516A048D4C2448E8F55F170083C040683C508C0050FF15A4318C0083C4088D4C2438E8")
    };

    // Puts the game's own bytes back, when BF1942.exe has all of DataField42's (false: it has none or not all of them)
    static bool UndoDataField42Patch()
    {
        string exe = G("BF1942.exe");
        byte[] data = File.ReadAllBytes(exe);
        foreach (var (offset, patched, _) in DataField42Patch)
        {
            byte[] bytes = Convert.FromHexString(patched);
            if (offset + bytes.Length > data.Length || !data.AsSpan(offset, bytes.Length).SequenceEqual(bytes)) return false;
        }
        foreach (var (offset, _, original) in DataField42Patch)
            Convert.FromHexString(original).CopyTo(data, offset);
        File.SetAttributes(exe, FileAttributes.Normal);
        File.WriteAllBytes(exe, data);
        return true;
    }

    // DataField42 running from this game folder is closed (like Setup's CloseDataField42)
    static void CloseDataField42()
    {
        string exe = G("DataField42.exe");
        for (int tries = 0; tries < 20; tries++)
        {
            bool running = false;
            foreach (Process p in Process.GetProcessesByName("DataField42"))
                using (p)
                    if (string.Equals(ProcessPath(p.Id), exe, StringComparison.OrdinalIgnoreCase))
                    {
                        running = true;
                        try { p.Kill(); } catch { }
                    }
            if (!running) return;
            Thread.Sleep(250);
        }
    }

    // BF1942's PunkBuster entry and its pb folder go; the PunkBuster Services only when no other game uses them
    // (like Setup's uninstaller)
    static void RemovePunkBuster()
    {
        foreach (Process p in Process.GetProcessesByName("pbsvc"))
            using (p)
                if (ProcessPath(p.Id) is { } path && path.StartsWith(Dir + @"\", StringComparison.OrdinalIgnoreCase))
                    try { p.Kill(); p.WaitForExit(5000); } catch { }
        using (RegistryKey uninstall = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                   .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true)!)
        {
            using RegistryKey? entry = uninstall.OpenSubKey(PBGameKey);
            if (entry?.GetValue("UninstallString") is string cmd && cmd.Trim('"').StartsWith(Dir + @"\", StringComparison.OrdinalIgnoreCase))
            {
                entry.Dispose();
                uninstall.DeleteSubKeyTree(PBGameKey, false);
                string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), PBGameKey);
                if (Directory.Exists(startMenu)) Directory.Delete(startMenu, true);
                Log("Removed " + PBGameKey + " from Apps");
            }
        }
        if (Directory.Exists(G("pb")))
        {
            Directory.Delete(G("pb"), true);
            Log("Removed pb");
        }
        // Punkbuster42 leaves its services' setup next to the game
        if (File.Exists(G("pbsvc.exe")))
        {
            File.Delete(G("pbsvc.exe"));
            Log("Removed pbsvc.exe");
        }
        if (OtherPunkBusterGame() is { } other) Log("Kept the PunkBuster Services: " + other + " uses them");
        else RemovePunkBusterServices();
    }

    static void RemovePunkBusterServices()
    {
        Log("Removing the PunkBuster Services");
        string sc = Path.Combine(System64, "sc.exe");
        foreach (string args in new[] { "stop PnkBstrA", "stop PnkBstrB", "delete PnkBstrA", "delete PnkBstrB" })
            using (Process p = Process.Start(new ProcessStartInfo(sc, args) { UseShellExecute = false, CreateNoWindow = true })!)
                p.WaitForExit();
        // The services take a moment to stop and let go of their files
        string[] files = { "PnkBstrA.exe", "PnkBstrB.exe", "PnkBstrK.sys", "pbsvc.exe" };
        for (int tries = 0; tries < 20; tries++)
        {
            bool left = false;
            foreach (string f in files.Select(f => Path.Combine(SystemX86, f)).Where(File.Exists))
                try { File.Delete(f); } catch { left = true; }
            if (!left) break;
            Thread.Sleep(500);
        }
        using RegistryKey? uninstall = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true);
        uninstall?.DeleteSubKeyTree(PBServicesKey, false);
    }

    // Another game that uses PunkBuster (a folder other than this one with pb\pbcl.dll, or another game's PunkBuster
    // entry in Apps), looked for where Setup's uninstaller looks: Apps, EA's registry keys, the folders games are
    // usually installed in, and every Steam library. Null when there is none.
    static string? OtherPunkBusterGame()
    {
        bool Other(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                dir = FullPath(dir.Trim().Trim('"'));
                return !string.Equals(dir, Dir, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, @"pb\pbcl.dll"));
            }
            catch { return false; }
        }
        string? InSubfolders(string? parent)
        {
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return null;
            try { return Directory.GetDirectories(parent).FirstOrDefault(Other); }
            catch { return null; }
        }

        var roots = new List<RegistryKey> { RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), Registry.CurrentUser };
        if (Environment.Is64BitOperatingSystem) roots.Insert(0, RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64));
        foreach (RegistryKey root in roots)
        {
            using RegistryKey? uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;
            foreach (string name in uninstall.GetSubKeyNames())
            {
                using RegistryKey? k = uninstall.OpenSubKey(name);
                if (k == null) continue;
                // Another game's "Punkbuster for <game>" (this game's was removed before)
                if (!string.Equals(name, PBServicesKey, StringComparison.OrdinalIgnoreCase) &&
                    k.GetValue("DisplayName") is string display && display.Contains("punkbuster", StringComparison.OrdinalIgnoreCase))
                    return display;
                if (Other(k.GetValue("InstallLocation") as string)) return (string)k.GetValue("InstallLocation")!;
                if (k.GetValue("UninstallString") is string cmd && Regex.Match(cmd, @"^\s*""?([^""]+?\.exe)", RegexOptions.IgnoreCase) is { Success: true } m &&
                    Other(Path.GetDirectoryName(m.Groups[1].Value)))
                    return Path.GetDirectoryName(m.Groups[1].Value);
            }
        }
        RegistryKey hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        foreach (string parent in new[] { @"SOFTWARE\Electronic Arts\EA GAMES", @"SOFTWARE\Electronic Arts" })
        {
            using RegistryKey? ea = hklm32.OpenSubKey(parent);
            if (ea == null) continue;
            foreach (string name in ea.GetSubKeyNames())
            {
                using RegistryKey? k = ea.OpenSubKey(name);
                foreach (string value in new[] { "GAMEDIR", "Install Dir" })
                    if (Other(k?.GetValue(value) as string)) return (string)k!.GetValue(value)!;
            }
        }
        string drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string? parent in new[] { Path.GetDirectoryName(Dir), Path.Combine(drive, "EA Games"), Path.Combine(pf86, "EA Games"),
                                           Path.Combine(pf86, "Electronic Arts"), Path.Combine(ProgramFiles64, "EA Games") })
            if (InSubfolders(parent) is { } found) return found;

        // Steam: its own steamapps\common and every library in libraryfolders.vdf
        using RegistryKey? steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        string steam = (steamKey?.GetValue("SteamPath") as string ?? Path.Combine(pf86, "Steam")).Replace('/', '\\');
        var libraries = new List<string> { steam };
        try
        {
            string vdf = File.ReadAllText(Path.Combine(steam, @"steamapps\libraryfolders.vdf"));
            foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        catch { }
        foreach (string library in libraries)
            if (InSubfolders(Path.Combine(library, @"steamapps\common")) is { } found) return found;
        return null;
    }
}
