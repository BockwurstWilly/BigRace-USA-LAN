// Pro Pinball - Big Race USA : LAN-Infos im Fenstertitel
// Liegt als dinput.dll im Spielordner. Das Spiel importiert daraus nur DirectInputCreateA; der Aufruf
// wird an die echte dinput.dll aus dem Systemordner weitergereicht. Es wird nichts injiziert.
// Ein Hintergrund-Thread schreibt die LAN-Infos in den Titel des Spielfensters. Die Texte schreibt der
// LAN-Launcher in den Shared Memory "Local\BigRaceLanBar"; ohne Launcher steht nur die eigene LAN-IP im Titel.
// Zusaetzlich werden neue DirectPlay-Dialoge des Spiels nach vorne geholt, damit sie nicht hinter dem
// Spielfenster verschwinden (dann wirkt das Spiel eingefroren).
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <iphlpapi.h>

#define BAR_MAGIC 0x4C524242    // "BBRL"
#define INFO_LEN 400
#define STATUS_LEN 200
#define TITLE_LEN (INFO_LEN + STATUS_LEN + 32)

typedef struct
{
    DWORD magic;
    DWORD heartbeat;            // GetTickCount() des Launchers beim letzten Schreiben
    WCHAR info[INFO_LEN];
    WCHAR status[STATUS_LEN];
} SharedBar;

static HMODULE realDinput;

// ---------------------------------------------------------------- Weiterleitung an die echte dinput.dll

static FARPROC RealProc(const char *name)
{
    if (!realDinput)
    {
        char path[MAX_PATH];
        GetSystemDirectoryA(path, MAX_PATH - 16);
        lstrcatA(path, "\\dinput.dll");
        realDinput = LoadLibraryA(path);
    }
    return realDinput ? GetProcAddress(realDinput, name) : NULL;
}

typedef HRESULT (WINAPI *Create4)(HINSTANCE, DWORD, void **, void *);
typedef HRESULT (WINAPI *Create5)(HINSTANCE, DWORD, const void *, void **, void *);

HRESULT WINAPI Proxy_DirectInputCreateA(HINSTANCE inst, DWORD version, void **out, void *outer)
{
    Create4 f = (Create4)RealProc("DirectInputCreateA");
    return f ? f(inst, version, out, outer) : E_FAIL;
}

HRESULT WINAPI Proxy_DirectInputCreateW(HINSTANCE inst, DWORD version, void **out, void *outer)
{
    Create4 f = (Create4)RealProc("DirectInputCreateW");
    return f ? f(inst, version, out, outer) : E_FAIL;
}

HRESULT WINAPI Proxy_DirectInputCreateEx(HINSTANCE inst, DWORD version, const void *iid, void **out, void *outer)
{
    Create5 f = (Create5)RealProc("DirectInputCreateEx");
    return f ? f(inst, version, iid, out, outer) : E_FAIL;
}

// ---------------------------------------------------------------- Fenster des Spiels

typedef struct { HWND best; int area; HWND dialogs[16]; int count; } Found;

static BOOL CALLBACK EnumProc(HWND h, LPARAM l)
{
    Found *f = (Found *)l;
    DWORD pid;
    char cls[32];
    RECT rc;
    GetWindowThreadProcessId(h, &pid);
    if (pid != GetCurrentProcessId() || !IsWindowVisible(h)) return TRUE;
    GetClassNameA(h, cls, sizeof(cls));
    if (lstrcmpA(cls, "#32770") == 0)
    {
        if (f->count < 16) f->dialogs[f->count++] = h;
    }
    else if (GetWindowRect(h, &rc))
    {
        int area = (rc.right - rc.left) * (rc.bottom - rc.top);
        if (area > f->area && rc.right - rc.left >= 200) { f->area = area; f->best = h; }
    }
    return TRUE;
}

