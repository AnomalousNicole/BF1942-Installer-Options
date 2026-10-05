using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.UI.Xaml;

namespace BF1942Options;

public partial class App : Application
{
    Window? window;
    static Mutex? instance;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string windowName);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);

    public App()
    {
        InitializeComponent();
        // Anything that was not handled where it happened goes to Options.log, so a crash can be looked into
        UnhandledException += (_, e) => Game.Log("Unexpected error: " + e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // One window per game folder: starting the app again (a double-click, or the desktop and the Start menu
        // shortcut both used) brings the open window to the front, instead of a second copy changing the same files
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Game.Dir.ToUpperInvariant())))[..16];
        instance = new Mutex(true, @"Local\BF1942Options-" + key, out bool first);
        if (!first)
        {
            // The window of this install's app: another install's app can be open too, with the same title
            IntPtr open = IntPtr.Zero;
            while ((open = FindWindowEx(IntPtr.Zero, open, null, "Battlefield 1942 Options")) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(open, out int processId);
                if (string.Equals(Game.ProcessPath(processId), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) break;
            }
            if (open != IntPtr.Zero)
            {
                if (IsIconic(open)) ShowWindow(open, 9);   // SW_RESTORE when minimized; a maximized window stays so
                SetForegroundWindow(open);
            }
            Exit();
            return;
        }
        window = new MainWindow();
        window.Activate();
    }
}
