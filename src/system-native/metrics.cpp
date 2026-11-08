#include "metrics.h"
#include <pdh.h>
#include <pdhmsg.h>
namespace magic::system
{
struct Metrics::Impl
{
    PDH_HQUERY query = nullptr;
    PDH_HCOUNTER engine = nullptr, dedicated = nullptr, shared = nullptr;
    bool storage = false, primed = false;
    PDH_STATUS error = ERROR_SUCCESS;
    ~Impl()
    {
        if (query)
            PdhCloseQuery(query);
    }
    void Close()
    {
        if (query)
            PdhCloseQuery(query);
        query = nullptr;
        engine = dedicated = shared = nullptr;
        primed = false;
    }
    std::vector<std::pair<std::wstring, double>> Values(PDH_HCOUNTER counter, DWORD format)
    {
        DWORD bytes = 0, count = 0;
        auto status = PdhGetFormattedCounterArrayW(counter, format, &bytes, &count, nullptr);
        if (status != PDH_MORE_DATA || bytes > 1024 * 1024)
            return {};
        std::vector<BYTE> memory(bytes);
        auto items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W *>(memory.data());
        status = PdhGetFormattedCounterArrayW(counter, format, &bytes, &count, items);
        if (status != ERROR_SUCCESS || count > 8192)
            return {};
        std::vector<std::pair<std::wstring, double>> values;
        for (DWORD n = 0; n < count; n++)
            if (items[n].FmtValue.CStatus == PDH_CSTATUS_VALID_DATA ||
                items[n].FmtValue.CStatus == PDH_CSTATUS_NEW_DATA)
            {
                double value =
                    format == PDH_FMT_DOUBLE ? items[n].FmtValue.doubleValue : double(items[n].FmtValue.largeValue);
                if (std::isfinite(value) && value >= 0)
                    values.emplace_back(items[n].szName, value);
            }
        return values;
    }
};
Metrics::Metrics() : impl(std::make_unique<Impl>())
{
}
Metrics::~Metrics() = default;
void Metrics::Configure(bool gpu, bool storage)
{
    impl->storage = storage;
    if (!gpu)
    {
        impl->Close();
        return;
    }
    if (impl->query)
        return;
    impl->error = PdhOpenQueryW(nullptr, 0, &impl->query);
    if (impl->error)
        return;
    impl->error = PdhAddEnglishCounterW(impl->query, L"\\GPU Engine(*)\\Utilization Percentage", 0, &impl->engine);
    if (impl->error)
    {
        impl->Close();
        return;
    }
    PdhAddEnglishCounterW(impl->query, L"\\GPU Adapter Memory(*)\\Dedicated Usage", 0, &impl->dedicated);
    PdhAddEnglishCounterW(impl->query, L"\\GPU Adapter Memory(*)\\Shared Usage", 0, &impl->shared);
    impl->error = PdhCollectQueryData(impl->query);
}
JsonObject Metrics::Sample()
{
    JsonObject result;
    if (impl->query)
    {
        auto status = PdhCollectQueryData(impl->query);
        JsonObject gpu;
        Flag(gpu, L"available", status == ERROR_SUCCESS && impl->primed);
        if (status == ERROR_SUCCESS && impl->primed)
        {
            std::map<std::wstring, double> engines;
            for (auto const &[name, value] : impl->Values(impl->engine, PDH_FMT_DOUBLE))
            {
                auto luid = name.find(L"luid_");
                auto key = luid == name.npos ? name : name.substr(luid);
                engines[key] += value;
            }
            double busiest = 0;
            for (auto const &[_, value] : engines)
                busiest = std::max(busiest, value);
            Number(gpu, L"percent", std::clamp(busiest, 0., 100.));
            for (auto [counter, key] : std::vector<std::pair<PDH_HCOUNTER, wchar_t const *>>{
                     {impl->dedicated, L"dedicatedBytes"}, {impl->shared, L"sharedBytes"}})
            {
                double total = 0;
                if (counter)
                    for (auto const &entry : impl->Values(counter, PDH_FMT_LARGE))
                        total += entry.second;
                Number(gpu, key, total);
            }
        }
        else
            Number(gpu, L"status", double(status));
        impl->primed = status == ERROR_SUCCESS;
        result.Insert(L"gpu", gpu);
    }
    else if (impl->error)
    {
        JsonObject gpu;
        Flag(gpu, L"available", false);
        Number(gpu, L"status", double(impl->error));
        result.Insert(L"gpu", gpu);
    }
    if (impl->storage)
    {
        JsonArray disks;
        wchar_t roots[512]{};
        DWORD length = GetLogicalDriveStringsW(512, roots);
        if (length && length < 512)
            for (wchar_t const *root = roots; *root; root += wcslen(root) + 1)
            {
                if (GetDriveTypeW(root) != DRIVE_FIXED)
                    continue;
                ULARGE_INTEGER available{}, total{}, free{};
                if (!GetDiskFreeSpaceExW(root, &available, &total, &free))
                    continue;
                JsonObject disk;
                Text(disk, L"root", root);
                Number(disk, L"totalBytes", double(total.QuadPart));
                Number(disk, L"availableBytes", double(available.QuadPart));
                disks.Append(disk);
            }
        result.Insert(L"storage", disks);
    }
    return result;
}
} // namespace magic::system
