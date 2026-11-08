#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <map>
#include <algorithm>
#include <stdexcept>
namespace {
struct Handle{HANDLE h=nullptr;~Handle(){if(h&&h!=INVALID_HANDLE_VALUE)CloseHandle(h);}operator HANDLE()const{return h;}};
struct Record{HWND window;DWORD pid;unsigned long long created;bool visible;};
std::vector<Record> records;std::vector<HWND> fixture;DWORD parentId=0;HWND relay=nullptr;bool fixtureMode=false,revealed=false,mutating=false,userChanged=false,appbarChanged=false;UINT taskbarCreated=0;UINT originalState=0,leasedState=0;HANDLE logFile=INVALID_HANDLE_VALUE;
void Log(char const* phase){if(logFile==INVALID_HANDLE_VALUE)return;auto line=std::string("{\"tick\":")+std::to_string(GetTickCount64())+",\"phase\":\""+phase+"\",\"windows\":"+std::to_string(records.size())+",\"userChanged\":"+(userChanged?"true":"false")+"}\n";DWORD written=0;WriteFile(logFile,line.data(),DWORD(line.size()),&written,nullptr);}
unsigned long long Created(HANDLE p){FILETIME c{},e{},k{},u{};if(!GetProcessTimes(p,&c,&e,&k,&u))throw std::runtime_error("process identity unavailable");return (static_cast<unsigned long long>(c.dwHighDateTime)<<32)|c.dwLowDateTime;}
std::wstring Image(HANDLE p){std::wstring s(32768,L'\0');DWORD n=DWORD(s.size());if(!QueryFullProcessImageNameW(p,0,s.data(),&n))throw std::runtime_error("process image unavailable");s.resize(n);return s;}
bool Target(HWND window,DWORD& pid,unsigned long long& created){
    wchar_t cls[128]{};GetClassNameW(window,cls,128);GetWindowThreadProcessId(window,&pid);
    if(fixtureMode){if(pid!=parentId||std::find(fixture.begin(),fixture.end(),window)==fixture.end()||std::wstring(cls).rfind(L"MagicGuardFixture-",0)!=0)return false;}
    else if(wcscmp(cls,L"Shell_TrayWnd")&&wcscmp(cls,L"Shell_SecondaryTrayWnd"))return false;
    Handle p;p.h=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,pid);if(!p.h)return false;
    try{created=Created(p);if(!fixtureMode){wchar_t dir[32768]{};UINT n=GetWindowsDirectoryW(dir,32768);if(!n||_wcsicmp(Image(p).c_str(),(std::wstring(dir,n)+L"\\explorer.exe").c_str()))return false;}return true;}catch(...){return false;}
}
BOOL CALLBACK Capture(HWND window,LPARAM){DWORD pid=0;unsigned long long created=0;if(!Target(window,pid,created))return TRUE;
    auto old=std::find_if(records.begin(),records.end(),[&](auto const&r){return r.window==window&&r.pid==pid&&r.created==created;});
    if(old==records.end()&&records.size()<32)records.push_back({window,pid,created,IsWindowVisible(window)!=FALSE});
    if(!revealed)ShowWindowAsync(window,SW_HIDE);return TRUE;}
