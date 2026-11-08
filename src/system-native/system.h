#pragma once
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
// Windows networking headers require this order; do not sort these includes.
#include <winsock2.h>
#include <windows.h>
#include <ws2ipdef.h>
#include <iphlpapi.h>
#include <netioapi.h>
#include <powrprof.h>
#include <dwmapi.h>
#include <shellscalingapi.h>
#include <sddl.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Data.Json.h>
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <set>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace magic::system
{
using namespace winrt::Windows::Data::Json;
inline constexpr wchar_t Protocol[] = L"Magic.System.Native/1";
inline constexpr DWORD FrameLimit = 1024 * 1024;
struct Handle
{
    HANDLE value = nullptr;
    Handle() = default;
    explicit Handle(HANDLE h) : value(h)
    {
    }
    ~Handle()
    {
        if (value && value != INVALID_HANDLE_VALUE)
            CloseHandle(value);
    }
    Handle(Handle const &) = delete;
    Handle &operator=(Handle const &) = delete;
    operator HANDLE() const
    {
        return value;
    }
};
struct ChangeState
{
    Handle changed{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    Handle stop{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::atomic<uint64_t> revision{1};
    std::atomic<unsigned> topics{0};
    void Notify(unsigned topic = 1)
    {
        topics.fetch_or(topic);
        revision.fetch_add(1);
        SetEvent(changed);
    }
};
inline void Check(bool ok)
{
    if (!ok)
        winrt::throw_last_error();
}
inline uint64_t Ticks(FILETIME f)
{
    return (uint64_t(f.dwHighDateTime) << 32) | f.dwLowDateTime;
}
inline uint64_t Created(HANDLE process)
{
    FILETIME c{}, e{}, k{}, u{};
    Check(GetProcessTimes(process, &c, &e, &k, &u) != FALSE);
    return Ticks(c);
}
inline std::wstring Image(HANDLE process)
{
    std::wstring s(32768, L'\0');
    DWORD n = DWORD(s.size());
    Check(QueryFullProcessImageNameW(process, 0, s.data(), &n) != FALSE);
    s.resize(n);
    return s;
}
inline void Text(JsonObject const &o, wchar_t const *key, std::wstring_view v)
{
    o.Insert(key, JsonValue::CreateStringValue(winrt::hstring{v}));
}
inline void Number(JsonObject const &o, wchar_t const *key, double v)
{
    o.Insert(key, JsonValue::CreateNumberValue(v));
}
inline void Flag(JsonObject const &o, wchar_t const *key, bool v)
{
    o.Insert(key, JsonValue::CreateBooleanValue(v));
}
inline std::wstring RequiredText(JsonObject const &o, wchar_t const *key, size_t max = 256)
{
    auto v = o.GetNamedString(key);
    if (v.empty() || v.size() > max)
        throw std::invalid_argument("Invalid string field: " + winrt::to_string(key));
    return std::wstring(v);
}
struct WindowInfo
{
    HWND hwnd{};
    DWORD pid{};
    uint64_t created{};
    std::wstring id, title, path, aumid;
    RECT rect{};
    UINT dpi = 96;
    bool minimized = false;
};
JsonArray ReadBatteryDetails();
void ReadPowerMode(JsonObject const &,SYSTEM_POWER_STATUS const &);
class Metrics;
class Broker
{
    static inline Broker *current = nullptr;
    std::unique_ptr<Metrics> metrics;
    std::map<uint64_t, std::pair<uint64_t, uint64_t>> priorNetwork;
    ULONGLONG networkTick = 0;
    ULONGLONG batteryDetailsTick = 0;
    JsonArray batteryDetails;
    HWND relay = nullptr;
    WindowInfo authorization{};
    // One short-lived input target, independent of the taskbar window list.
    // Desktop/tool windows can receive input without being Dock applications.
    WindowInfo inputTarget{};
    DWORD inputThread = 0;
    ULONGLONG inputUntil = 0;
    uint64_t inputGeneration = 0;
    std::atomic<uintptr_t> movingWindow{0};
    HHOOK keyboardHook = nullptr;
    bool winHeld = false, winCombo = false, swallowA = false, capturedChord = false;
    bool shortcutsSubscribed = false;
    std::atomic<unsigned> shortcutModes{0}; // 1: standalone Win, 2: Win+A
    std::atomic<bool> shortcutHookActive{false};
    std::atomic<uint64_t> quickSettingsEpoch{0};
    DWORD foregroundDelegate;
    bool startHotkeyRegistered = false;
    std::vector<unsigned> pendingShortcutModes;
    unsigned winKeys = 0;
    std::atomic<uint64_t> startMenuEpoch{0};
    static LRESULT CALLBACK KeyboardEvent(int, WPARAM, LPARAM);
    std::vector<HWINEVENTHOOK> hooks;
    HPOWERNOTIFY ac = nullptr, battery = nullptr, saver = nullptr;
    HANDLE network = nullptr;
    std::map<HWND, WindowInfo> windows;
    std::map<HWND, WindowInfo> *collecting = nullptr;
    bool systemListeners = false;
    std::atomic<bool> windowSessionEvents{false};
    std::atomic<bool> windowLimitReached{false};
    std::set<HWND> pending;
    bool overflow = false;
    std::mutex gate;
    std::mutex samplingGate;
    std::atomic<bool> wantWindows = false, wantSystem = false;
    std::atomic<unsigned> hookCount{0}, powerCount{0};
    std::atomic<bool> networkActive{false};
    std::atomic<uint64_t> events{0}, enumerations{0}, samples{0};
    mutable std::atomic<uint64_t> identityReadFailures{0};
    uint64_t priorIdle = 0, priorKernel = 0, priorUser = 0, windowGeneration = 0;
    static LRESULT CALLBACK WindowProc(HWND, UINT, WPARAM, LPARAM);
    static void CALLBACK WindowEvent(HWINEVENTHOOK, DWORD, HWND, LONG, LONG, DWORD, DWORD);
    static void CALLBACK NetworkEvent(PVOID, MIB_IPINTERFACE_ROW *, MIB_NOTIFICATION_TYPE);
    static BOOL CALLBACK Enumerate(HWND, LPARAM);
    void ConfigureOnThread();
    void Refresh();
    void Flush();
    bool ReadWindow(HWND, WindowInfo &) const;
    void Signal()
    {
        wake->Notify();
    }

  public:
    std::shared_ptr<ChangeState> wake = std::make_shared<ChangeState>();
    Handle &changed = wake->changed;
    Handle &stop = wake->stop;
    explicit Broker(DWORD foregroundDelegate = 0);
    ~Broker();
    void Configure(bool windowsEnabled, bool systemEnabled);
    void ConfigureShortcuts(bool enabled);
    void SetShortcutModes(bool win, bool winA);
    void ConfigureMetrics(bool gpu, bool storage);
    JsonObject Snapshot(bool sampleSystem);
    JsonObject Health();
    JsonObject Action(JsonObject const &request);
    WindowInfo ResolveWindow(std::wstring const &id);
    JsonObject CaptureInputTarget(DWORD popupProcess);
    WindowInfo ResolveInputWindow(std::wstring const &id);
    uint64_t Revision() const
    {
        return wake->revision.load();
    }
    bool SystemDemand() const
    {
        return wantSystem.load();
    }
    void ResetChanged()
    {
        std::scoped_lock lock(gate);
        ResetEvent(changed);
    }
    void Pump(HANDLE parent);
    void RequestStop()
    {
        SetEvent(stop);
        PostMessageW(relay, WM_NULL, 0, 0);
    }
};
} // namespace magic::system
