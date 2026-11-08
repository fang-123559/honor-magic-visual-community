#include "providers.h"
#include "bluetooth-battery.h"
#include <winrt/Windows.Devices.Bluetooth.h>
#include <winrt/Windows.Devices.Enumeration.h>
#include <winrt/Windows.Devices.Radios.h>
#include <winrt/Windows.Devices.Bluetooth.Rfcomm.h>
#include <winrt/Windows.Devices.Bluetooth.GenericAttributeProfile.h>
#include <winioctl.h>
#include <bthdef.h>
#include <bthioctl.h>
namespace magic::system
{
using namespace winrt::Windows::Devices::Enumeration;
namespace
{
struct RadioObservation{winrt::Windows::Devices::Radios::Radio radio{nullptr};winrt::Windows::Devices::Radios::Radio::StateChanged_revoker changed;};
struct BluetoothWatcher
{
    DeviceWatcher watcher{nullptr};
    DeviceWatcher::Added_revoker added;
    DeviceWatcher::Updated_revoker updated;
    DeviceWatcher::Removed_revoker removed;
    DeviceWatcher::EnumerationCompleted_revoker complete;
    ~BluetoothWatcher()
    {
        if (watcher)
        {
            try
            {
                if (watcher.Status() == DeviceWatcherStatus::Started ||
                    watcher.Status() == DeviceWatcherStatus::EnumerationCompleted)
                    watcher.Stop();
            }
            catch (...)
            {
            }
        }
    }
};
std::vector<winrt::hstring> BluetoothSelectors()
{
    return {winrt::Windows::Devices::Bluetooth::BluetoothDevice::GetDeviceSelectorFromPairingState(true),
            winrt::Windows::Devices::Bluetooth::BluetoothLEDevice::GetDeviceSelectorFromPairingState(true)};
}
// Explicit device action only. Pairing and radio state are never modified.
void DisconnectBluetooth(std::wstring const &id,std::shared_ptr<ChangeState> const &state)
{
    using namespace winrt::Windows::Devices::Bluetooth;
    ULONGLONG deadline=GetTickCount64()+4000;BTH_ADDR address=0;bool connected=false;
    BluetoothDevice classic{nullptr};
    try{classic=Await(BluetoothDevice::FromIdAsync(id),state,deadline);}catch(winrt::hresult_error const &e){if(e.code()!=E_INVALIDARG)throw;}
    if(classic){address=classic.BluetoothAddress();connected=classic.ConnectionStatus()==BluetoothConnectionStatus::Connected;classic.Close();}
    else{auto le=Await(BluetoothLEDevice::FromIdAsync(id),state,deadline);if(le){address=le.BluetoothAddress();connected=le.ConnectionStatus()==BluetoothConnectionStatus::Connected;le.Close();}}
    if(!address)throw std::invalid_argument("Bluetooth device address unavailable");
    if(!connected)return;
    auto radios=Await(DeviceInformation::FindAllAsync(BluetoothAdapter::GetDeviceSelector()),state,deadline);
    DWORD failure=ERROR_NOT_SUPPORTED;unsigned count=0;
    for(auto const &radio:radios){
        if(++count>8||GetTickCount64()>=deadline)break;
        Handle handle(CreateFileW(radio.Id().c_str(),0,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr));
        if(handle.value==INVALID_HANDLE_VALUE){failure=GetLastError();continue;}
        Handle done(CreateEventW(nullptr,TRUE,FALSE,nullptr));Check(done.value!=nullptr);OVERLAPPED operation{};operation.hEvent=done;
        DWORD bytes=0;bool ok=DeviceIoControl(handle,IOCTL_BTH_DISCONNECT_DEVICE,&address,sizeof(address),nullptr,0,&bytes,&operation)!=FALSE;
        if(!ok){failure=GetLastError();if(failure==ERROR_IO_PENDING){
            HANDLE waits[]{done,state->stop};ULONGLONG now=GetTickCount64();DWORD timeout=now<deadline?static_cast<DWORD>(deadline-now):0;
            if(WaitForMultipleObjects(2,waits,FALSE,timeout)!=WAIT_OBJECT_0){CancelIoEx(handle,&operation);GetOverlappedResult(handle,&operation,&bytes,TRUE);throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_TIMEOUT));}
            ok=GetOverlappedResult(handle,&operation,&bytes,FALSE)!=FALSE;if(!ok)failure=GetLastError();
        }}
        if(ok)return;
    }
    throw winrt::hresult_error(HRESULT_FROM_WIN32(failure),L"Windows 无法断开此蓝牙设备；配对关系已保留。");
}
} // namespace
struct BluetoothProvider::Impl
{
    std::vector<std::unique_ptr<RadioObservation>> radios;
    std::shared_ptr<ChangeState> state;
    std::shared_ptr<std::atomic<bool>> active = std::make_shared<std::atomic<bool>>(false);
    winrt::Windows::Foundation::IAsyncOperation<DevicePairingResult> pendingPair{nullptr};
    std::shared_ptr<std::atomic<int>> pairingStatus = std::make_shared<std::atomic<int>>(-1);
    bool discover = false;
    std::map<std::wstring, DeviceInformation> offered;
    std::vector<std::unique_ptr<BluetoothWatcher>> watchers;
    explicit Impl(std::shared_ptr<ChangeState> s) : state(std::move(s))
    {
    }
    void Stop()
    {
        if (pendingPair)
        {
            try
            {
                pendingPair.Cancel();
            }
            catch (...)
            {
            }
            pendingPair = nullptr;
        }
        discover = false;
        *active = false;
        radios.clear();
        watchers.clear();
        offered.clear();
    }
    ~Impl()
    {
        Stop();
    }
    JsonObject Health() const
    {
        JsonObject result;
        Number(result, L"watchers", double(watchers.size()));
        Number(result, L"callbacks", double(watchers.size() * 4));
        Number(result, L"cachedDevices", double(offered.size()));
        Flag(result, L"pairingPending",
             pendingPair && pendingPair.Status() == winrt::Windows::Foundation::AsyncStatus::Started);
        Number(result, L"pairingStatus", pairingStatus->load());
        return result;
    }
};
BluetoothProvider::BluetoothProvider(std::shared_ptr<ChangeState> state)
    : impl(std::make_unique<Impl>(std::move(state)))
{
}
BluetoothProvider::~BluetoothProvider() = default;
JsonObject BluetoothProvider::Health() const
{
    return impl->Health();
}
JsonObject BluetoothProvider::Read()
{
    JsonObject result;
    Flag(result, L"available", true);
    try
    {
        auto radios =
            Await(winrt::Windows::Devices::Radios::Radio::GetRadiosAsync(), impl->state, GetTickCount64() + 1500);
        bool on = false, exists = false;
        for (auto const &radio : radios)
            if (radio.Kind() == winrt::Windows::Devices::Radios::RadioKind::Bluetooth)
            {
                exists = true;
                if(*impl->active && impl->radios.empty()){
                    auto observed=std::make_unique<RadioObservation>();observed->radio=radio;
                    auto signal=impl->state;auto active=impl->active;
                    observed->changed=radio.StateChanged(winrt::auto_revoke,[signal,active](auto const&,auto const&){if(*active)signal->Notify(128);});
                    impl->radios.push_back(std::move(observed));
                }
                on |= radio.State() == winrt::Windows::Devices::Radios::RadioState::On;
            }
        Flag(result, L"radioOn", on);
        Flag(result, L"radioAvailable", exists);
    }
    catch (winrt::hresult_error const &error)
    {
        Number(result, L"radioError", error.code().value);
    }
    ULONGLONG deadline = GetTickCount64() + 4000;
    std::map<std::wstring, DeviceInformation> devices;
    JsonArray rows;
    auto batteries=BluetoothBatteries(std::min(deadline,GetTickCount64()+250));
    auto selectors = BluetoothSelectors();
    bool discover = std::exchange(impl->discover, false); // Explicit discovery is one read, never a permanent scan.
    if (discover)
    {
        selectors.push_back(
            winrt::Windows::Devices::Bluetooth::BluetoothDevice::GetDeviceSelectorFromPairingState(false));
        selectors.push_back(
            winrt::Windows::Devices::Bluetooth::BluetoothLEDevice::GetDeviceSelectorFromPairingState(false));
    }
    for (auto const &selector : selectors)
    {
      try {
        auto properties = winrt::single_threaded_vector<winrt::hstring>({L"System.Devices.Aep.IsConnected",L"System.Devices.Aep.DeviceAddress",L"System.Devices.Aep.Bluetooth.Cod.Major",L"System.Devices.Aep.Bluetooth.Cod.Minor"});
        auto collection =
            Await(DeviceInformation::FindAllAsync(selector, properties, DeviceInformationKind::AssociationEndpoint),
                  impl->state, deadline);
        for (auto const &device : collection)
        {
            if (devices.size() >= 128)
                break;
            std::wstring id(device.Id());
            if (devices.contains(id))
                continue;
            devices.emplace(id, device);
            JsonObject row;
            Text(row, L"id", id);
            Text(row, L"name", std::wstring(device.Name()).substr(0, 512));
            // The Windows AEP class properties are UInt16, not JSON doubles.
            auto code=[&](wchar_t const *key){auto property=device.Properties().TryLookup(key);if(!property)return 0u;auto value=property.try_as<winrt::Windows::Foundation::IPropertyValue>();if(!value)return 0u;using P=winrt::Windows::Foundation::PropertyType;return value.Type()==P::UInt16?static_cast<unsigned>(value.GetUInt16()):value.Type()==P::UInt32?value.GetUInt32():0u;};
            unsigned major=code(L"System.Devices.Aep.Bluetooth.Cod.Major"),minor=code(L"System.Devices.Aep.Bluetooth.Cod.Minor");
            Text(row,L"deviceIcon",major==4?L"IoHeadset":major==2?L"IoPhonePortraitOutline":major==1?L"FaComputer":major==5&&(minor&0x20)?L"BsMouse":major==5&&(minor&0x10)?L"BsKeyboard":L"TbBluetooth");
            Flag(row, L"paired", device.Pairing().IsPaired());
            auto connected = device.Properties().TryLookup(L"System.Devices.Aep.IsConnected");
            if (connected)
            {
                auto value = connected.try_as<winrt::Windows::Foundation::IPropertyValue>();
                if (value && value.Type() == winrt::Windows::Foundation::PropertyType::Boolean)
                    {
                        Flag(row, L"connected", value.GetBoolean());
                        if(value.GetBoolean()){
                            auto property=device.Properties().TryLookup(L"System.Devices.Aep.DeviceAddress");auto text=property?property.try_as<winrt::Windows::Foundation::IPropertyValue>():nullptr;
                            auto address=text&&text.Type()==winrt::Windows::Foundation::PropertyType::String?BluetoothMac(std::wstring(text.GetString()),true):BluetoothMac(id);
                            auto found=batteries.find(address);if(found!=batteries.end()&&found->second.Value()>=0){Number(row,L"batteryPercent",found->second.Value());Text(row,L"batterySource",found->second.hfp?L"pnp-hfp":L"pnp");}
                        }
                    }
            }
            rows.Append(row);
        }
      } catch (winrt::hresult_error const &error) {
        Number(result, L"devicesError", error.code().value);
        Flag(result, L"devicesIncomplete", true);
      }
    }
    impl->offered.swap(devices);
    Flag(result, L"discoveryStarted", discover);
    result.Insert(L"devices", rows);
    return result;
}
JsonObject BluetoothProvider::Subscribe(bool enabled)
{
    if (!enabled)
        impl->Stop();
    else if (impl->watchers.empty())
    {
        impl->active = std::make_shared<std::atomic<bool>>(true);
        auto signal = impl->state;
        auto active = impl->active;
        try
        {
            for (auto const &selector : BluetoothSelectors())
            {
                auto row = std::make_unique<BluetoothWatcher>();
                row->watcher = DeviceInformation::CreateWatcher(selector,winrt::single_threaded_vector<winrt::hstring>({L"System.Devices.Aep.IsConnected"}),DeviceInformationKind::AssociationEndpoint);
                row->added = row->watcher.Added(winrt::auto_revoke, [signal, active](auto const &, auto const &) {
                    if (*active)
                        signal->Notify(128);
                });
                row->updated = row->watcher.Updated(winrt::auto_revoke, [signal, active](auto const &, auto const &) {
                    if (*active)
                        signal->Notify(128);
                });
                row->removed = row->watcher.Removed(winrt::auto_revoke, [signal, active](auto const &, auto const &) {
                    if (*active)
                        signal->Notify(128);
                });
                row->complete =
                    row->watcher.EnumerationCompleted(winrt::auto_revoke, [signal, active](auto const &, auto const &) {
                        if (*active)
                            signal->Notify(128);
                    });
                row->watcher.Start();
                impl->watchers.push_back(std::move(row));
            }
        }
        catch (...)
        {
            impl->Stop();
            throw;
        }
    }
    return impl->Health();
}
JsonObject BluetoothProvider::Unpair(JsonObject const &args)
{
    auto id = RequiredText(args, L"deviceId", 4096);
    auto found = impl->offered.find(id);
    if (found == impl->offered.end())
        throw std::invalid_argument("Bluetooth device not offered");
    auto result = Await(found->second.Pairing().UnpairAsync(), impl->state, GetTickCount64() + 4000);
    JsonObject response;
    Number(response, L"status", int(result.Status()));
    Flag(response, L"accepted",
         result.Status() == DeviceUnpairingResultStatus::Unpaired ||
             result.Status() == DeviceUnpairingResultStatus::AlreadyUnpaired);
    return response;
}
JsonObject BluetoothProvider::Action(JsonObject const &args)
{
    auto action = RequiredText(args, L"action", 32);
    JsonObject result;
    if (action == L"radio")
    {
        auto deadline=GetTickCount64()+4000;
        auto radios = Await(winrt::Windows::Devices::Radios::Radio::GetRadiosAsync(), impl->state, deadline);
        bool found = false;
        for (auto const &radio : radios)
            if (radio.Kind() == winrt::Windows::Devices::Radios::RadioKind::Bluetooth)
            {
                found = true;
                auto status = Await(radio.SetStateAsync(args.GetNamedBoolean(L"value")
                                                            ? winrt::Windows::Devices::Radios::RadioState::On
                                                            : winrt::Windows::Devices::Radios::RadioState::Off),
                                    impl->state, deadline);
                if (status != winrt::Windows::Devices::Radios::RadioAccessStatus::Allowed)
                    throw std::runtime_error("Bluetooth radio permission denied");
            }
        if (!found)
            throw std::runtime_error("Bluetooth radio unavailable");
        Flag(result, L"accepted", true);
    }
    else if (action == L"discover")
    {
        impl->discover = true;
        Flag(result, L"accepted", true);
    }
    else
    {
        auto id = RequiredText(args, L"deviceId", 4096);
        auto found = impl->offered.find(id);
        if (found == impl->offered.end())
            throw std::invalid_argument("Bluetooth device not offered");
        if (action == L"pair")
        {
            if (impl->pendingPair && impl->pendingPair.Status() == winrt::Windows::Foundation::AsyncStatus::Started)
                throw std::invalid_argument("Another pairing request is waiting for confirmation");
            impl->pairingStatus->store(-1);
            impl->pendingPair = found->second.Pairing().PairAsync();
            auto signal = impl->state;
            auto status = impl->pairingStatus;
            impl->pendingPair.Completed([signal, status](auto const &op, auto completed) {
                try
                {
                    status->store(completed == winrt::Windows::Foundation::AsyncStatus::Completed
                                      ? int(op.GetResults().Status())
                                      : -2);
                }
                catch (...)
                {
                    status->store(-2);
                }
                signal->Notify(128);
            });
            Flag(result, L"accepted", true);
            Text(result, L"status", L"pending-user-confirmation");
        }
        else if(action==L"disconnect")
        {
            if(!found->second.Pairing().IsPaired())throw std::invalid_argument("Bluetooth device is no longer paired");
            DisconnectBluetooth(id,impl->state);Flag(result,L"accepted",true);Text(result,L"status",L"disconnected");
        }
        else if (action == L"connect")
        {
            using namespace winrt::Windows::Devices::Bluetooth;
            ULONGLONG deadline = GetTickCount64() + 4000;
            bool connected = false;
            BluetoothDevice device{nullptr};
            try
            {
                device = Await(BluetoothDevice::FromIdAsync(id), impl->state, deadline);
            }
            catch (winrt::hresult_error const &e)
            {
                if (e.code() != E_INVALIDARG)
                    throw;
            }
            if (device)
            {
                auto services =
                    Await(device.GetRfcommServicesAsync(BluetoothCacheMode::Uncached), impl->state, deadline);
                connected = device.ConnectionStatus() == BluetoothConnectionStatus::Connected;
                device.Close();
            }
            else
            {
                auto le = Await(BluetoothLEDevice::FromIdAsync(id), impl->state, deadline);
                if (le)
                {
                    auto services = Await(le.GetGattServicesAsync(BluetoothCacheMode::Uncached), impl->state, deadline);
                    for (auto const &service : services.Services())
                        service.Close();
                    connected = le.ConnectionStatus() == BluetoothConnectionStatus::Connected;
                    le.Close();
                }
            }
            Flag(result, L"accepted", connected);
            Text(result, L"status", connected ? L"connected" : L"service query completed; device is not connected");
        }
        else
            throw std::invalid_argument("Unsupported Bluetooth action");
    }
    impl->state->Notify(128);
    return result;
}
} // namespace magic::system
