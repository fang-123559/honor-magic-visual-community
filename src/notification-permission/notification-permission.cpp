#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "../shared/native-ui-font.h"
#include <appmodel.h>
#include <shellapi.h>
#include <dispatcherqueue.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.Notifications.Management.h>
#include <string>
#include <atomic>
using namespace winrt::Windows::UI::Notifications::Management;
namespace {
HWND mainWindow=nullptr,requestButton=nullptr,statusText=nullptr;HFONT font=nullptr;
winrt::Windows::Foundation::IAsyncOperation<UserNotificationListenerAccessStatus> pending{nullptr};
std::atomic<LONG> lastError{0};std::atomic<int> access{0};int result=3;
void State(){auto status=UserNotificationListener::Current().GetAccessStatus();access=int(status);SetWindowTextW(statusText,status==UserNotificationListenerAccessStatus::Allowed?L"已允许。Magic 视界可以在通知面板中显示和清除通知。":status==UserNotificationListenerAccessStatus::Denied?L"权限未允许。可以在 Windows 设置中为 Magic 视界单独开启。":L"允许 Magic 视界在自己的通知面板中显示来自其他应用的通知。\r\n你可以随时在 Windows 设置中撤销权限。");if(status==UserNotificationListenerAccessStatus::Allowed){SetWindowTextW(requestButton,L"启用通知面板");}}
LRESULT CALLBACK Procedure(HWND h,UINT message,WPARAM w,LPARAM l){
    if(message==WM_COMMAND&&LOWORD(w)==1){
        if(access==int(UserNotificationListenerAccessStatus::Allowed)){result=0;DestroyWindow(h);return 0;}
        if(pending)return 0;
        try{EnableWindow(requestButton,FALSE);pending=UserNotificationListener::Current().RequestAccessAsync();pending.Completed([h](auto const&operation,auto status){try{if(status==winrt::Windows::Foundation::AsyncStatus::Completed)access=int(operation.GetResults());else lastError=operation.ErrorCode().value;}catch(winrt::hresult_error const&error){lastError=error.code().value;}PostMessageW(h,WM_APP+1,0,0);});SetTimer(h,1,180000,nullptr);}
        catch(winrt::hresult_error const&error){lastError=error.code().value;PostMessageW(h,WM_APP+1,0,0);}return 0;
    }
    if(message==WM_COMMAND&&LOWORD(w)==2){ShellExecuteW(h,L"open",L"ms-settings:privacy-notifications",nullptr,nullptr,SW_SHOWNORMAL);return 0;}
    if(message==WM_APP+1){KillTimer(h,1);pending=nullptr;EnableWindow(requestButton,TRUE);if(lastError){SetWindowTextW(statusText,L"Windows 未完成授权请求。请使用下方的 Windows 通知隐私设置入口。权限不会自动开启。");}else try{State();if(access==int(UserNotificationListenerAccessStatus::Allowed)){result=0;DestroyWindow(h);}}catch(...){SetWindowTextW(statusText,L"暂时无法读取权限状态，请使用 Windows 设置查看。");}return 0;}
    if(message==WM_TIMER&&w==1){if(pending)pending.Cancel();KillTimer(h,1);EnableWindow(requestButton,TRUE);SetWindowTextW(statusText,L"授权请求已超时，可关闭后重新打开。权限不会自动开启。");return 0;}
    if(message==WM_DESTROY){if(pending)pending.Cancel();PostQuitMessage(result);return 0;}
    return DefWindowProcW(h,message,w,l);
}
}
int WINAPI wWinMain(HINSTANCE instance,HINSTANCE,PWSTR,int){
    try{
        UINT32 length=0;auto identity=GetCurrentPackageFamilyName(&length,nullptr);
        if(identity!=APPMODEL_ERROR_NO_PACKAGE){if(identity!=ERROR_INSUFFICIENT_BUFFER||length>512)return 3;std::wstring family(length,L'\0');if(GetCurrentPackageFamilyName(&length,family.data())!=ERROR_SUCCESS||family.rfind(L"MagicCommunity.Native_",0)!=0)return 3;}
        winrt::init_apartment(winrt::apartment_type::single_threaded);SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        winrt::Windows::System::DispatcherQueueController queue{nullptr};DispatcherQueueOptions options{sizeof(options),DQTYPE_THREAD_CURRENT,DQTAT_COM_STA};winrt::check_hresult(CreateDispatcherQueueController(options,reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(winrt::put_abi(queue))));
        WNDCLASSW cls{};cls.hInstance=instance;cls.lpszClassName=L"MagicIndependentNotificationPermission";cls.lpfnWndProc=Procedure;cls.hCursor=LoadCursorW(nullptr,IDC_ARROW);cls.hbrBackground=reinterpret_cast<HBRUSH>(COLOR_WINDOW+1);if(!RegisterClassW(&cls))return 3;
        int width=620,height=265;mainWindow=CreateWindowExW(WS_EX_DLGMODALFRAME,cls.lpszClassName,L"Magic 视界社区版 · 通知权限",WS_OVERLAPPED|WS_CAPTION|WS_SYSMENU,(GetSystemMetrics(SM_CXSCREEN)-width)/2,(GetSystemMetrics(SM_CYSCREEN)-height)/2,width,height,nullptr,nullptr,instance,nullptr);if(!mainWindow)return 3;
        font=CreateFontW(-17,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,CLEARTYPE_QUALITY,DEFAULT_PITCH,NativeUiFont::Family().c_str());
        statusText=CreateWindowExW(0,L"STATIC",L"",WS_CHILD|WS_VISIBLE,26,24,562,100,mainWindow,nullptr,instance,nullptr);
        requestButton=CreateWindowExW(0,L"BUTTON",L"请求 Windows 授权",WS_CHILD|WS_VISIBLE|WS_TABSTOP|BS_DEFPUSHBUTTON,26,158,180,35,mainWindow,reinterpret_cast<HMENU>(1),instance,nullptr);
        auto settings=CreateWindowExW(0,L"BUTTON",L"Windows 通知隐私设置",WS_CHILD|WS_VISIBLE|WS_TABSTOP,228,158,220,35,mainWindow,reinterpret_cast<HMENU>(2),instance,nullptr);
        for(auto window:{statusText,requestButton,settings})SendMessageW(window,WM_SETFONT,reinterpret_cast<WPARAM>(font),TRUE);State();ShowWindow(mainWindow,SW_SHOW);SetForegroundWindow(mainWindow);
        MSG message{};while(GetMessageW(&message,nullptr,0,0)>0){if(!IsDialogMessageW(mainWindow,&message)){TranslateMessage(&message);DispatchMessageW(&message);}}
        queue.ShutdownQueueAsync();if(font)DeleteObject(font);return result;
    }catch(...){return 3;}
}
