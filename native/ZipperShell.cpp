// Explorer context menu for .zip/.7z files: a "Zipper" submenu like WinRAR's, where "Extrair para" names the target folder.
// Also registered as a drag-and-drop handler on folders and drives: right-dragging archives there offers extracting into
// the drop folder.
// A static registry verb cannot do that (its label is fixed), hence this small COM handler. It only builds the menu;
// every command runs Zipper.exe (next to this DLL) with the same arguments ShellIntegration.cs handles.
// Build with native\build.ps1, which writes src\Zipper\tools\ZipperShell.dll.
#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <new>
#include <string>
#include <vector>

// same GUID as HandlerClsid in ShellIntegration.cs
static const CLSID CLSID_ZipperMenu = {0x55c77995, 0x1843, 0x4308, {0x9f, 0xff, 0xf1, 0x5e, 0x42, 0x74, 0x6f, 0xf7}};

static HMODULE module;
static LONG locks; // live objects + LockServer calls

enum Command { Open, Extract, ExtractFolder, ExtractHere, CommandCount };

static std::wstring ZipperExe()
{
    wchar_t path[MAX_PATH];
    GetModuleFileNameW(module, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    return std::wstring(path) + L"\\Zipper.exe";
}

// Doubles "&" so the menu does not turn it into a mnemonic.
static std::wstring Escape(const std::wstring& text)
{
    std::wstring escaped;
    for (auto c : text) escaped += c == L'&' ? L"&&" : std::wstring(1, c);
    return escaped;
}

// "C:\a\Rogerin.7z" -> "Rogerin"
std::wstring MenuStem(const std::wstring& file)
{
    std::wstring name = PathFindFileNameW(file.c_str());
    auto dot = name.rfind(L'.');
    if (dot != std::wstring::npos && dot > 0) name.resize(dot);
    return Escape(name);
}

// `target` is the drop folder when right-dragging ("" otherwise); there WinRAR shows the full path.
std::wstring ExtractFolderLabel(const std::vector<std::wstring>& files, const std::wstring& target)
{
    if (files.size() > 1) return L"Extrair cada arquivo para pasta separada";
    std::wstring folder = target.empty() ? L"" : Escape(target + (target.back() == L'\\' ? L"" : L"\\"));
    return L"Extrair para \"" + folder + MenuStem(files[0]) + L"\\\"";
}

static bool IsArchive(const std::wstring& file)
{
    auto extension = PathFindExtensionW(file.c_str());
    return lstrcmpiW(extension, L".zip") == 0 || lstrcmpiW(extension, L".7z") == 0;
}

// Zipper.exe's icon as a 32-bit bitmap, which menus draw with transparency.
static HBITMAP IconBitmap()
{
    HICON icon = nullptr;
    if (ExtractIconExW(ZipperExe().c_str(), 0, nullptr, &icon, 1) == 0 || !icon) return nullptr;
    int size = GetSystemMetrics(SM_CXSMICON);
    BITMAPINFO info = {};
    info.bmiHeader = {sizeof(BITMAPINFOHEADER), size, -size, 1, 32, BI_RGB};
    void* bits;
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
    if (bitmap)
    {
        HGDIOBJ old = SelectObject(dc, bitmap);
        DrawIconEx(dc, 0, 0, icon, size, size, 0, nullptr, DI_NORMAL);
        SelectObject(dc, old);
    }
    DeleteDC(dc);
    DestroyIcon(icon);
    return bitmap;
}

class Menu : public IShellExtInit, public IContextMenu
{
    LONG refs = 1;
    std::vector<std::wstring> files;
    std::wstring target; // drop folder when used as a drag-and-drop handler, "" for the archive's own menu
    HBITMAP icon = nullptr;

    void Add(HMENU menu, UINT position, UINT id, const wchar_t* text, HMENU submenu = nullptr)
    {
        MENUITEMINFOW item = {sizeof item};
        item.fMask = MIIM_ID | MIIM_STRING | MIIM_BITMAP | (submenu ? MIIM_SUBMENU : 0);
        item.wID = id;
        item.dwTypeData = const_cast<wchar_t*>(text);
        item.hbmpItem = icon;
        item.hSubMenu = submenu;
        InsertMenuItemW(menu, position, TRUE, &item);
    }

public:
    Menu() { InterlockedIncrement(&locks); }
    virtual ~Menu()
    {
        if (icon) DeleteObject(icon);
        InterlockedDecrement(&locks);
    }

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override
    {
        if (riid == IID_IUnknown || riid == IID_IShellExtInit) *out = static_cast<IShellExtInit*>(this);
        else if (riid == IID_IContextMenu) *out = static_cast<IContextMenu*>(this);
        else
        {
            *out = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&refs); }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG left = InterlockedDecrement(&refs);
        if (left == 0) delete this;
        return left;
    }

    // The selected (or dragged) files arrive as CF_HDROP; `folder` is the drop folder, null for a file's own menu.
    STDMETHODIMP Initialize(PCIDLIST_ABSOLUTE folder, IDataObject* data, HKEY) override
    {
        files.clear();
        target.clear();
        if (folder)
        {
            wchar_t path[MAX_PATH];
            if (!SHGetPathFromIDListW(folder, path)) return E_FAIL; // not a disk folder
            target = path;
        }
        if (!data) return E_INVALIDARG;
        FORMATETC format = {CF_HDROP, nullptr, DVASPECT_CONTENT, -1, TYMED_HGLOBAL};
        STGMEDIUM medium;
        if (FAILED(data->GetData(&format, &medium))) return E_INVALIDARG;
        if (auto drop = static_cast<HDROP>(GlobalLock(medium.hGlobal)))
        {
            UINT count = DragQueryFileW(drop, 0xFFFFFFFF, nullptr, 0);
            for (UINT i = 0; i < count; i++)
            {
                std::wstring path(DragQueryFileW(drop, i, nullptr, 0), L'\0');
                DragQueryFileW(drop, i, path.data(), static_cast<UINT>(path.size() + 1));
                files.push_back(path);
            }
            GlobalUnlock(medium.hGlobal);
        }
        ReleaseStgMedium(&medium);
        return files.empty() ? E_FAIL : S_OK;
    }

    STDMETHODIMP QueryContextMenu(HMENU menu, UINT index, UINT first, UINT, UINT flags) override
    {
        if (flags & CMF_DEFAULTONLY) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        // right-dragging anything that is not an archive onto a folder gets no menu
        if (!target.empty())
            for (auto& file : files)
                if (!IsArchive(file)) return MAKE_HRESULT(SEVERITY_SUCCESS, 0, 0);
        if (!icon) icon = IconBitmap();
        HMENU submenu = CreatePopupMenu();
        auto folder = ExtractFolderLabel(files, target);
        if (target.empty())
        {
            Add(submenu, 0, first + Open, L"Abrir com o Zipper");
            Add(submenu, 1, first + Extract, L"Extrair arquivos...");
            Add(submenu, 2, first + ExtractFolder, folder.c_str());
            Add(submenu, 3, first + ExtractHere, L"Extrair aqui");
        }
        else // WinRAR's order for a right-drag
        {
            Add(submenu, 0, first + Extract, L"Extrair arquivos...");
            Add(submenu, 1, first + ExtractHere, L"Extrair aqui");
            Add(submenu, 2, first + ExtractFolder, folder.c_str());
        }
        Add(menu, index, first + CommandCount, L"Zipper", submenu);
        return MAKE_HRESULT(SEVERITY_SUCCESS, 0, CommandCount + 1);
    }

    STDMETHODIMP InvokeCommand(CMINVOKECOMMANDINFO* info) override
    {
        if (HIWORD(info->lpVerb)) return E_FAIL; // called by verb name: we only have menu ids
        UINT command = LOWORD(info->lpVerb);
        static const wchar_t* arguments[] = {L"", L"--extract ", L"--extract-folder ", L"--extract-here "};
        if (command >= CommandCount) return E_FAIL;
        auto exe = ZipperExe();
        for (auto& file : files) // one process per archive, as the registry verbs did
        {
            std::wstring line = L"\"" + exe + L"\" " + arguments[command] + L"\"" + file + L"\"";
            // a trailing backslash would escape the closing quote ("C:\" -> C:"), so drive roots get a "."
            if (!target.empty()) line += L" \"" + target + (target.back() == L'\\' ? L"." : L"") + L"\"";
            STARTUPINFOW startup = {sizeof startup};
            PROCESS_INFORMATION process;
            if (!CreateProcessW(exe.c_str(), line.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
                return HRESULT_FROM_WIN32(GetLastError());
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
        return S_OK;
    }

    STDMETHODIMP GetCommandString(UINT_PTR, UINT, UINT*, CHAR*, UINT) override { return E_NOTIMPL; }
};

class Factory : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** out) override
    {
        if (riid != IID_IUnknown && riid != IID_IClassFactory)
        {
            *out = nullptr;
            return E_NOINTERFACE;
        }
        *out = this;
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; } // static object
    STDMETHODIMP_(ULONG) Release() override { return 1; }

    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** out) override
    {
        *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        auto menu = new (std::nothrow) Menu();
        if (!menu) return E_OUTOFMEMORY;
        HRESULT result = menu->QueryInterface(riid, out);
        menu->Release();
        return result;
    }

    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&locks);
        else InterlockedDecrement(&locks);
        return S_OK;
    }
};

static Factory factory;

extern "C" HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID riid, void** out)
{
    if (!IsEqualCLSID(clsid, CLSID_ZipperMenu))
    {
        *out = nullptr;
        return CLASS_E_CLASSNOTAVAILABLE;
    }
    return factory.QueryInterface(riid, out);
}

extern "C" HRESULT __stdcall DllCanUnloadNow() { return locks == 0 ? S_OK : S_FALSE; }

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}
