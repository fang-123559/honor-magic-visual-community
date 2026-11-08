#include "providers.h"
#include <mmdeviceapi.h>
#include <audiopolicy.h>
namespace magic::system
{
namespace
{
std::wstring SessionId(IAudioSessionControl2 *session)
{
    LPWSTR text = nullptr;
    winrt::check_hresult(session->GetSessionInstanceIdentifier(&text));
    std::wstring id = text;
    CoTaskMemFree(text);
    return id;
}
std::wstring EndpointId(IMMDevice *device)
{
    LPWSTR text = nullptr;
    winrt::check_hresult(device->GetId(&text));
    std::wstring id = text;
    CoTaskMemFree(text);
    return id;
}
struct Events final : IAudioSessionEvents, IAudioSessionNotification
{
    std::atomic<ULONG> refs{1};
    std::shared_ptr<ChangeState> state;
    std::atomic<bool> active{true};
    explicit Events(std::shared_ptr<ChangeState> s) : state(std::move(s))
    {
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **out) override
    {
        if (!out)
            return E_POINTER;
        *out = nullptr;
        if (id == __uuidof(IUnknown) || id == __uuidof(IAudioSessionEvents))
            *out = static_cast<IAudioSessionEvents *>(this);
        else if (id == __uuidof(IAudioSessionNotification))
            *out = static_cast<IAudioSessionNotification *>(this);
        else
            return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override
    {
        return ++refs;
    }
    ULONG STDMETHODCALLTYPE Release() override
    {
        auto n = --refs;
        if (!n)
            delete this;
        return n;
    }
    HRESULT Notify()
    {
        if (active)
            state->Notify(2);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnDisplayNameChanged(LPCWSTR, LPCGUID) override
    {
        return Notify();
    }
    HRESULT STDMETHODCALLTYPE OnIconPathChanged(LPCWSTR, LPCGUID) override
    {
        return Notify();
    }
    HRESULT STDMETHODCALLTYPE OnSimpleVolumeChanged(float, BOOL, LPCGUID) override
    {
        return Notify();
    }
    HRESULT STDMETHODCALLTYPE OnChannelVolumeChanged(DWORD, float[], DWORD, LPCGUID) override
    {
        return Notify();
    }
    HRESULT STDMETHODCALLTYPE OnGroupingParamChanged(LPCGUID, LPCGUID) override
    {
        return Notify();
    }
    HRESULT STDMETHODCALLTYPE OnStateChanged(AudioSessionState) override
    {
        return Notify();
    }
    HRESULT STDMETHODCALLTYPE OnSessionDisconnected(AudioSessionDisconnectReason) override
    {
        return Notify();
    }
    HRESULT STDMETHODCALLTYPE OnSessionCreated(IAudioSessionControl *) override
    {
        return Notify();
    }
};
struct Session
{
    winrt::com_ptr<IAudioSessionControl2> control;
    winrt::com_ptr<ISimpleAudioVolume> volume;
    winrt::com_ptr<Events> callback;
    bool registered = false;
    ~Session()
    {
        if (registered)
            control->UnregisterAudioSessionNotification(callback.get());
    }
};
} // namespace
struct MixerProvider::Impl
{
    std::shared_ptr<ChangeState> state;
    bool subscribed = false, managerRegistered = false;
    winrt::com_ptr<IMMDeviceEnumerator> enumerator;
    winrt::com_ptr<IAudioSessionManager2> manager;
    winrt::com_ptr<Events> callback;
    std::wstring endpoint;
    std::map<std::wstring, std::unique_ptr<Session>> sessions;
    explicit Impl(std::shared_ptr<ChangeState> s) : state(std::move(s))
    {
    }
    void Release()
    {
        if (callback)
            callback->active = false;
        sessions.clear();
        if (manager && managerRegistered)
            manager->UnregisterSessionNotification(callback.get());
        managerRegistered = false;
        callback = nullptr;
        manager = nullptr;
        enumerator = nullptr;
        endpoint.clear();
    }
    ~Impl()
    {
        Release();
    }
    void Prepare()
    {
        if (!enumerator)
            winrt::check_hresult(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER,
                                                  __uuidof(IMMDeviceEnumerator), enumerator.put_void()));
        winrt::com_ptr<IMMDevice> device;
        winrt::check_hresult(enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, device.put()));
        auto id = EndpointId(device.get());
        if (manager && endpoint != id)
        {
            Release();
            winrt::check_hresult(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_INPROC_SERVER,
                                                  __uuidof(IMMDeviceEnumerator), enumerator.put_void()));
        }
        if (!manager)
        {
            winrt::check_hresult(
                device->Activate(__uuidof(IAudioSessionManager2), CLSCTX_INPROC_SERVER, nullptr, manager.put_void()));
            endpoint = id;
            callback.attach(new Events(state));
        }
        if (subscribed && !managerRegistered)
        {
            winrt::check_hresult(manager->RegisterSessionNotification(callback.get()));
            managerRegistered = true;
        }
    }
};
MixerProvider::MixerProvider(std::shared_ptr<ChangeState> s) : impl(std::make_unique<Impl>(std::move(s)))
{
}
MixerProvider::~MixerProvider() = default;
JsonObject MixerProvider::Health() const
{
    JsonObject result;
    Flag(result, L"subscribed", impl->subscribed);
    Number(result, L"managerCallbacks", impl->managerRegistered ? 1 : 0);
    Number(result, L"sessionCallbacks", impl->subscribed ? double(impl->sessions.size()) : 0);
    Number(result, L"cachedSessions", double(impl->sessions.size()));
    return result;
}
JsonObject MixerProvider::Subscribe(bool enabled)
{
    if (!enabled)
    {
        impl->subscribed = false;
        impl->Release();
    }
    else
    {
        impl->subscribed = true;
        try
        {
            impl->Prepare();
        }
        catch (...)
        {
            impl->subscribed = false;
            impl->Release();
            throw;
        }
    }
    return Health();
}
JsonObject MixerProvider::Read()
{
    impl->Prepare();
    winrt::com_ptr<IAudioSessionEnumerator> enumeration;
    winrt::check_hresult(impl->manager->GetSessionEnumerator(enumeration.put()));
    int count = 0;
    enumeration->GetCount(&count);
    std::map<std::wstring, std::unique_ptr<Session>> next;
    JsonArray rows;
    for (int n = 0; n < std::min(count, 64); n++)
    {
        winrt::com_ptr<IAudioSessionControl> control;
        if (FAILED(enumeration->GetSession(n, control.put())))
            continue;
        auto session = std::make_unique<Session>();
        session->control = control.as<IAudioSessionControl2>();
        session->volume = control.as<ISimpleAudioVolume>();
        AudioSessionState state{};
        session->control->GetState(&state);
        if (state == AudioSessionStateExpired)
            continue;
        auto id = SessionId(session->control.get());
        DWORD pid = 0;
        session->control->GetProcessId(&pid);
        LPWSTR display = nullptr;
        session->control->GetDisplayName(&display);
        std::wstring name = display ? display : L"";
        CoTaskMemFree(display);
        if (name.empty() && pid)
        {
            Handle process{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
            if (process.value)
                try
                {
                    auto path = Image(process);
                    name = path.substr(path.find_last_of(L"\\/") + 1);
                }
                catch (...)
                {
                }
        }
        if (name.empty())
            name = L"System sounds";
        float volume = 0;
        BOOL muted = FALSE;
        session->volume->GetMasterVolume(&volume);
        session->volume->GetMute(&muted);
        JsonObject row;
        Text(row, L"id", id);
        Text(row, L"name", name.substr(0, 512));
        Number(row, L"pid", pid);
        Number(row, L"volume", volume);
        Flag(row, L"muted", muted != FALSE);
        rows.Append(row);
        if (impl->subscribed)
        {
            auto existing = impl->sessions.find(id);
            if (existing != impl->sessions.end())
            {
                session = std::move(existing->second);
                impl->sessions.erase(existing);
            }
            else
            {
                session->callback = impl->callback;
                winrt::check_hresult(session->control->RegisterAudioSessionNotification(session->callback.get()));
                session->registered = true;
            }
        }
        next.emplace(id, std::move(session));
    }
    impl->sessions.swap(next);
    auto result = Health();
    Flag(result, L"available", true);
    Text(result, L"deviceId", impl->endpoint);
    result.Insert(L"sessions", rows);
    return result;
}
JsonObject MixerProvider::Set(JsonObject const &args)
{
    auto id = RequiredText(args, L"sessionId", 4096);
    auto found = impl->sessions.find(id);
    if (found == impl->sessions.end())
        throw std::invalid_argument("Audio session expired");
    auto expected = RequiredText(args, L"deviceId", 4096);
    winrt::com_ptr<IMMDevice> device;
    winrt::check_hresult(impl->enumerator->GetDefaultAudioEndpoint(eRender, eMultimedia, device.put()));
    if (expected != impl->endpoint || expected != EndpointId(device.get()))
        throw std::invalid_argument("Default output endpoint changed");
    AudioSessionState state{};
    winrt::check_hresult(found->second->control->GetState(&state));
    if (state == AudioSessionStateExpired || SessionId(found->second->control.get()) != id)
        throw std::invalid_argument("Audio session expired");
    auto action = RequiredText(args, L"action", 32);
    if (action == L"set-volume")
    {
        double value = args.GetNamedNumber(L"value");
        if (!std::isfinite(value) || value < 0 || value > 1)
            throw std::invalid_argument("Session volume outside 0..1");
        winrt::check_hresult(found->second->volume->SetMasterVolume(float(value), nullptr));
    }
    else if (action == L"set-mute")
        winrt::check_hresult(found->second->volume->SetMute(args.GetNamedBoolean(L"value"), nullptr));
    else
        throw std::invalid_argument("Unsupported session action");
    impl->state->Notify(2);
    JsonObject result;
    Flag(result, L"accepted", true);
    if(action==L"set-volume"){
        float actual=0;winrt::check_hresult(found->second->volume->GetMasterVolume(&actual));Number(result,L"value",actual);
    }
    return result;
}
} // namespace magic::system
