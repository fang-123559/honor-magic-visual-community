#pragma once
#include <cfgmgr32.h>
#include <devpropdef.h>
#include <cwctype>
#pragma comment(lib,"cfgmgr32.lib")
namespace magic::system {
// The property key is supplied by the Windows Bluetooth driver. Its presence,
// type and range are checked every read; absence does not create a zero value.
inline constexpr DEVPROPKEY BluetoothBatteryKey{{0x104ea319,0x6ee2,0x4701,{0xbd,0x47,0x8d,0xdb,0xf4,0x25,0xbb,0xe5}},2};
inline std::wstring BluetoothMac(std::wstring value,bool address=false){
    std::transform(value.begin(),value.end(),value.begin(),[](wchar_t c){return static_cast<wchar_t>(towupper(c));});
    if(!address){size_t at=value.find(L"DEV_");size_t skip=4;if(at==value.npos){at=value.find(L"BLUETOOTHDEVICE_");skip=16;}if(at==value.npos)return L"";value=value.substr(at+skip,12);}
    std::wstring result;for(wchar_t c:value){if(c==L':'||c==L'-'||c==L' ')continue;if(!iswxdigit(c))return L"";result+=c;}return result.size()==12?result:L"";
}
struct BluetoothBatteryValues{bool hfp=false;std::set<int> preferred,fallback;int Value()const{auto const &values=hfp?preferred:fallback;return values.size()==1?*values.begin():-1;}};
inline std::map<std::wstring,BluetoothBatteryValues> BluetoothBatteries(ULONGLONG deadline){
    std::map<std::wstring,BluetoothBatteryValues> result;ULONG count=0;
    constexpr ULONG flags=CM_GETIDLIST_FILTER_PRESENT;
    if(CM_Get_Device_ID_List_SizeW(&count,nullptr,flags)!=CR_SUCCESS||count<2||count>262144)return result;
    std::vector<wchar_t> values(count,0);if(CM_Get_Device_ID_ListW(nullptr,values.data(),count,flags)!=CR_SUCCESS)return result;
    unsigned visited=0;for(size_t offset=0;offset<values.size()&&values[offset]&&visited++<4096&&GetTickCount64()<deadline;){
        size_t end=offset;while(end<values.size()&&values[end])++end;if(end==values.size())break;std::wstring id(values.data()+offset,end-offset);offset=end+1;
        std::wstring upper=id;std::transform(upper.begin(),upper.end(),upper.begin(),[](wchar_t c){return static_cast<wchar_t>(towupper(c));});
        bool hfp=upper.starts_with(L"BTHHFENUM\\");if(!hfp&&!upper.starts_with(L"BTHENUM\\")&&!upper.starts_with(L"BTHLE\\")&&!upper.starts_with(L"BTHLEDEVICE\\"))continue;
        DEVINST node=0;if(CM_Locate_DevNodeW(&node,id.data(),CM_LOCATE_DEVNODE_NORMAL)!=CR_SUCCESS)continue;
        auto address=BluetoothMac(upper);DEVINST ancestor=node;
        for(int depth=0;address.empty()&&depth<6;depth++){DEVINST parent=0;if(CM_Get_Parent(&parent,ancestor,0)!=CR_SUCCESS)break;wchar_t parentId[MAX_DEVICE_ID_LEN]{};if(CM_Get_Device_IDW(parent,parentId,MAX_DEVICE_ID_LEN,0)!=CR_SUCCESS)break;address=BluetoothMac(parentId);ancestor=parent;}
        if(address.empty())continue;auto &entry=result[address];entry.hfp|=hfp;
        DEVPROPTYPE type=DEVPROP_TYPE_EMPTY;ULONG bytes=sizeof(ULONG);ULONG battery=0;
        if(CM_Get_DevNode_PropertyW(node,&BluetoothBatteryKey,&type,reinterpret_cast<PBYTE>(&battery),&bytes,0)!=CR_SUCCESS)continue;
        if(!((type==DEVPROP_TYPE_BYTE&&bytes==1)||(type==DEVPROP_TYPE_UINT32&&bytes==4))||battery>100)continue;
        (hfp?entry.preferred:entry.fallback).insert(static_cast<int>(battery));
    }return result;
}
}
