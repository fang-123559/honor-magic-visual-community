#include "providers.h"
#include <wlanapi.h>
#include <cstring>
#include <winrt/Windows.Networking.Connectivity.h>
namespace magic::system
{
namespace
{
void WlanCheck(DWORD code)
{
    if (code != ERROR_SUCCESS)
        winrt::throw_hresult(HRESULT_FROM_WIN32(code));
}
std::wstring InterfaceId(GUID const &id)
{
    wchar_t text[40]{};
    StringFromGUID2(id, text, 40);
    return text;
}
struct WlanMemory
{
    void *value = nullptr;
    ~WlanMemory()
    {
        if (value)
            WlanFreeMemory(value);
    }
};
} // namespace
struct WifiProvider::Impl
{
    std::shared_ptr<ChangeState> state;
    HANDLE client = nullptr;
    bool subscribed = false;
    winrt::Windows::Networking::Connectivity::NetworkInformation::NetworkStatusChanged_revoker connectivityChanged;
    std::map<std::wstring, GUID> interfaces;
    std::map<std::wstring, std::set<std::wstring>> profiles;
    explicit Impl(std::shared_ptr<ChangeState> s) : state(std::move(s))
    {
    }
    static void WINAPI Changed(WLAN_NOTIFICATION_DATA *, PVOID context)
    {
        static_cast<Impl *>(context)->state->Notify(64);
    }
    void Open()
    {
        if (!client)
        {
            DWORD version = 0;
            WlanCheck(WlanOpenHandle(2, nullptr, &version, &client));
        }
    }
    void Enumerate()
    {
        Open();
        WlanMemory memory;
        WlanCheck(WlanEnumInterfaces(client, nullptr, reinterpret_cast<WLAN_INTERFACE_INFO_LIST **>(&memory.value)));
        auto list = static_cast<WLAN_INTERFACE_INFO_LIST *>(memory.value);
        interfaces.clear();
        for (DWORD i = 0; i < std::min(list->dwNumberOfItems, 32ul); i++)
            interfaces[InterfaceId(list->InterfaceInfo[i].InterfaceGuid)] = list->InterfaceInfo[i].InterfaceGuid;
    }
    GUID Resolve(std::wstring const &id)
    {
        auto found = interfaces.find(id);
        if (found == interfaces.end())
            throw std::invalid_argument("Wi-Fi interface not offered");
        return found->second;
    }
    void Stop()
    {
        connectivityChanged.revoke();
        if (client)
        {
            if (subscribed)
                WlanRegisterNotification(client, WLAN_NOTIFICATION_SOURCE_NONE, TRUE, nullptr, nullptr, nullptr,
                                         nullptr);
            WlanCloseHandle(client, nullptr);
        }
        client = nullptr;
        subscribed = false;
        interfaces.clear();
        profiles.clear();
    }
    ~Impl()
    {
        Stop();
    }
    JsonObject Health() const
    {
        JsonObject result;
        Flag(result, L"subscribed", subscribed);
        Number(result, L"callbacks", subscribed ? 1 : 0);
        Number(result, L"cachedInterfaces", double(interfaces.size()));
        return result;
    }
};
WifiProvider::WifiProvider(std::shared_ptr<ChangeState> state) : impl(std::make_unique<Impl>(std::move(state)))
{
}
WifiProvider::~WifiProvider() = default;
JsonObject WifiProvider::Health() const
{
    return impl->Health();
}
JsonObject WifiProvider::Subscribe(bool enabled)
{
    if (!enabled)
        impl->Stop();
    else
    {
        impl->Open();
        WlanCheck(WlanRegisterNotification(impl->client, WLAN_NOTIFICATION_SOURCE_ACM, TRUE, Impl::Changed, impl.get(),
                                           nullptr, nullptr));
        impl->subscribed = true;
        if(!impl->connectivityChanged)try{auto signal=impl->state;impl->connectivityChanged=winrt::Windows::Networking::Connectivity::NetworkInformation::NetworkStatusChanged(winrt::auto_revoke,[signal](auto const &){signal->Notify(64);});}catch(winrt::hresult_error const &){}

    }
    return impl->Health();
}
JsonObject WifiProvider::Read(bool networkDetails)
{
    impl->Enumerate();
    WlanMemory memory;
    WlanCheck(WlanEnumInterfaces(impl->client, nullptr, reinterpret_cast<WLAN_INTERFACE_INFO_LIST **>(&memory.value)));
    auto list = static_cast<WLAN_INTERFACE_INFO_LIST *>(memory.value);
    JsonArray rows;
    using namespace winrt::Windows::Networking::Connectivity;
    std::map<GUID,NetworkConnectivityLevel,decltype([](GUID const &a,GUID const &b){return memcmp(&a,&b,sizeof(GUID))<0;})> connectivity;
    try{for(auto const &profile:NetworkInformation::GetConnectionProfiles())if(auto adapter=profile.NetworkAdapter();adapter&&connectivity.size()<64)connectivity[adapter.NetworkAdapterId()]=profile.GetNetworkConnectivityLevel();}catch(winrt::hresult_error const &){}
    for (DWORD i = 0; i < std::min(list->dwNumberOfItems, 32ul); i++)
    {
        auto const &item = list->InterfaceInfo[i];
        JsonObject row;
        Text(row, L"id", InterfaceId(item.InterfaceGuid));
        Text(row, L"name", item.strInterfaceDescription);
        Number(row, L"state", item.isState);
        if(item.isState==wlan_interface_state_connected){
            if(auto status=connectivity.find(item.InterfaceGuid);status!=connectivity.end()){Flag(row,L"internetKnown",true);Flag(row,L"internetAvailable",status->second==NetworkConnectivityLevel::InternetAccess);Number(row,L"connectivityLevel",static_cast<int>(status->second));}
            WlanMemory connection;DWORD bytes=0;WLAN_OPCODE_VALUE_TYPE value{};
            if(WlanQueryInterface(impl->client,&item.InterfaceGuid,wlan_intf_opcode_current_connection,nullptr,&bytes,&connection.value,&value)==ERROR_SUCCESS&&bytes>=sizeof(WLAN_CONNECTION_ATTRIBUTES)){
                auto current=static_cast<WLAN_CONNECTION_ATTRIBUTES*>(connection.value);std::wstring hex;wchar_t pair[3]{};auto const &ssid=current->wlanAssociationAttributes.dot11Ssid;
                for(ULONG b=0;b<std::min(ssid.uSSIDLength,32ul);++b){swprintf_s(pair,L"%02x",ssid.ucSSID[b]);hex+=pair;}Text(row,L"ssidHex",hex);
            }
        }

        WlanMemory radios;
        DWORD radioBytes = 0;
        WLAN_OPCODE_VALUE_TYPE radioType{};
        if (WlanQueryInterface(impl->client, &item.InterfaceGuid, wlan_intf_opcode_radio_state, nullptr, &radioBytes,
                               &radios.value, &radioType) == ERROR_SUCCESS &&
            radioBytes >= sizeof(WLAN_RADIO_STATE))
        {
            auto radio = static_cast<WLAN_RADIO_STATE *>(radios.value);
            bool on = false;
            for (DWORD phy = 0; phy < std::min(radio->dwNumberOfPhys, DWORD(WLAN_MAX_PHY_INDEX)); ++phy)
                on |= radio->PhyRadioState[phy].dot11SoftwareRadioState == dot11_radio_state_on &&
                      radio->PhyRadioState[phy].dot11HardwareRadioState == dot11_radio_state_on;
            Flag(row, L"radioOn", on);
        }
        if (networkDetails)
        {
            WlanMemory networks;
            DWORD code = WlanGetAvailableNetworkList(impl->client, &item.InterfaceGuid, 0, nullptr,
                                                     reinterpret_cast<WLAN_AVAILABLE_NETWORK_LIST **>(&networks.value));
            Number(row, L"networkDetailsError", code);
            if (code == ERROR_SUCCESS)
            {
                JsonArray available;
                auto all = static_cast<WLAN_AVAILABLE_NETWORK_LIST *>(networks.value);
                for (DWORD j = 0; j < std::min(all->dwNumberOfItems, 256ul); j++)
                {
                    auto const &network = all->Network[j];
                    JsonObject n;
                    Text(n, L"profile", network.strProfileName);
                    Number(n, L"authentication", network.dot11DefaultAuthAlgorithm);
                    Number(n, L"signal", network.wlanSignalQuality);
                    Flag(n, L"secured", network.bSecurityEnabled != FALSE);
                    Flag(n, L"connected", (network.dwFlags & WLAN_AVAILABLE_NETWORK_CONNECTED) != 0);
                    std::wstring hex;
                    wchar_t pair[3]{};
                    for (ULONG b = 0; b < std::min(network.dot11Ssid.uSSIDLength, 32ul); b++)
                    {
                        swprintf_s(pair, L"%02x", network.dot11Ssid.ucSSID[b]);
                        hex += pair;
                    }
                    Text(n, L"ssidHex", hex);
                    available.Append(n);
                }
                row.Insert(L"networks", available);
            }
        }
        rows.Append(row);
    }
    JsonObject result = impl->Health();
    Flag(result, L"available", true);
    Flag(result, L"networkDetailsRequested", networkDetails);
    result.Insert(L"interfaces", rows);
    return result;
}
JsonObject WifiProvider::Profiles(JsonObject const &args)
{
    auto id = RequiredText(args, L"interfaceId", 64);
    GUID adapterGuid = impl->Resolve(id);
    WlanMemory memory;
    WlanCheck(WlanGetProfileList(impl->client, &adapterGuid, nullptr,
                                 reinterpret_cast<WLAN_PROFILE_INFO_LIST **>(&memory.value)));
    auto list = static_cast<WLAN_PROFILE_INFO_LIST *>(memory.value);
    auto &offered = impl->profiles[id];
    offered.clear();
    JsonArray rows;
    for (DWORD i = 0; i < std::min(list->dwNumberOfItems, 256ul); i++)
    {
        std::wstring name = list->ProfileInfo[i].strProfileName;
        offered.insert(name);
        rows.Append(JsonValue::CreateStringValue(name));
    }
    JsonObject result;
    result.Insert(L"profiles", rows);
    return result;
}
JsonObject WifiProvider::Action(JsonObject const &args)
{
    auto id = RequiredText(args, L"interfaceId", 64);
    GUID adapterGuid = impl->Resolve(id);
    auto action = RequiredText(args, L"action", 32);
    DWORD result = ERROR_INVALID_PARAMETER;
    if (action == L"radio")
    {
        WlanMemory memory;
        DWORD bytes = 0;
        WLAN_OPCODE_VALUE_TYPE type{};
        WlanCheck(WlanQueryInterface(impl->client, &adapterGuid, wlan_intf_opcode_radio_state, nullptr, &bytes,
                                     &memory.value, &type));
        if (bytes < sizeof(WLAN_RADIO_STATE))
            throw std::runtime_error("Radio state truncated");
        auto radios = static_cast<WLAN_RADIO_STATE *>(memory.value);
        for (DWORD n = 0; n < std::min(radios->dwNumberOfPhys, DWORD(WLAN_MAX_PHY_INDEX)); n++)
        {
            WLAN_PHY_RADIO_STATE radio = radios->PhyRadioState[n];
            radio.dot11SoftwareRadioState =
                args.GetNamedBoolean(L"value") ? dot11_radio_state_on : dot11_radio_state_off;
            WlanCheck(WlanSetInterface(impl->client, &adapterGuid, wlan_intf_opcode_radio_state, sizeof(radio), &radio,
                                       nullptr));
        }
        result = ERROR_SUCCESS;
    }
    else if (action == L"forget-profile")
    {
        auto profile = RequiredText(args, L"profile", 256);
        auto list = impl->profiles.find(id);
        if (list == impl->profiles.end() || !list->second.contains(profile))
            throw std::invalid_argument("Wi-Fi profile not offered");
        result = WlanDeleteProfile(impl->client, &adapterGuid, profile.c_str(), nullptr);
    }
    else if (action == L"connect-network")
    {
        auto hex = RequiredText(args, L"ssidHex", 64);
        if (hex.size() % 2 || !std::all_of(hex.begin(), hex.end(), [](wchar_t c) {
                return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f');
            }))
            throw std::invalid_argument("Invalid SSID identity");
        WlanMemory memory;
        WlanCheck(WlanGetAvailableNetworkList(impl->client, &adapterGuid, 0, nullptr,
                                              reinterpret_cast<WLAN_AVAILABLE_NETWORK_LIST **>(&memory.value)));
        auto networks = static_cast<WLAN_AVAILABLE_NETWORK_LIST *>(memory.value);
        WLAN_AVAILABLE_NETWORK const *selected = nullptr;
        for (DWORD n = 0; n < std::min(networks->dwNumberOfItems, 256ul); n++)
        {
            auto const &network = networks->Network[n];
            std::wstring candidate;
            wchar_t pair[3]{};
            for (ULONG b = 0; b < std::min(network.dot11Ssid.uSSIDLength, 32ul); b++)
            {
                swprintf_s(pair, L"%02x", network.dot11Ssid.ucSSID[b]);
                candidate += pair;
            }
            if (candidate == hex)
            {
                selected = &network;
                break;
            }
        }
        if (!selected || selected->dot11BssType != dot11_BSS_type_infrastructure)
            throw std::invalid_argument("Wireless network no longer offered");
        bool secure = selected->bSecurityEnabled != FALSE;
        if (secure && (selected->dot11DefaultAuthAlgorithm != DOT11_AUTH_ALGO_RSNA_PSK ||
                       selected->dot11DefaultCipherAlgorithm != DOT11_CIPHER_ALGO_CCMP))
            throw std::runtime_error("This security mode requires Windows network settings; no downgrade attempted");
        auto password = std::wstring(args.GetNamedString(L"password", L""));
        if (secure && (password.size() < 8 || password.size() > 63))
            throw std::invalid_argument("Wi-Fi password length must be 8..63");
        auto escape = [](std::wstring const &value) {
            std::wstring encoded;
            for (auto c : value)
            {
                switch (c)
                {
                case L'&':
                    encoded += L"&amp;";
                    break;
                case L'<':
                    encoded += L"&lt;";
                    break;
                case L'>':
                    encoded += L"&gt;";
                    break;
                case L'"':
                    encoded += L"&quot;";
                    break;
                case L'\'':
                    encoded += L"&apos;";
                    break;
                default:
                    if (c < 32)
                        throw std::invalid_argument("Invalid password character");
                    encoded += c;
                }
            }
            return encoded;
        };
        std::wstring profile = L"Magic-" + hex;
        std::wstring xml = L"<?xml version=\"1.0\"?><WLANProfile "
                           L"xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>" +
                           profile + L"</name><SSIDConfig><SSID><hex>" + hex +
                           L"</hex></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>manual</"
                           L"connectionMode><MSM><security><authEncryption><authentication>" +
                           (secure ? std::wstring(L"WPA2PSK") : std::wstring(L"open")) +
                           L"</authentication><encryption>" + (secure ? std::wstring(L"AES") : std::wstring(L"none")) +
                           L"</encryption><useOneX>false</useOneX></authEncryption>";
        if (secure)
            xml += L"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>" +
                   escape(password) + L"</keyMaterial></sharedKey>";
        xml += L"</security></MSM></WLANProfile>";
        DWORD reason = 0;
        DWORD created = WlanSetProfile(impl->client, &adapterGuid, WLAN_PROFILE_USER, xml.c_str(), nullptr, FALSE,
                                       nullptr, &reason);
        SecureZeroMemory(password.data(), password.size() * sizeof(wchar_t));
        SecureZeroMemory(xml.data(), xml.size() * sizeof(wchar_t));
        WlanCheck(created);
        WLAN_CONNECTION_PARAMETERS parameters{};
        parameters.wlanConnectionMode = wlan_connection_mode_profile;
        parameters.strProfile = profile.c_str();
        parameters.dot11BssType = dot11_BSS_type_infrastructure;
        result = WlanConnect(impl->client, &adapterGuid, &parameters, nullptr);
        if (result != ERROR_SUCCESS)
            WlanDeleteProfile(impl->client, &adapterGuid, profile.c_str(), nullptr);
    }
    else if (action == L"disconnect")
        result = WlanDisconnect(impl->client, &adapterGuid, nullptr);
    else if (action == L"scan")
        result = WlanScan(impl->client, &adapterGuid, nullptr, nullptr, nullptr);
    else if (action == L"connect-profile")
    {
        auto profile = RequiredText(args, L"profile", 256);
        auto list = impl->profiles.find(id);
        if (list == impl->profiles.end() || !list->second.contains(profile))
            throw std::invalid_argument("Wi-Fi profile not offered");
        WLAN_CONNECTION_PARAMETERS parameters{};
        parameters.wlanConnectionMode = wlan_connection_mode_profile;
        parameters.strProfile = profile.c_str();
        parameters.dot11BssType = dot11_BSS_type_any;
        result = WlanConnect(impl->client, &adapterGuid, &parameters, nullptr);
    }
    else
        throw std::invalid_argument("Unsupported Wi-Fi action");
    WlanCheck(result);
    JsonObject response;
    Flag(response, L"accepted", true);
    Text(response, L"status", L"submitted; connection completion is reported by Windows events");
    return response;
}
} // namespace magic::system
