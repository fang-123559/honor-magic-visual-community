#pragma once
#include "native-calendar-data.h"
// Format the toolbar date with locally copied Moment locale tables. It never
// loads JavaScript or reads a Seelen installation at runtime.
inline constexpr auto NativeClockRegistryPath=L"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced";
inline constexpr UINT NativeClockPreferenceMessage=WM_APP+64;
// One outstanding Windows registry notification for the renderer, shared by all monitors.
struct NativeClockPreferenceWatch {
    HWND relay=nullptr; HKEY key=nullptr; HANDLE signal=nullptr; PTP_WAIT wait=nullptr;
    NativeClockPreferenceWatch()=default;
    NativeClockPreferenceWatch(NativeClockPreferenceWatch const&)=delete;
    ~NativeClockPreferenceWatch(){
        if(wait){SetThreadpoolWait(wait,nullptr,nullptr);WaitForThreadpoolWaitCallbacks(wait,TRUE);CloseThreadpoolWait(wait);}
        if(key)RegCloseKey(key);
        if(signal)CloseHandle(signal);
    }
    void Arm(){
        if(wait&&RegNotifyChangeKeyValue(key,FALSE,REG_NOTIFY_CHANGE_LAST_SET|REG_NOTIFY_THREAD_AGNOSTIC,signal,TRUE)==ERROR_SUCCESS)
            SetThreadpoolWait(wait,signal,nullptr);
    }
    void Start(HWND window,wchar_t const* path=NativeClockRegistryPath){
        if(!window||RegOpenKeyExW(HKEY_CURRENT_USER,path,0,KEY_NOTIFY,&key)!=ERROR_SUCCESS)return;
        relay=window;signal=CreateEventW(nullptr,FALSE,FALSE,nullptr);
        if(signal)wait=CreateThreadpoolWait([](PTP_CALLBACK_INSTANCE,void* context,PTP_WAIT,TP_WAIT_RESULT){
            PostMessageW(static_cast<NativeClockPreferenceWatch*>(context)->relay,NativeClockPreferenceMessage,0,0);
        },this,nullptr);
        Arm();
    }
};
inline std::optional<bool> nativeClockSecondsPreference;
inline void InvalidateNativeClockSeconds(){nativeClockSecondsPreference.reset();}
inline bool NativeClockShowSeconds(){
    if(!nativeClockSecondsPreference){DWORD value=0,size=sizeof(value);auto status=RegGetValueW(HKEY_CURRENT_USER,NativeClockRegistryPath,L"ShowSecondsInSystemClock",RRF_RT_REG_DWORD,nullptr,&value,&size);nativeClockSecondsPreference=status==ERROR_SUCCESS&&value==1;}
    return *nativeClockSecondsPreference;
}
inline auto &NativeClockLocaleCache(){static std::map<std::wstring,JsonObject> cache;return cache;}
inline JsonObject NativeClockLocale(std::wstring language) {
    auto &cache=NativeClockLocaleCache();
    std::transform(language.begin(),language.end(),language.begin(),::towlower);if(language==L"zh")language=L"zh-cn";
    auto find=[](std::wstring const &lang){return std::find_if(std::begin(NativeCalendarLocaleAssets),std::end(NativeCalendarLocaleAssets),[&](auto const &asset){return lang==asset.language;});};
    auto asset=find(language);if(asset==std::end(NativeCalendarLocaleAssets)){auto dash=language.find(L'-');if(dash!=language.npos)asset=find(language.substr(0,dash));}if(asset==std::end(NativeCalendarLocaleAssets))asset=find(L"en");
    auto found=cache.find(asset->language);if(found!=cache.end())return found->second;if(cache.size()>=2)cache.clear();auto parsed=JsonObject::Parse(to_hstring(asset->json));cache.emplace(asset->language,parsed);return parsed;

}
inline std::wstring NativeClockPattern(std::wstring const &pattern,JsonObject const &locale) {
    auto formats=locale.GetNamedObject(L"formats");auto result=pattern;
    for(int pass=0;pass<6;++pass){std::wstring next;bool replaced=false;
        for(size_t i=0;i<result.size();){
            if(result[i]==L'['){auto end=result.find(L']',i+1);if(end!=result.npos){next+=result.substr(i,end-i+1);i=end+1;continue;}}
            if(result[i]==L'\\'&&i+1<result.size()){next+=result.substr(i,2);i+=2;continue;}
            bool matched=false;for(auto key:{L"LTS",L"LT",L"LLLL",L"LLL",L"LL",L"L",L"llll",L"lll",L"ll",L"l"}){
                auto count=wcslen(key);if(result.compare(i,count,key)==0){auto value=Str(formats,key);if(!value.empty()){next+=value;i+=count;matched=true;replaced=true;break;}}
            }if(!matched)next+=result[i++];
        }result=std::move(next);if(!replaced)break;
    }return result;
}
// Format tokens inside [] or escaped literals are never rewritten.
inline std::wstring NativeClockApplySeconds(std::wstring pattern,bool show){
    bool hasSeconds=false;std::wstring result;
    for(size_t i=0;i<pattern.size();){
        if(pattern[i]==L'['){auto end=pattern.find(L']',i+1);if(end==pattern.npos){result+=pattern.substr(i);break;}result+=pattern.substr(i,end-i+1);i=end+1;continue;}
        if(pattern[i]==L'\\'&&i+1<pattern.size()){result+=pattern.substr(i,2);i+=2;continue;}
        wchar_t c=pattern[i];size_t end=i+1;while(end<pattern.size()&&pattern[end]==c)++end;
        if(c==L's'||c==L'S'){hasSeconds=true;if(show)result+=pattern.substr(i,end-i);else{if(!result.empty()&&(result.back()==L':'||result.back()==L'.'))result.pop_back();else if(end<pattern.size()&&pattern[end]==L':')++end;}}
        else result+=pattern.substr(i,end-i);i=end;
    }
    if(!show||hasSeconds)return result;
    for(size_t i=0;i<result.size();){
        if(result[i]==L'['){auto end=result.find(L']',i+1);if(end==result.npos)break;i=end+1;continue;}
        if(result[i]==L'\\'){i+=2;continue;}
        if(result[i]==L'm'){size_t end=i+1;while(end<result.size()&&result[end]==L'm')++end;result.insert(end,L":ss");break;}++i;
    }return result;
}
inline std::wstring NativeClockEffectivePattern(JsonObject const &data){auto pattern=Str(data,L"dateFormat");if(pattern.empty())pattern=L"ddd D MMM, hh:mm A";return NativeClockApplySeconds(NativeClockPattern(pattern,NativeClockLocale(Str(data,L"language"))),NativeClockShowSeconds());}
inline std::wstring NativeWindowsClock(SYSTEMTIME const &now, bool seconds, wchar_t const *locale=LOCALE_NAME_USER_DEFAULT) {
    wchar_t date[256]{}, time[256]{}, pattern[256]{};
    GetLocaleInfoEx(locale,seconds?LOCALE_STIMEFORMAT:LOCALE_SSHORTTIME,pattern,256);
    if(!GetDateFormatEx(locale,DATE_SHORTDATE,&now,nullptr,date,256,nullptr))
        swprintf_s(date,L"%04u/%02u/%02u",now.wYear,now.wMonth,now.wDay);
    if(!GetTimeFormatEx(locale,seconds?0:TIME_NOSECONDS,&now,pattern[0]?pattern:nullptr,time,256))
        swprintf_s(time,seconds?L"%02u:%02u:%02u":L"%02u:%02u",now.wHour,now.wMinute,now.wSecond);
    return std::wstring(date)+L" "+time;
}
inline std::wstring ToolbarClock(JsonObject const &) {
    SYSTEMTIME now{};GetLocalTime(&now);
    return NativeWindowsClock(now,NativeClockShowSeconds());
}
inline bool ToolbarClockSeconds(JsonObject const &) { return NativeClockShowSeconds(); }
