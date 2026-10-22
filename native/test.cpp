// Self-check for ZipperShell.dll: loads it the way Explorer does, builds the menu for real files and checks the labels.
// Usage: test.exe <ZipperShell.dll> <dir>   (creates Rogerin.7z and Outro & Mais.zip in <dir>)
#include <windows.h>
#include <shlobj.h>
#include <cstdio>
#include <string>
#include <vector>

static const CLSID CLSID_ZipperMenu = {0x55c77995, 0x1843, 0x4308, {0x9f, 0xff, 0xf1, 0x5e, 0x42, 0x74, 0x6f, 0xf7}};
typedef HRESULT(__stdcall* GetClassObject)(REFCLSID, REFIID, void**);
static int failures;

static void Check(bool ok, const wchar_t* what)
{
    wprintf(L"%ls %ls\n", ok ? L"ok  " : L"FAIL", what);
    if (!ok) failures++;
}

// `target` set = right-drag onto that folder
static std::vector<std::wstring> Labels(GetClassObject get, const std::vector<std::wstring>& files, const std::wstring& target = L"")
{
    std::vector<PIDLIST_ABSOLUTE> ids;
    for (auto& f : files) ids.push_back(ILCreateFromPathW(f.c_str()));
    IShellItemArray* items = nullptr;
    SHCreateShellItemArrayFromIDLists(static_cast<UINT>(ids.size()), const_cast<PCIDLIST_ABSOLUTE_ARRAY>(ids.data()), &items);
    IDataObject* data = nullptr;
    items->BindToHandler(nullptr, BHID_DataObject, IID_PPV_ARGS(&data));

    IClassFactory* factory = nullptr;
    get(CLSID_ZipperMenu, IID_PPV_ARGS(&factory));
    IShellExtInit* init = nullptr;
    factory->CreateInstance(nullptr, IID_PPV_ARGS(&init));
    PIDLIST_ABSOLUTE folder = target.empty() ? nullptr : ILCreateFromPathW(target.c_str());
    init->Initialize(folder, data, nullptr);
    if (folder) ILFree(folder);
    IContextMenu* menu = nullptr;
    init->QueryInterface(IID_PPV_ARGS(&menu));

    HMENU root = CreatePopupMenu();
    HRESULT added = menu->QueryContextMenu(root, 0, 100, 200, CMF_NORMAL);
    std::vector<std::wstring> labels;
    wchar_t text[256];
    if (GetMenuItemCount(root) > 0 && GetMenuStringW(root, 0, text, 256, MF_BYPOSITION)) labels.push_back(text);
    HMENU sub = GetSubMenu(root, 0);
    for (int i = 0; sub && i < GetMenuItemCount(sub); i++)
    {
        GetMenuStringW(sub, i, text, 256, MF_BYPOSITION);
        labels.push_back(text);
    }
    Check(HRESULT_CODE(added) == (labels.empty() ? 0 : 5), L"QueryContextMenu reports its ids");

    DestroyMenu(root);
    menu->Release();
    init->Release();
    data->Release();
    items->Release();
    for (auto id : ids) ILFree(id);
    return labels;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3) return 2;
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    HMODULE dll = LoadLibraryW(argv[1]);
    Check(dll != nullptr, L"DLL loads");
    if (!dll) return 1;
    auto get = reinterpret_cast<GetClassObject>(GetProcAddress(dll, "DllGetClassObject"));
    Check(get && GetProcAddress(dll, "DllCanUnloadNow"), L"COM entry points exported");

    std::wstring dir = argv[2], one = dir + L"\\Rogerin.7z", two = dir + L"\\Outro & Mais.zip", text = dir + L"\\nota.txt", drop = dir + L"\\Destino";
    CreateDirectoryW(drop.c_str(), nullptr);
    for (auto& f : {one, two, text}) CloseHandle(CreateFileW(f.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, 0, nullptr));

    auto single = Labels(get, {one});
    for (auto& l : single) wprintf(L"     %ls\n", l.c_str());
    Check(single.size() == 5 && single[0] == L"Zipper" && single[1] == L"Abrir com o Zipper" && single[2] == L"Extrair arquivos..."
          && single[3] == L"Extrair para \"Rogerin\\\"" && single[4] == L"Extrair aqui", L"single archive labels");

    auto amp = Labels(get, {two});
    Check(amp.size() == 5 && amp[3] == L"Extrair para \"Outro && Mais\\\"", L"& is escaped in the folder name");

    auto many = Labels(get, {one, two});
    Check(many.size() == 5 && many[3] == L"Extrair cada arquivo para pasta separada", L"several archives label");

    auto dragged = Labels(get, {one}, drop);
    for (auto& l : dragged) wprintf(L"     %ls\n", l.c_str());
    Check(dragged.size() == 4 && dragged[1] == L"Extrair arquivos..." && dragged[2] == L"Extrair aqui"
          && dragged[3] == L"Extrair para \"" + drop + L"\\Rogerin\\\"", L"right-drag labels show the drop folder");

    auto root = Labels(get, {one}, L"C:\\");
    Check(root.size() == 4 && root[3] == L"Extrair para \"C:\\Rogerin\\\"", L"drive root has a single backslash");

    Check(Labels(get, {one, text}, drop).empty(), L"no menu when a non-archive is dragged");

    wprintf(failures ? L"%d falha(s)\n" : L"tudo ok\n", failures);
    return failures ? 1 : 0;
}
