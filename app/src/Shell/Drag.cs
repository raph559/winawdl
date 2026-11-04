using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Winawdl.Shell;

/// <summary>A file carried out of the window, the way Explorer carries one: its
/// own picture under the pointer, and a copy wherever it is let go.</summary>
public static class Drag
{
    private const int Copy = 1;
    private static readonly Guid DataObjectHandler = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");
    private static readonly Guid DataObjectInterface = new("0000010e-0000-0000-c000-000000000046");

    /// <summary>Carries `path` until it is let go. Returns false when the file is gone.</summary>
    public static bool Out(Visual from, string path)
    {
        if (!File.Exists(path)) return false;
        // Only a copy is offered: the shelf keeps showing the file, so it has to stay where it is.
        var data = ShellData(path);
        if (data != IntPtr.Zero)
        {
            try
            {
                var window = (PresentationSource.FromVisual(from) as HwndSource)?.Handle ?? IntPtr.Zero;
                if (SHDoDragDrop(window, data, IntPtr.Zero, Copy, out _) >= 0) return true;
            }
            finally
            {
                Marshal.Release(data);
            }
        }
        // Without the shell's own package the drag still carries the path, only without the picture.
        var files = new DataObject();
        files.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        DragDrop.DoDragDrop(from, files, DragDropEffects.Copy);
        return true;
    }

    /// <summary>What Explorer itself hands out for the file: the path, its
    /// shell identity, and the picture the drag shows.</summary>
    internal static IntPtr ShellData(string path)
    {
        try
        {
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out var item) != 0)
                return IntPtr.Zero;
            try
            {
                return item.BindToHandler(IntPtr.Zero, DataObjectHandler, DataObjectInterface, out var data) == 0
                    ? data
                    : IntPtr.Zero;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch (Exception error) when (error is COMException or ArgumentException)
        {
            return IntPtr.Zero;
        }
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid handler,
                          [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll")]
    private static extern int SHDoDragDrop(IntPtr window, IntPtr data, IntPtr source, int allowed, out int effect);
}