// LAN-Adresse der Netzwerkkarte mit Standardgateway (Ethernet/WLAN, keine VPN-Adapter)
static void OwnIp(WCHAR *out, int len)
{
    ULONG size = 16384;
    IP_ADAPTER_ADDRESSES *list = (IP_ADAPTER_ADDRESSES *)HeapAlloc(GetProcessHeap(), 0, size), *a;
    lstrcpynW(out, L"nicht gefunden", len);
    if (!list) return;
    if (GetAdaptersAddresses(AF_INET, GAA_FLAG_INCLUDE_GATEWAYS | GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER, NULL, list, &size) == NO_ERROR)
    {
        for (a = list; a; a = a->Next)
        {
            if (a->OperStatus != IfOperStatusUp || !a->FirstGatewayAddress || !a->FirstUnicastAddress) continue;
            if (a->IfType != IF_TYPE_ETHERNET_CSMACD && a->IfType != IF_TYPE_IEEE80211) continue;
            {
                BYTE *ip = (BYTE *)&((struct sockaddr_in *)a->FirstUnicastAddress->Address.lpSockaddr)->sin_addr;
                wsprintfW(out, L"%u.%u.%u.%u", ip[0], ip[1], ip[2], ip[3]);
            }
            break;
        }
    }
    HeapFree(GetProcessHeap(), 0, list);
}

// ---------------------------------------------------------------- Hintergrund-Thread

static DWORD WINAPI Worker(LPVOID unused)
{
    HANDLE map = NULL;
    SharedBar *shared = NULL;
    HWND raised[32] = { 0 };
    int nextRaised = 0, ticks = 0;
    WCHAR ip[32] = L"", title[TITLE_LEN], current[TITLE_LEN];

    for (;; Sleep(250), ticks++)
    {
        Found f;
        int i, j;
        DWORD_PTR result;

        ZeroMemory(&f, sizeof(f));
        EnumWindows(EnumProc, (LPARAM)&f);

        // Neue DirectPlay-Dialoge einmal ueber alle Fenster legen
        for (i = 0; i < f.count; i++)
        {
            BOOL known = FALSE;
            for (j = 0; j < 32; j++) if (raised[j] == f.dialogs[i]) known = TRUE;
            if (known) continue;
            raised[nextRaised++ % 32] = f.dialogs[i];
            SetWindowPos(f.dialogs[i], HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(f.dialogs[i]);
        }
        if (!f.best) continue;

        // Titel: Infos vom Launcher, sonst nur die eigene IP
        if (!shared)
        {
            map = OpenFileMappingW(FILE_MAP_READ, FALSE, L"Local\\BigRaceLanBar");
            if (map) shared = (SharedBar *)MapViewOfFile(map, FILE_MAP_READ, 0, 0, sizeof(SharedBar));
            if (map && !shared) { CloseHandle(map); map = NULL; }
        }
        if (shared && shared->magic == BAR_MAGIC && GetTickCount() - shared->heartbeat < 3000)
        {
            WCHAR inf[INFO_LEN], sta[STATUS_LEN];
            lstrcpynW(inf, shared->info, INFO_LEN);
            lstrcpynW(sta, shared->status, STATUS_LEN);
            wsprintfW(title, L"%s     |     %s", inf, sta);
        }
        else
        {
            if (ticks % 40 == 0 || !ip[0]) OwnIp(ip, 32);
            wsprintfW(title, L"Deine IP: %s     |     Mitspieler-Anzeige: LAN-Launcher starten", ip);
        }

        // Nur bei Aenderung setzen (das Spiel kann seinen Titel selbst zuruecksetzen) - mit Timeout,
        // damit ein beschaeftigtes Spiel diesen Thread nicht blockiert
        current[0] = 0;
        InternalGetWindowText(f.best, current, TITLE_LEN);
        if (lstrcmpW(current, title) != 0)
            SendMessageTimeoutW(f.best, WM_SETTEXT, 0, (LPARAM)title, SMTO_ABORTIFHUNG, 500, &result);
    }
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE inst, DWORD reason, LPVOID reserved)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(inst);
        CloseHandle(CreateThread(NULL, 0, Worker, NULL, 0, NULL));
    }
    return TRUE;
}
