#include "system.h"
#include <setupapi.h>
#include <winioctl.h>
#include <poclass.h>

namespace magic::system
{
// Read only, using Windows' battery class interface. No WMI host or extra timer.
JsonArray ReadBatteryDetails()
{
    constexpr GUID batteryGuid{0x72631e54, 0x78a4, 0x11d0, {0xbc, 0xf7, 0x00, 0xaa, 0x00, 0xb7, 0xb3, 0x2a}};
    JsonArray batteries;
    HDEVINFO devices = SetupDiGetClassDevsW(&batteryGuid, nullptr, nullptr, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    if (devices == INVALID_HANDLE_VALUE)
        return batteries;
    for (DWORD index = 0; index < 32 && batteries.Size() < 8; index++)
    {
        SP_DEVICE_INTERFACE_DATA device{};
        device.cbSize = sizeof(device);
        if (!SetupDiEnumDeviceInterfaces(devices, nullptr, &batteryGuid, index, &device))
            break;
        DWORD size = 0;
        SetupDiGetDeviceInterfaceDetailW(devices, &device, nullptr, 0, &size, nullptr);
        if (size < sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) || size > 65536)
            continue;
        std::vector<BYTE> buffer(size);
        auto detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W *>(buffer.data());
        detail->cbSize = sizeof(*detail);
        if (!SetupDiGetDeviceInterfaceDetailW(devices, &device, detail, size, nullptr, nullptr))
            continue;
        Handle battery(CreateFileW(detail->DevicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                   OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        if (battery.value == INVALID_HANDLE_VALUE)
            continue;
        DWORD timeout = 0, returned = 0;
        ULONG tag = 0;
        if (!DeviceIoControl(battery, IOCTL_BATTERY_QUERY_TAG, &timeout, sizeof(timeout), &tag, sizeof(tag), &returned,
                             nullptr) ||
            returned != sizeof(tag) || !tag)
            continue;
        BATTERY_QUERY_INFORMATION query{};
        query.BatteryTag = tag;
        query.InformationLevel = BatteryInformation;
        BATTERY_INFORMATION info{};
        if (!DeviceIoControl(battery, IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(query), &info, sizeof(info),
                             &returned, nullptr) ||
            returned < sizeof(info))
            continue;
        if (!(info.Capabilities & BATTERY_SYSTEM_BATTERY) || (info.Capabilities & BATTERY_IS_SHORT_TERM))
            continue;
        JsonObject row;
        Text(row, L"id", std::to_wstring(batteries.Size()));
        wchar_t name[256]{};
        query.InformationLevel = BatteryDeviceName;
        if (DeviceIoControl(battery, IOCTL_BATTERY_QUERY_INFORMATION, &query, sizeof(query), name, sizeof(name),
                            &returned, nullptr))
        {
            name[255] = 0;
            Text(row, L"name", name);
        }
        else
            Text(row, L"name", L"");
        for (auto key : {L"healthPercent", L"cycleCount", L"designedCapacity", L"fullChargedCapacity"})
            row.Insert(key, JsonValue::CreateNullValue());
        if (info.DesignedCapacity && info.DesignedCapacity != BATTERY_UNKNOWN_CAPACITY && info.FullChargedCapacity &&
            info.FullChargedCapacity != BATTERY_UNKNOWN_CAPACITY)
        {
            Number(row, L"designedCapacity", info.DesignedCapacity);
            Number(row, L"fullChargedCapacity", info.FullChargedCapacity);
            Number(row, L"healthPercent", std::min(100., 100. * info.FullChargedCapacity / info.DesignedCapacity));
        }
        // The battery-class contract uses zero when cycle count is unsupported.
        if (info.CycleCount && info.CycleCount != 0xffffffff)
            Number(row, L"cycleCount", info.CycleCount);
        batteries.Append(row);
    }
    SetupDiDestroyDeviceInfoList(devices);
    return batteries;
}

void ReadPowerMode(JsonObject const &power, SYSTEM_POWER_STATUS const &status)
{
    Text(power, L"mode", L"unknown");
    if (status.ACLineStatus > 1)
        return;
    // Documented Windows 11 API; resolve optionally for older supported systems.
    static HMODULE module = LoadLibraryExW(L"powrprof.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    using QueryMode = DWORD(WINAPI *)(GUID *);
    static auto ac =
        module ? reinterpret_cast<QueryMode>(GetProcAddress(module, "PowerGetUserConfiguredACPowerMode")) : nullptr;
    static auto dc =
        module ? reinterpret_cast<QueryMode>(GetProcAddress(module, "PowerGetUserConfiguredDCPowerMode")) : nullptr;
    auto query = status.ACLineStatus == 1 ? ac : dc;
    GUID mode{};
    if (!query || query(&mode) != ERROR_SUCCESS)
        return;
    constexpr GUID efficiency{0x961cc777, 0x2547, 0x4f9d, {0x81, 0x74, 0x7d, 0x86, 0x18, 0x1b, 0x8a, 0x7a}};
    constexpr GUID performance{0xded574b5, 0x45a0, 0x4f42, {0x87, 0x37, 0x46, 0x34, 0x5c, 0x09, 0xc2, 0x38}};
    if (IsEqualGUID(mode, efficiency))
        Text(power, L"mode", L"efficiency");
    else if (IsEqualGUID(mode, performance))
        Text(power, L"mode", L"performance");
    else if (IsEqualGUID(mode, GUID{}))
        Text(power, L"mode", L"balanced");
}
} // namespace magic::system
