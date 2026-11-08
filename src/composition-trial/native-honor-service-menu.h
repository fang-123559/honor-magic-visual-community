#pragma once
inline bool Window::HonorContext(POINT point){
    POINT client=point;ScreenToClient(hwnd,&client);auto tile=Find(Hit(client.x*96/dpi,client.y*96/dpi));if(!tile||!tile->card||Str(tile->card,L"type")!=L"system.honor-service")return false;
    auto rect=RootBounds(tile->key);float width=static_cast<float>(Num(tile->card,L"columns",4))*96-8,scale=(rect.right-rect.left)/width;int index=NativeServiceHit(*this,*tile,(client.x*96/dpi-rect.left)/scale,(client.y*96/dpi-rect.top)/scale,width);if(index<0)return false;
    auto state=providerState;auto key=tile->key;JsonArray entries;auto add=[&](wchar_t const *id,std::wstring const &label,bool enabled){JsonObject row;Put(row,L"id",std::wstring(id));Put(row,L"label",label);row.Insert(L"enabled",JsonValue::CreateBooleanValue(enabled));entries.Append(row);};
    std::wstring title;for(auto const &row:NativeServiceRows(*this,*tile))if(static_cast<int>(Num(row,L"index"))==index)title=Str(row,L"name");
    add(L"title",title,false);add(L"remove",L"从卡片删除",true);add(L"cancel",L"取消",true);auto &menus=Menus(app);menus.Begin(point,L"toolbar",[state,key,index](size_t,std::wstring const &id){if(auto window=Live(state)){Menus(window->app).Close(false);if(id==L"remove")window->CardAction(key,L"honor-hide",index);}},[](){});menus.Show(0,entries);return true;
}
