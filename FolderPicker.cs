// The Windows folder picker (IFileOpenDialog). WinUI's own pickers don't open in an app that runs as administrator.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BF1942Options;

static class FolderPicker
{
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialog { }

    // Only the methods up to the last one used, in the interface's order
    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr filterSpec);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IntPtr item);

    const uint FOS_NOCHANGEDIR = 0x8, FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
    const uint SIGDN_FILESYSPATH = 0x80058000;
    const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    // The folder picked, or null when the player cancelled. It opens in startIn, when that folder exists.
    // The picker runs on a thread of its own: the window's thread is an ASTA, which turns away the calls that
    // UI Automation (screen readers) makes into a modal picker it shows. The owner window is disabled meanwhile.
    public static Task<string?> PickAsync(IntPtr owner, string title, string startIn)
    {
        var done = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(Pick(owner, title, startIn)); }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }

    static string? Pick(IntPtr owner, string title, string startIn)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
            dialog.SetTitle(title);
            if (Directory.Exists(startIn))
            {
                Guid shellItem = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(startIn, IntPtr.Zero, ref shellItem, out IntPtr pointer);
                try
                {
                    var folder = (IShellItem)Marshal.GetObjectForIUnknown(pointer);
                    dialog.SetFolder(folder);
                    Marshal.ReleaseComObject(folder);
                }
                finally { Marshal.Release(pointer); }
            }
            int hr = dialog.Show(owner);
            if (hr == ERROR_CANCELLED) return null;
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out IShellItem result);
            try
            {
                result.GetDisplayName(SIGDN_FILESYSPATH, out string path);
                return path;
            }
            finally { Marshal.ReleaseComObject(result); }
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }
}
