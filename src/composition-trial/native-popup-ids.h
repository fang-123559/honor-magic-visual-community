#pragma once
#include <string>
// Exact built-in aliases only. Keep in sync with the migration ledger and C#.
struct NativePopupAlias { wchar_t const *legacy; wchar_t const *canonical; };
inline constexpr NativePopupAlias NativePopupAliases[]{
    {L"@seelen/calendar-popup",L"@magic/calendar-popup"},
    {L"@seelen/keyboard-selector",L"@magic/keyboard-selector"},
    {L"@seelen/user-menu",L"@magic/user-menu"},
    {L"@seelen/quick-settings",L"@magic/quick-settings"},
    {L"@seelen/media-popup",L"@magic/media-popup"},
    {L"@seelen/network-popup",L"@magic/network-popup"},
    {L"@seelen/bluetooth-popup",L"@magic/bluetooth-popup"},
    {L"@seelen/notifications",L"@magic/notifications"},
    {L"@seelen/system-tray",L"@magic/system-tray"},
    {L"@seelen/apps-menu",L"@magic/apps-menu"},
    {L"@seelen/task-switcher",L"@magic/task-switcher"},
    {L"@seelen/power-menu",L"@magic/power-menu"},
    {L"@seelen/flyouts",L"@magic/flyouts"},
};
inline std::wstring CanonicalNativePopupId(std::wstring const &id){
    if(id.rfind(L"@seelen/",0)!=0)return id;
    for(auto const &alias:NativePopupAliases)if(id==alias.legacy)return alias.canonical;
    return id;
}

// Old built-in drawing keys are accepted only at this compatibility boundary.
inline std::wstring CanonicalNativeBarPlugin(std::wstring const &id){
    if(id==L"@seelen/dock-system-tray")return L"@magic/dock-system-tray";
    if(id==L"@seelen/dock-calendar-popup")return L"@magic/dock-calendar-popup";
    if(id==L"@seelen/dock-media-popup")return L"@magic/dock-media-popup";
    if(id==L"@seelen/dock-bluetooth-popup")return L"@magic/dock-bluetooth-popup";
    if(id==L"@seelen/dock-keyboard-selector")return L"@magic/dock-keyboard-selector";
    if(id==L"@seelen/dock-notifications")return L"@magic/dock-notifications";
    if(id==L"@seelen/dock-network-popup")return L"@magic/dock-network-popup";
    return id;
}
