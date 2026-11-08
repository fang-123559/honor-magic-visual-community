#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <appmodel.h>
#include <sddl.h>
#include <shellapi.h>
#include <shlobj.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Data.Json.h>
#include <filesystem>
#include <string>
#include <vector>
#include "window-action.h"
#include "../shared/trusted-win32-install.h"

// The elevated helper accepts its own build's package family only.
#ifndef MAGIC_WINDOW_PARENT_FAMILY
#define MAGIC_WINDOW_PARENT_FAMILY L"MagicCommunity.Native_x52pfh0dth434"
#endif
using namespace winrt::Windows::Data::Json;
namespace {
struct Handle { HANDLE h=nullptr; explicit Handle(HANDLE value=nullptr):h(value){} ~Handle(){if(h&&h!=INVALID_HANDLE_VALUE)CloseHandle(h);} Handle(Handle const&)=delete; };
uint64_t Created(HANDLE p){FILETIME c{},e{},k{},u{};if(!GetProcessTimes(p,&c,&e,&k,&u))throw 1;return (uint64_t(c.dwHighDateTime)<<32)|c.dwLowDateTime;}
std::wstring Image(HANDLE p){wchar_t b[32768];DWORD n=32768;if(!QueryFullProcessImageNameW(p,0,b,&n))throw 1;return {b,n};}
std::wstring Canonical(std::wstring const& path){
    Handle f(CreateFileW(path.c_str(),FILE_READ_ATTRIBUTES,FILE_SHARE_READ|FILE_SHARE_WRITE|FILE_SHARE_DELETE,nullptr,OPEN_EXISTING,FILE_FLAG_BACKUP_SEMANTICS,nullptr));
    wchar_t b[32768];DWORD n=GetFinalPathNameByHandleW(f.h,b,32768,FILE_NAME_NORMALIZED|VOLUME_NAME_DOS);
    if(!n||n>=32768)throw 1;return {b,n};
}
std::wstring Sid(HANDLE p) {
    HANDLE t=nullptr;if(!OpenProcessToken(p,TOKEN_QUERY,&t))throw 1;Handle token(t);
    DWORD n=0;GetTokenInformation(t,TokenUser,nullptr,0,&n);std::vector<BYTE>b(n);
    if(!GetTokenInformation(t,TokenUser,b.data(),n,&n))throw 1;
    LPWSTR sid=nullptr;if(!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(b.data())->User.Sid,&sid))throw 1;
    std::wstring result(sid);LocalFree(sid);return result;
}
DWORD Session(DWORD pid){DWORD n=0;if(!ProcessIdToSessionId(pid,&n))throw 1;return n;}
bool Elevated(){HANDLE t=nullptr;if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&t))return false;Handle token(t);TOKEN_ELEVATION e{};DWORD n=0;return GetTokenInformation(t,TokenElevation,&e,sizeof(e),&n)&&e.TokenIsElevated;}
bool Transfer(HANDLE pipe,HANDLE parent,void* bytes,DWORD length,bool write,DWORD timeout){
    auto deadline=timeout==INFINITE?0:GetTickCount64()+timeout;
    while(length) {
        Handle event(CreateEventW(nullptr,TRUE,FALSE,nullptr));OVERLAPPED ov{};ov.hEvent=event.h;DWORD done=0;
        BOOL ok=write?WriteFile(pipe,bytes,length,&done,&ov):ReadFile(pipe,bytes,length,&done,&ov);
        if(!ok) {
            if(GetLastError()!=ERROR_IO_PENDING)return false;
            HANDLE waits[]{parent,event.h};DWORD remaining=timeout==INFINITE?INFINITE:DWORD(deadline>GetTickCount64()?deadline-GetTickCount64():0);
            if(WaitForMultipleObjects(2,waits,FALSE,remaining)!=WAIT_OBJECT_0+1){CancelIoEx(pipe,&ov);GetOverlappedResult(pipe,&ov,&done,TRUE);return false;}
            if(!GetOverlappedResult(pipe,&ov,&done,FALSE))return false;
        }
        if(!done)return false;bytes=static_cast<BYTE*>(bytes)+done;length-=done;
    }return true;
}
JsonObject Receive(HANDLE pipe,HANDLE parent){
    DWORD n=0;if(!Transfer(pipe,parent,&n,4,false,INFINITE)||n==0||n>16384)throw 1;
    std::string s(n,'\0');if(!Transfer(pipe,parent,s.data(),n,false,3000))throw 1;
    return JsonObject::Parse(winrt::to_hstring(s));
}
void Send(HANDLE pipe,HANDLE parent,JsonObject const& data){
    auto s=winrt::to_string(data.Stringify());DWORD n=DWORD(s.size());
    if(n>16384||!Transfer(pipe,parent,&n,4,true,3000)||!Transfer(pipe,parent,s.data(),n,true,3000))throw 1;
}
void Text(JsonObject const&o,wchar_t const*k,std::wstring_view v){o.Insert(k,JsonValue::CreateStringValue(winrt::hstring(v)));}
void Flag(JsonObject const&o,wchar_t const*k,bool v){o.Insert(k,JsonValue::CreateBooleanValue(v));}
uint64_t Integer(JsonObject const&o,wchar_t const*k) {
    std::wstring s(o.GetNamedString(k));if(s.empty()||s.size()>20||s.find_first_not_of(L"0123456789")!=s.npos)throw 1;
    return std::stoull(s);
}
void Serve(HANDLE pipe,HANDLE parent,DWORD pid,uint64_t created,DWORD session,std::wstring const& sid,std::wstring const& nonce){
    ULONG server=0;
    JsonObject hello;Text(hello,L"protocol",L"Magic.WindowAction/1");Text(hello,L"nonce",nonce);Send(pipe,parent,hello);
    uint64_t sequence=0;
    while(WaitForSingleObject(parent,0)==WAIT_TIMEOUT) {
        auto request=Receive(pipe,parent);JsonObject response;
        auto requestId=std::wstring(request.GetNamedString(L"id",L""));
        Text(response,L"id",requestId);Text(response,L"protocol",L"Magic.WindowAction/1");
        if(request.GetNamedString(L"protocol",L"")!=L"Magic.WindowAction/1"){Text(response,L"status",L"protocol_mismatch");Send(pipe,parent,response);break;}
        if(requestId.size()!=32||requestId.find_first_not_of(L"0123456789abcdef")!=requestId.npos||Integer(request,L"sequence")!=++sequence)throw 1;
        if(!GetNamedPipeServerProcessId(pipe,&server)||server!=pid||Created(parent)!=created)throw 3;
        auto action=std::wstring(request.GetNamedString(L"action"));
        if(action==L"shutdown"){Flag(response,L"accepted",true);Text(response,L"status",L"stopped");Send(pipe,parent,response);break;}
        if(action!=L"activate"&&action!=L"restore"&&action!=L"minimize"&&action!=L"close"){Text(response,L"status",L"unsupported_action");Flag(response,L"accepted",false);Send(pipe,parent,response);continue;}
        HWND hwnd=reinterpret_cast<HWND>(uintptr_t(Integer(request,L"hwnd")));DWORD targetPid=DWORD(Integer(request,L"pid"));
        Handle target(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|SYNCHRONIZE,FALSE,targetPid));DWORD actual=0;
        auto match=[&](){
            GetWindowThreadProcessId(hwnd,&actual);
            return IsWindow(hwnd)&&IsWindowVisible(hwnd)&&GetAncestor(hwnd,GA_ROOT)==hwnd&&actual==targetPid&&targetPid!=pid&&targetPid!=GetCurrentProcessId()&&target.h&&
                WaitForSingleObject(target.h,0)==WAIT_TIMEOUT&&Created(target.h)==Integer(request,L"created")&&Session(targetPid)==session&&Sid(target.h)==sid&&
                !_wcsicmp(Canonical(Image(target.h)).c_str(),Canonical(std::wstring(request.GetNamedString(L"path"))).c_str());
        };
        bool valid=false;try{valid=match();}catch(...){}
        if(!valid){Text(response,L"status",L"target_expired");Flag(response,L"accepted",false);Send(pipe,parent,response);continue;}
        auto result=magic::window_action::Perform(hwnd,action);
        // A successful close may destroy the HWND before this read. The exact
        // target was authenticated immediately before posting WM_CLOSE.
        if(action!=L"close")try{valid=match();}catch(...){valid=false;}
        Flag(response,L"accepted",valid&&result.accepted);Flag(response,L"restored",valid&&result.restored);Flag(response,L"activated",valid&&result.activated);Flag(response,L"minimized",valid&&result.minimized);
        response.Insert(L"win32Error",JsonValue::CreateNumberValue(result.error));
        Flag(response,L"closeRequested",action==L"close"&&valid&&result.accepted);
        Text(response,L"status",!valid?L"target_expired":result.accepted?(action==L"close"?L"close_requested":L"completed"):result.error==ERROR_ACCESS_DENIED?L"win32_denied":L"state_not_reached");
        Send(pipe,parent,response);
    }
}

}
int WINAPI wWinMain(HINSTANCE,HINSTANCE,PWSTR,int){
 try {
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    int argc=0;auto argv=CommandLineToArgvW(GetCommandLineW(),&argc);
    if(!argv || argc!=5)return 2;
    std::wstring pipeName=argv[1],parentId=argv[2],created=argv[3],nonce=argv[4];LocalFree(argv);
    if(nonce.size()!=32 || nonce.find_first_not_of(L"0123456789abcdef")!=nonce.npos || pipeName!=L"Magic.WindowAction."+nonce)return 2;
    DWORD pid=DWORD(std::stoul(parentId));Handle parent(OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid));
    if(!parent.h||Created(parent.h)!=std::stoull(created)||WaitForSingleObject(parent.h,0)!=WAIT_TIMEOUT||!Elevated())return 3;
    auto sid=Sid(parent.h);DWORD session=Session(pid);
    if(!session||session!=Session(GetCurrentProcessId())||sid!=Sid(GetCurrentProcess()))return 3;
    wchar_t self[32768];DWORD selfLen=GetModuleFileNameW(nullptr,self,32768);if(!selfLen||selfLen>=32768)return 3;
    auto own=Canonical(std::wstring(self,selfLen));
    auto directory=std::filesystem::path(own).parent_path();
    if(_wcsicmp(Canonical(Image(parent.h)).c_str(),(directory/L"MagicCommunity.exe").c_str()))return 3;
    UINT32 length=0;
    auto packageStatus=GetPackageFullName(parent.h,&length,nullptr);
    if(packageStatus==APPMODEL_ERROR_NO_PACKAGE) {
        if(!magic::installed::Tree(directory))return 3;
    } else {
    if(packageStatus!=ERROR_INSUFFICIENT_BUFFER||length>512)return 3;
    std::wstring full(length,L'\0');if(GetPackageFullName(parent.h,&length,full.data())!=ERROR_SUCCESS)return 3;full.resize(wcslen(full.c_str()));
    if(full.rfind(L"MagicCommunity.Native_",0)!=0)return 3;
    length=0;if(GetPackagePathByFullName(full.c_str(),&length,nullptr)!=ERROR_INSUFFICIENT_BUFFER||length>32768)return 3;
    std::wstring package(length,L'\0');if(GetPackagePathByFullName(full.c_str(),&length,package.data())!=ERROR_SUCCESS)return 3;package.resize(wcslen(package.c_str()));
    // OS-registered package in WindowsApps, exact same immutable installation.
    length=0;if(GetPackageFamilyName(parent.h,&length,nullptr)!=ERROR_INSUFFICIENT_BUFFER||length>512)return 3;
    std::wstring family(length,L'\0');if(GetPackageFamilyName(parent.h,&length,family.data())!=ERROR_SUCCESS||wcscmp(family.c_str(),MAGIC_WINDOW_PARENT_FAMILY))return 3;
    PWSTR programFiles=nullptr;if(FAILED(SHGetKnownFolderPath(FOLDERID_ProgramFiles,0,nullptr,&programFiles)))return 3;
    auto protectedRoot=Canonical(std::wstring(programFiles)+L"\\WindowsApps")+L"\\";CoTaskMemFree(programFiles);
    auto packageRoot=Canonical(package);
    if(_wcsnicmp(packageRoot.c_str(),protectedRoot.c_str(),protectedRoot.size())||_wcsicmp(directory.c_str(),packageRoot.c_str()))return 3;
    }
    std::wstring mutexName=L"Local\\Magic.WindowAction."+sid+L"."+std::to_wstring(session);
    Handle mutex(CreateMutexW(nullptr,FALSE,mutexName.c_str()));if(!mutex.h||WaitForSingleObject(mutex.h,0)!=WAIT_OBJECT_0)return 4;
    std::wstring endpoint=L"\\\\.\\pipe\\"+pipeName;Handle pipe;
    for(int i=0;i<50;i++){
        pipe.h=CreateFileW(endpoint.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);
        if(pipe.h!=INVALID_HANDLE_VALUE)break;
        if(WaitForSingleObject(parent.h,100)!=WAIT_TIMEOUT)return 3;
    }
    if(pipe.h==INVALID_HANDLE_VALUE)return 5;
    ULONG server=0;if(!GetNamedPipeServerProcessId(pipe.h,&server)||server!=pid)return 3;
    Serve(pipe.h,parent.h,pid,std::stoull(created),session,sid,nonce);
    ReleaseMutex(mutex.h);return 0;
 } catch(...){return 6;}
}