void Reconcile(){mutating=true;EnumWindows(Capture,0);mutating=false;Log("reconciled");}
UINT AppbarState(){APPBARDATA data{sizeof(data)};return UINT(SHAppBarMessage(ABM_GETSTATE,&data));}
void SetAppbar(UINT state){APPBARDATA data{sizeof(data)};data.lParam=state;SHAppBarMessage(ABM_SETSTATE,&data);}
void Visibility(bool show){mutating=true;revealed=show;for(auto const&r:records){DWORD pid=0;unsigned long long created=0;if(Target(r.window,pid,created)&&pid==r.pid&&created==r.created)ShowWindowAsync(r.window,show?SW_SHOWNOACTIVATE:SW_HIDE);}mutating=false;Log(show?"revealed":"hidden");}
void Restore(){mutating=true;for(auto const&r:records){DWORD pid=0;unsigned long long created=0;if(Target(r.window,pid,created)&&pid==r.pid&&created==r.created)ShowWindowAsync(r.window,r.visible?SW_SHOWNOACTIVATE:SW_HIDE);}if(!fixtureMode&&appbarChanged&&!userChanged&&AppbarState()==leasedState)SetAppbar(originalState);mutating=false;Log("restored");}
LRESULT CALLBACK Procedure(HWND window,UINT message,WPARAM w,LPARAM l){
    if(message==WM_APP+1||message==taskbarCreated||message==WM_DISPLAYCHANGE){Reconcile();return 0;}
    if(message==WM_SETTINGCHANGE&&!mutating&&!fixtureMode&&appbarChanged&&AppbarState()!=leasedState){userChanged=true;Log("settings-user-changed");}
    return DefWindowProcW(window,message,w,l);
}
void CALLBACK Changed(HWINEVENTHOOK,DWORD,HWND window,LONG object,LONG child,DWORD,DWORD){if(mutating||object!=OBJID_WINDOW||child!=CHILDID_SELF)return;DWORD pid=0;unsigned long long created=0;if(Target(window,pid,created)&&(!std::any_of(records.begin(),records.end(),[&](auto const&r){return r.window==window&&r.pid==pid&&r.created==created;})||(!revealed&&IsWindowVisible(window))))PostMessageW(relay,WM_APP+1,0,0);}
}
int wmain(int argc,wchar_t** argv){
    try{
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        std::map<std::wstring,std::wstring> args;for(int i=1;i<argc;i+=2){if(i+1>=argc||!args.emplace(argv[i],argv[i+1]).second)throw std::runtime_error("invalid arguments");}
        auto required=[&](wchar_t const* key)->std::wstring{auto i=args.find(key);if(i==args.end()||i->second.empty())throw std::runtime_error("missing argument");return i->second;};
        parentId=DWORD(std::stoul(required(L"--parent")));Handle parent;parent.h=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,parentId);
        if(!parent.h||Created(parent)!=std::stoull(required(L"--created"))||_wcsicmp(Image(parent).c_str(),required(L"--image").c_str()))throw std::runtime_error("parent identity differs");
        auto prefix=required(L"--events");if(prefix.rfind(L"Local\\MagicTaskbar-",0)!=0||prefix.size()!=51)throw std::runtime_error("invalid event namespace");
        Handle stop,show,hide,ready,expired;stop.h=OpenEventW(SYNCHRONIZE,FALSE,(prefix+L"-stop").c_str());show.h=OpenEventW(SYNCHRONIZE|EVENT_MODIFY_STATE,FALSE,(prefix+L"-show").c_str());hide.h=OpenEventW(SYNCHRONIZE|EVENT_MODIFY_STATE,FALSE,(prefix+L"-hide").c_str());ready.h=OpenEventW(EVENT_MODIFY_STATE,FALSE,(prefix+L"-ready").c_str());expired.h=OpenEventW(EVENT_MODIFY_STATE,FALSE,(prefix+L"-expired").c_str());if(!stop.h||!show.h||!hide.h||!ready.h||!expired.h)throw std::runtime_error("owner events missing");
        auto expiry=std::stoull(required(L"--expires"));FILETIME now{};GetSystemTimeAsFileTime(&now);auto time=(static_cast<unsigned long long>(now.dwHighDateTime)<<32)|now.dwLowDateTime;auto mode=args.contains(L"--mode")?required(L"--mode"):L"timed";if(mode!=L"timed"&&mode!=L"continuous")throw std::runtime_error("unknown acceptance mode");bool continuous=mode==L"continuous";if(continuous?expiry!=0:expiry<=time||expiry-time>3600ULL*10000000ULL)throw std::runtime_error("acceptance deadline differs from mode");
        if(args.contains(L"--fixture")){fixtureMode=true;auto list=required(L"--fixture");size_t at=0;while(at<list.size()){size_t end=list.find(L',',at);fixture.push_back(reinterpret_cast<HWND>(std::stoull(list.substr(at,end-at))));if(end==list.npos)break;at=end+1;}if(fixture.empty()||fixture.size()>16)throw std::runtime_error("fixture bound");}
        auto ownedProcess=[&](wchar_t const* id,wchar_t const* created,wchar_t const* image,HANDLE& result){DWORD pid=DWORD(std::stoul(required(id)));result=OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);if(!result||Created(result)!=std::stoull(required(created))||_wcsicmp(Image(result).c_str(),required(image).c_str()))throw std::runtime_error("renderer/backend identity differs");};
        Handle renderer,backend;ownedProcess(L"--renderer",L"--renderer-created",L"--renderer-image",renderer.h);ownedProcess(L"--backend",L"--backend-created",L"--backend-image",backend.h);
        logFile=CreateFileW(required(L"--log").c_str(),FILE_APPEND_DATA,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(logFile==INVALID_HANDLE_VALUE)throw std::runtime_error("new log required");
        WNDCLASSW cls{};cls.hInstance=GetModuleHandleW(nullptr);cls.lpfnWndProc=Procedure;cls.lpszClassName=L"MagicIndependentTaskbarGuard";if(!RegisterClassW(&cls))throw std::runtime_error("guard class failed");relay=CreateWindowExW(0,cls.lpszClassName,L"",0,0,0,0,0,nullptr,nullptr,cls.hInstance,nullptr);if(!relay)throw std::runtime_error("guard window failed");taskbarCreated=RegisterWindowMessageW(L"TaskbarCreated");
        // Auto-reset consumes the reveal timeout once. CancelWaitableTimer does
        // not clear an already-signaled manual-reset timer, which caused a busy
        // loop repeatedly hiding the taskbar and appending the same log line.
        Handle deadline,revealDeadline;deadline.h=continuous?CreateEventW(nullptr,TRUE,FALSE,nullptr):CreateWaitableTimerW(nullptr,TRUE,nullptr);revealDeadline.h=CreateWaitableTimerW(nullptr,FALSE,nullptr);LARGE_INTEGER due{};due.QuadPart=static_cast<LONGLONG>(expiry);if(!deadline.h||!revealDeadline.h||(!continuous&&!SetWaitableTimer(deadline,&due,0,nullptr,nullptr,FALSE)))throw std::runtime_error("lease timer failed");
        originalState=fixtureMode?0:AppbarState();if(!fixtureMode&&originalState!=std::stoul(required(L"--original-state")))throw std::runtime_error("taskbar state changed before lease acquisition");leasedState=originalState|ABS_AUTOHIDE;bool active=false;HWINEVENTHOOK hook=nullptr;
        try{
            if(!fixtureMode&&originalState!=leasedState){mutating=true;SetAppbar(leasedState);mutating=false;appbarChanged=AppbarState()==leasedState;}
            active=true;Reconcile();if(records.empty())throw std::runtime_error("no validated taskbar or fixture");
            hook=SetWinEventHook(EVENT_OBJECT_CREATE,EVENT_OBJECT_SHOW,nullptr,Changed,0,0,WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS);if(!hook)throw std::runtime_error("guard events failed");
            SetEvent(ready);Log(continuous?"ready-continuous":"ready");HANDLE waits[]{parent,renderer,backend,stop,show,hide,deadline,revealDeadline};bool run=true;
            while(run){DWORD value=MsgWaitForMultipleObjects(8,waits,FALSE,INFINITE,QS_ALLINPUT);
                if(value>=WAIT_OBJECT_0&&value<=WAIT_OBJECT_0+3)run=false;
                else if(value==WAIT_OBJECT_0+4){ResetEvent(show);Visibility(true);LARGE_INTEGER delay{};delay.QuadPart=-80000000;SetWaitableTimer(revealDeadline,&delay,0,nullptr,nullptr,FALSE);}
                else if(value==WAIT_OBJECT_0+5||value==WAIT_OBJECT_0+7){ResetEvent(hide);CancelWaitableTimer(revealDeadline);Visibility(false);}
                else if(value==WAIT_OBJECT_0+6){SetEvent(expired);Log("lease-expired");run=false;}
                else if(value==WAIT_OBJECT_0+8){MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}}
                else throw std::runtime_error("guard wait failed");
            }
        }catch(...){if(hook)UnhookWinEvent(hook);if(active)Restore();DestroyWindow(relay);CloseHandle(logFile);throw;}
        if(hook)UnhookWinEvent(hook);Restore();DestroyWindow(relay);CloseHandle(logFile);return 0;
    }catch(std::exception const& error){OutputDebugStringA(error.what());return 2;}
}
