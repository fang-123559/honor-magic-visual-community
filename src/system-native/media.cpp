#include "providers.h"
#include <winrt/Windows.Media.Control.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/Windows.Security.Cryptography.h>
namespace magic::system
{
using namespace winrt::Windows::Media::Control;
using Session = GlobalSystemMediaTransportControlsSession;
using Manager = GlobalSystemMediaTransportControlsSessionManager;
namespace
{
std::wstring NewMediaId()
{
    GUID id{};
    winrt::check_hresult(CoCreateGuid(&id));
    wchar_t text[40]{};
    StringFromGUID2(id, text, 40);
    return L"media:" + std::wstring(text);
}
std::wstring Short(winrt::hstring const &text)
{
    return std::wstring(text).substr(0, 512);
}
struct SessionRegistration
{
    std::wstring id;
    Session session{nullptr};
    std::shared_ptr<std::atomic<uint64_t>> track=std::make_shared<std::atomic<uint64_t>>(1);
    Session::MediaPropertiesChanged_revoker metadata;
    Session::PlaybackInfoChanged_revoker playback;
    Session::TimelinePropertiesChanged_revoker timeline;
};
} // namespace
struct MediaProvider::Impl
{
    std::shared_ptr<ChangeState> state;
    Manager manager{nullptr};
    bool subscribed = false;
    std::shared_ptr<std::atomic<bool>> active = std::make_shared<std::atomic<bool>>(false);
    std::shared_ptr<std::atomic<bool>> dirty = std::make_shared<std::atomic<bool>>(true);
    Manager::SessionsChanged_revoker sessionsChanged;
    Manager::CurrentSessionChanged_revoker currentChanged;
    std::vector<SessionRegistration> sessions;
    explicit Impl(std::shared_ptr<ChangeState> s) : state(std::move(s))
    {
    }
    void Stop()
    {
        *active = false;
        sessions.clear();
        sessionsChanged.revoke();
        currentChanged.revoke();
        manager = nullptr;
        subscribed = false;
        *dirty = true;
    }
    ~Impl()
    {
        Stop();
    }
    void Prepare(ULONGLONG deadline)
    {
        if (!manager)
            manager = Await(Manager::RequestAsync(), state, deadline);
        if (subscribed && !sessionsChanged)
        {
            auto signal = state;
            auto invalid = dirty;
            auto live = active;
            sessionsChanged =
                manager.SessionsChanged(winrt::auto_revoke, [signal, invalid, live](auto const &, auto const &) {
                    if (!*live)
                        return;
                    *invalid = true;
                    signal->Notify(4);
                });
            currentChanged =
                manager.CurrentSessionChanged(winrt::auto_revoke, [signal, live](auto const &, auto const &) {
                    if (*live)
                        signal->Notify(4);
                });
        }
        if (!subscribed || dirty->exchange(false))
        {
            auto old = std::move(sessions);
            sessions.clear();
            for (auto const &session : manager.GetSessions())
            {
                if (sessions.size() >= 32)
                    break;
                SessionRegistration row;
                row.session = session;
                auto prior = std::find_if(old.begin(), old.end(), [&](auto const &item) {
                    return winrt::get_abi(item.session) == winrt::get_abi(session);
                });
                row.id = prior == old.end() ? NewMediaId() : prior->id;if(prior!=old.end())row.track=prior->track;
                if (subscribed)
                {
                    auto signal = state;
                    auto live = active;auto track=row.track;
                    row.metadata =
                        session.MediaPropertiesChanged(winrt::auto_revoke, [signal, live,track](auto const &, auto const &) {
                            if (*live){track->fetch_add(1);signal->Notify(4);}
                        });
                    row.playback =
                        session.PlaybackInfoChanged(winrt::auto_revoke, [signal, live](auto const &, auto const &) {
                            if (*live)
                                signal->Notify(4);
                        });
                    row.timeline = session.TimelinePropertiesChanged(winrt::auto_revoke,
                                                                     [signal, live](auto const &, auto const &) {
                                                                         if (*live)
                                                                             signal->Notify(4);
                                                                     });
                }
                sessions.push_back(std::move(row));
            }
        }
    }
    Session Resolve(std::wstring const &id)
    {
        auto found = std::find_if(sessions.begin(), sessions.end(), [&](auto const &item) { return item.id == id; });
        if (found == sessions.end())
            throw std::invalid_argument("Media session expired");
        return found->session;
    }
    JsonObject Health() const
    {
        JsonObject result;
        Flag(result, L"subscribed", subscribed);
        Number(result, L"cachedSessions", double(sessions.size()));
        Number(result, L"managerCallbacks", sessionsChanged ? 2 : 0);
        Number(result, L"sessionCallbacks", subscribed ? double(sessions.size() * 3) : 0);
        return result;
    }
};
MediaProvider::MediaProvider(std::shared_ptr<ChangeState> state) : impl(std::make_unique<Impl>(std::move(state)))
{
}
MediaProvider::~MediaProvider() = default;
JsonObject MediaProvider::Health() const
{
    return impl->Health();
}
JsonObject MediaProvider::Subscribe(bool enabled)
{
    if (!enabled)
        impl->Stop();
    else
    {
        if (!impl->subscribed)
            impl->active = std::make_shared<std::atomic<bool>>(true);
        impl->subscribed = true;
        try
        {
            impl->Prepare(GetTickCount64() + 4000);
        }
        catch (...)
        {
            impl->Stop();
            throw;
        }
    }
    return impl->Health();
}
JsonObject MediaProvider::Read()
{
    ULONGLONG deadline = GetTickCount64() + 4000;
    impl->Prepare(deadline);
    JsonObject result = impl->Health();
    JsonArray rows;
    auto current = impl->manager.GetCurrentSession();
    for (auto const &item : impl->sessions)
    {
        JsonObject row;
        Text(row, L"id", item.id);Text(row,L"trackId",item.id+L":"+std::to_wstring(item.track->load()));
        Text(row, L"appId", Short(item.session.SourceAppUserModelId()));
        Flag(row, L"current", current && winrt::get_abi(current) == winrt::get_abi(item.session));
        auto properties = Await(item.session.TryGetMediaPropertiesAsync(), impl->state, deadline);
        Text(row, L"title", Short(properties.Title()));
        Text(row, L"artist", Short(properties.Artist()));
        Text(row, L"album", Short(properties.AlbumTitle()));
        Flag(row, L"hasArtwork", bool(properties.Thumbnail()));
        auto playback = item.session.GetPlaybackInfo();
        Number(row, L"status", int(playback.PlaybackStatus()));
        auto rate = playback.PlaybackRate();
        Number(row, L"playbackRate", rate ? rate.Value() : 1.0);
        auto controls = playback.Controls();
        Flag(row, L"canPlay", controls.IsPlayEnabled());
        Flag(row, L"canPause", controls.IsPauseEnabled());
        Flag(row, L"canNext", controls.IsNextEnabled());
        Flag(row, L"canPrevious", controls.IsPreviousEnabled());
        auto timeline = item.session.GetTimelineProperties();
        Number(row, L"positionMs", double((timeline.Position()-timeline.StartTime()).count()) / 10000.0);
        Number(row, L"positionUpdatedUnixMs", double(timeline.LastUpdatedTime().time_since_epoch().count()) / 10000.0 - 11644473600000.0);
        Number(row,L"seekMinMs",std::max(0.,double((timeline.MinSeekTime()-timeline.StartTime()).count())/10000.));
        Number(row,L"seekMaxMs",std::max(0.,double((timeline.MaxSeekTime()-timeline.StartTime()).count())/10000.));
        Flag(row,L"canSeek",controls.IsPlaybackPositionEnabled()&&timeline.MaxSeekTime()>timeline.MinSeekTime());
        Number(row, L"durationMs", double((timeline.EndTime() - timeline.StartTime()).count()) / 10000.0);
        rows.Append(row);
    }
    Flag(result, L"available", true);
    result.Insert(L"sessions", rows);
    return result;
}
JsonObject MediaProvider::Command(JsonObject const &args)
{
    auto action = RequiredText(args, L"action", 32);
    if (action != L"play" && action != L"pause" && action != L"toggle" && action != L"next" && action != L"previous" && action != L"seek")
        throw std::invalid_argument("Unsupported media action");
    auto identity=RequiredText(args,L"sessionId",128);auto session=impl->Resolve(identity);
    auto offered=std::find_if(impl->sessions.begin(),impl->sessions.end(),[&](auto const &item){return item.id==identity;});
    auto version=args.TryLookup(L"trackId");if(version){if(version.ValueType()!=JsonValueType::String||std::wstring(version.GetString())!=identity+L":"+std::to_wstring(offered->track->load()))throw std::invalid_argument("Media track expired");}
    if(action==L"seek"&&!version)throw std::invalid_argument("Seek requires media track identity");
    bool accepted = false;
    ULONGLONG deadline = GetTickCount64() + 4000;
    if(action==L"seek"){
        auto value=args.TryLookup(L"positionMs");if(!value||value.ValueType()!=JsonValueType::Number)throw std::invalid_argument("Missing seek position");double ms=value.GetNumber();auto timeline=session.GetTimelineProperties();
        double low=double((timeline.MinSeekTime()-timeline.StartTime()).count())/10000.,high=double((timeline.MaxSeekTime()-timeline.StartTime()).count())/10000.;
        if(!session.GetPlaybackInfo().Controls().IsPlaybackPositionEnabled()||!std::isfinite(ms)||ms<std::max(0.,low)||ms>high||high<=low||ms>604800000.)throw std::invalid_argument("Seek unavailable or outside timeline");
        long double ticks=static_cast<long double>(timeline.StartTime().count())+ms*10000.L;if(ticks<0||ticks>INT64_MAX)throw std::invalid_argument("Seek timestamp exceeds bound");
        if(std::wstring(version.GetString())!=identity+L":"+std::to_wstring(offered->track->load()))throw std::invalid_argument("Media track changed during seek");
        accepted=Await(session.TryChangePlaybackPositionAsync(static_cast<int64_t>(ticks)),impl->state,deadline);
    }
    else if (action == L"play")
        accepted = Await(session.TryPlayAsync(), impl->state, deadline);
    else if (action == L"pause")
        accepted = Await(session.TryPauseAsync(), impl->state, deadline);
    else if (action == L"toggle")
        accepted = Await(session.TryTogglePlayPauseAsync(), impl->state, deadline);
    else if (action == L"next")
        accepted = Await(session.TrySkipNextAsync(), impl->state, deadline);
    else
        accepted = Await(session.TrySkipPreviousAsync(), impl->state, deadline);
    JsonObject result;
    Flag(result, L"accepted", accepted);
    return result;
}
JsonObject MediaProvider::Artwork(JsonObject const &args)
{
    auto session = impl->Resolve(RequiredText(args, L"sessionId", 128));
    ULONGLONG deadline = GetTickCount64() + 4000;
    auto properties = Await(session.TryGetMediaPropertiesAsync(), impl->state, deadline);
    JsonObject result;
    if (!properties.Thumbnail())
    {
        Flag(result, L"available", false);
        return result;
    }
    auto stream = Await(properties.Thumbnail().OpenReadAsync(), impl->state, deadline);
    try
    {
        auto size = stream.Size();
        if (!size || size > 256 * 1024)
        {
            stream.Close();
            Flag(result, L"available", false);
            Text(result, L"reason", L"Artwork exceeds bounded payload or is empty");
            return result;
        }
        winrt::Windows::Storage::Streams::DataReader reader{stream};
        try
        {
            uint32_t read = Await(reader.LoadAsync(uint32_t(size)), impl->state, deadline);
            auto buffer = reader.ReadBuffer(read);
            Text(result, L"base64",
                 winrt::Windows::Security::Cryptography::CryptographicBuffer::EncodeToBase64String(buffer));
            Text(result, L"contentType", stream.ContentType());
            Flag(result, L"available", true);
            reader.Close();
        }
        catch (...)
        {
            reader.Close();
            throw;
        }
        stream.Close();
        return result;
    }
    catch (...)
    {
        stream.Close();
        throw;
    }
}
} // namespace magic::system
