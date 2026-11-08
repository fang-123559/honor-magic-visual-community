#pragma once
#include <windows.h>
#include <aclapi.h>
#include <sddl.h>
#include <shlobj.h>
#include <filesystem>
#include <string>

// An unpackaged helper has no OS package identity. Only the fixed, administrator
// owned installation can use it. Never infer trust from a request-supplied path.
namespace magic::installed {
inline std::filesystem::path Root() {
    PWSTR value=nullptr;
    if(FAILED(SHGetKnownFolderPath(FOLDERID_ProgramFiles,0,nullptr,&value)))return {};
    std::filesystem::path result=std::filesystem::path(value)/L"MagicCommunityDesktop";
    CoTaskMemFree(value);return result;
}
inline bool Privileged(PSID sid) {
    if(!sid||!IsValidSid(sid))return false;
    if(IsWellKnownSid(sid,WinLocalSystemSid)||IsWellKnownSid(sid,WinBuiltinAdministratorsSid))return true;
    PSID installer=nullptr;
    if(!ConvertStringSidToSidW(L"S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",&installer))return false;
    bool result=EqualSid(sid,installer)!=FALSE;LocalFree(installer);return result;
}
inline bool Protected(std::filesystem::path const& path) {
    DWORD attributes=GetFileAttributesW(path.c_str());
    if(attributes==INVALID_FILE_ATTRIBUTES||(attributes&FILE_ATTRIBUTE_REPARSE_POINT))return false;
    PSECURITY_DESCRIPTOR descriptor=nullptr;PSID owner=nullptr;PACL acl=nullptr;
    if(GetNamedSecurityInfoW(path.c_str(),SE_FILE_OBJECT,OWNER_SECURITY_INFORMATION|DACL_SECURITY_INFORMATION,&owner,nullptr,&acl,nullptr,&descriptor)!=ERROR_SUCCESS)return false;
    bool valid=Privileged(owner)&&acl;
    constexpr DWORD writes=FILE_WRITE_DATA|FILE_APPEND_DATA|FILE_WRITE_EA|FILE_WRITE_ATTRIBUTES|FILE_DELETE_CHILD|DELETE|WRITE_DAC|WRITE_OWNER|GENERIC_WRITE|GENERIC_ALL;
    for(DWORD i=0;valid&&i<acl->AceCount;i++) {
        void* item=nullptr;if(!GetAce(acl,i,&item)){valid=false;break;}
        auto header=static_cast<ACE_HEADER*>(item);
        if(header->AceFlags&INHERIT_ONLY_ACE)continue;
        if(header->AceType==ACCESS_ALLOWED_ACE_TYPE) {
            auto ace=static_cast<ACCESS_ALLOWED_ACE*>(item);
            if((ace->Mask&writes)&&!Privileged(&ace->SidStart))valid=false;
        } else if(header->AceType!=ACCESS_DENIED_ACE_TYPE)valid=false;
    }
    LocalFree(descriptor);return valid;
}
inline bool Tree(std::filesystem::path directory) {
    try {
        auto expected=Root();auto text=directory.native();
        if(text.rfind(L"\\\\?\\",0)==0)directory=text.substr(4);
        if(expected.empty()||_wcsicmp(directory.c_str(),expected.c_str()))return false;
        if(!Protected(expected.parent_path())||!Protected(expected))return false;
        size_t count=0;
        for(auto const& file:std::filesystem::recursive_directory_iterator(expected))
            if(++count>8192||!Protected(file.path()))return false;
        return true;
    } catch(...) {return false;}
}
}
