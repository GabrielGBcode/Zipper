using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Zipper;

// Drag source for files that are still being extracted: Explorer first asks for the list (FileGroupDescriptorW) and,
// on drop, for each file's bytes (FileContents), which waits for the background extraction to finish.
sealed class VirtualFiles(VirtualFiles.Item[] files, string root, Task ready) : System.Runtime.InteropServices.ComTypes.IDataObject
{
    public sealed record Item(string Path, bool IsDir, long Size); // Path is relative, with '/'

    static readonly short Descriptor = (short)DataFormats.GetFormat("FileGroupDescriptorW").Id;
    static readonly short Contents = (short)DataFormats.GetFormat("FileContents").Id;
    const int DV_E_FORMATETC = unchecked((int)0x80040064), E_NOTIMPL = unchecked((int)0x80004001), OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    const int EntrySize = 592; // sizeof(FILEDESCRIPTORW)

    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, bool create, IntPtr template, out IStream stream);
    [DllImport("shell32.dll")] static extern int SHCreateStdEnumFmtEtc(uint count, FORMATETC[] formats, out IEnumFORMATETC result);

    static FORMATETC Format(short id, TYMED tymed) => new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = tymed };

    internal IntPtr BuildDescriptor()
    {
        var handle = GlobalAlloc(0x42, (nuint)(4 + EntrySize * files.Length)); // GMEM_MOVEABLE | GMEM_ZEROINIT
        var p = GlobalLock(handle);
        Marshal.WriteInt32(p, files.Length);
        for (var i = 0; i < files.Length; i++)
        {
            var o = p + 4 + EntrySize * i;
            var f = files[i];
            Marshal.WriteInt32(o, 0x4 | 0x40 | 0x4000); // FD_ATTRIBUTES | FD_FILESIZE | FD_PROGRESSUI
            Marshal.WriteInt32(o + 36, f.IsDir ? 0x10 : 0x80);
            Marshal.WriteInt32(o + 64, (int)(f.Size >> 32));
            Marshal.WriteInt32(o + 68, (int)f.Size);
            var name = f.Path.Replace('/', '\\');
            for (var c = 0; c < Math.Min(name.Length, 259); c++) Marshal.WriteInt16(o + 72 + c * 2, name[c]);
        }
        GlobalUnlock(handle);
        return handle;
    }

    // keeps the UI alive while Explorer waits for the extraction
    void Wait()
    {
        while (!ready.IsCompleted)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        ready.GetAwaiter().GetResult();
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == Descriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildDescriptor();
        }
        else if (format.cfFormat == Contents && (format.tymed & TYMED.TYMED_ISTREAM) != 0 && format.lindex >= 0 && format.lindex < files.Length && !files[format.lindex].IsDir)
        {
            Wait();
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(Path.Combine(root, files[format.lindex].Path), 0, 0, false, IntPtr.Zero, out var stream));
            medium.tymed = TYMED.TYMED_ISTREAM;
            medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
        }
        else throw new COMException("Formato não suportado", DV_E_FORMATETC);
    }

    public int QueryGetData(ref FORMATETC format) =>
        format.cfFormat == Descriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 || format.cfFormat == Contents && (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? 0 : DV_E_FORMATETC;

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("", E_NOTIMPL);
        Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc(2, [Format(Descriptor, TYMED.TYMED_HGLOBAL), Format(Contents, TYMED.TYMED_ISTREAM)], out var result));
        return result;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return 0x00040130; // DATA_S_SAMEFORMATETC
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("", E_NOTIMPL);
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new COMException("", E_NOTIMPL); // the drag image helper tries this
    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection) => throw new COMException("", OLE_E_ADVISENOTSUPPORTED);
    public void DUnadvise(int connection) => throw new COMException("", OLE_E_ADVISENOTSUPPORTED);
    public int EnumDAdvise(out IEnumSTATDATA enumAdvise) => throw new COMException("", OLE_E_ADVISENOTSUPPORTED);
}
