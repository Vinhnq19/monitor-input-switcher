using System.IO;
using System.Runtime.InteropServices;

namespace MonitorInputSwitcher;

internal static class ShellRecycle
{
    public static bool Recycle(string path)
    {
        LanFileTree.VerifyLocalPath(path);
        ShellItem? source = null, parent = null, bin = null, recycled = null;
        object? transfer = null;
        nint pidl = 0;
        try
        {
            Guid iid = typeof(ShellItem).GUID;
            Guid recycleFolder = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out source));
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetDirectoryName(path)!, 0, ref iid, out parent));
            Marshal.ThrowExceptionForHR(SHGetKnownFolderIDList(ref recycleFolder, 0, 0, out pidl));
            Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(pidl, ref iid, out bin));

            Guid handler = new("D5E346A1-F753-4932-B403-4574800E2498");
            Guid transferId = typeof(TransferSource).GUID;
            Marshal.ThrowExceptionForHR(parent.BindToHandler(0, ref handler, ref transferId, out transfer));
            int result = ((TransferSource)transfer).RecycleItem(source, bin, 0, out recycled);
            Marshal.ThrowExceptionForHR(result);
            return recycled is not null && !File.Exists(path) && !Directory.Exists(path);
        }
        finally
        {
            LanFileClipboard.Release(recycled);
            LanFileClipboard.Release(transfer);
            LanFileClipboard.Release(bin);
            LanFileClipboard.Release(parent);
            LanFileClipboard.Release(source);
            if (pidl != 0) Marshal.FreeCoTaskMem(pidl);
        }
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ShellItem
    {
        [PreserveSig] int BindToHandler(nint bindContext, ref Guid handler, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out object value);
    }

    [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface TransferSource
    {
        [PreserveSig] int Advise(nint sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetProperties(nint properties);
        [PreserveSig] int OpenItem(nint item, uint flags, ref Guid iid, out nint value);
        [PreserveSig] int MoveItem(nint source, nint destination, nint name, uint flags, out nint value);
        [PreserveSig] int RecycleItem(ShellItem source, ShellItem destination, uint flags, out ShellItem value);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint context, ref Guid iid,
        out ShellItem item);
    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderIDList(ref Guid id, uint flags, nint token, out nint pidl);
    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromIDList(nint pidl, ref Guid iid,
        out ShellItem item);
}
