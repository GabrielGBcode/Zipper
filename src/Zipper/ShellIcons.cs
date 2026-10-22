using System.Runtime.InteropServices;

namespace Zipper;

// Icon and type name the Windows shell uses for a file extension, cached per extension.
static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;
    const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    static readonly Dictionary<string, string> TypeNames = [];

    // Returns the ImageList key and the type name.
    public static (string Key, string Type) Get(string name, bool isDirectory, ImageList images)
    {
        var key = isDirectory ? "<pasta>" : Path.GetExtension(name).ToLowerInvariant();
        if (TypeNames.TryGetValue(key, out var type)) return (key, type);

        var info = new SHFILEINFO();
        SHGetFileInfo(isDirectory ? "pasta" : "arquivo" + key, isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
            ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
        if (info.hIcon != IntPtr.Zero)
        {
            using (var icon = Icon.FromHandle(info.hIcon)) images.Images.Add(key, (Icon)icon.Clone());
            DestroyIcon(info.hIcon);
        }
        return (key, TypeNames[key] = info.szTypeName ?? "");
    }
}
