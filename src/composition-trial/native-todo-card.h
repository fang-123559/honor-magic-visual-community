#pragma once
// One shared data projection; selection/scroll are view state, never copied tasks.
struct NativeTodoItem{int index;std::wstring id,text;double order;bool done;std::wstring date,priority;};
inline std::vector<NativeTodoItem> NativeTodoItems(JsonObject const &settings){
    std::vector<NativeTodoItem> result;std::set<std::wstring> ids;auto values=settings.GetNamedArray(L"items",JsonArray{});int index=0;
    for(auto const &value:values){if(index>=512)break;int at=index++;if(value.ValueType()!=JsonValueType::Object)continue;auto row=value.GetObject();auto id=Str(row,L"id");if(id.empty()||id.size()>64||ids.contains(id))continue;ids.insert(id);result.push_back({at,id,Str(row,L"text"),Num(row,L"order",at+1),Bool(row,L"done"),Str(row,L"date"),Str(row,L"priority")});}
    std::stable_sort(result.begin(),result.end(),[](auto const&a,auto const&b){return a.order<b.order;});return result;
}
inline JsonObject NativeTodoData(Window const &window,Tile const &tile){return window.app.model.todoData?window.app.model.todoData:tile.card.GetNamedObject(L"settings",JsonObject{});}
inline std::wstring NativeTodoToday(){SYSTEMTIME now{};GetLocalTime(&now);wchar_t date[16]{};swprintf_s(date,L"%04d-%02d-%02d",now.wYear,now.wMonth,now.wDay);return date;}
inline std::vector<NativeTodoItem> NativeTodoVisible(JsonObject const &settings,std::wstring const &date=L""){
    auto values=NativeTodoItems(settings);std::erase_if(values,[&](auto const &item){return item.done||(!date.empty()&&!item.date.empty()&&item.date!=date);});return values;
}
inline float NativeTodoScrollMax(JsonObject const &settings,D2D1_RECT_F body,float step,std::wstring const &date=L""){return std::max(0.f,NativeTodoVisible(settings,date).size()*step-(body.bottom-body.top));}
inline void PaintNativeTodoRows(NativePaint &p,JsonObject const &settings,D2D1_RECT_F body,float step,float offset,std::wstring const &date=L""){
    auto values=NativeTodoVisible(settings,date);float maximum=NativeTodoScrollMax(settings,body,step,date);offset=std::clamp(offset,0.f,maximum);
    bool fade=maximum-offset>2;com_ptr<ID2D1LinearGradientBrush> opacity;
    p.c->PushAxisAlignedClip(body,D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
    if(fade){D2D1_GRADIENT_STOP stops[]{{0,D2D1::ColorF(0xffffff,1)},{1,D2D1::ColorF(0xffffff,0)}};com_ptr<ID2D1GradientStopCollection> collection;check_hresult(p.c->CreateGradientStopCollection(stops,2,collection.put()));check_hresult(p.c->CreateLinearGradientBrush(D2D1::LinearGradientBrushProperties({0,body.bottom-24},{0,body.bottom}),collection.get(),opacity.put()));p.c->PushLayer(D2D1::LayerParameters(body,nullptr,D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,D2D1::Matrix3x2F::Identity(),1,opacity.get()),nullptr);}
    for(size_t i=0;i<values.size();i++){float y=body.top+i*step-offset;if(y+step<body.top||y>body.bottom)continue;auto const &item=values[i];
        auto color=D2D1::ColorF(item.priority==L"urgent"?(p.theme.dark?0xe05c55:0xc0453c):item.priority==L"relaxed"?(p.theme.dark?0x5c8be0:0x3a6bbf):(p.theme.dark?0x57b97a:0x3f8f5e));
        p.Fill({body.left,y,body.right,y+step-2},p.theme.SectionFilm(),7);p.Fill({body.left+6,y+6,body.left+9,y+step-8},color,2);
        p.Text(item.text,{body.left+16,y,body.right-91,y+step-2},12.5f,p.theme.Ink(),400,DWRITE_TEXT_ALIGNMENT_LEADING,0,true);
        auto tag=item.date.empty()?L"每天":item.date.substr(5,2)+L"/"+item.date.substr(8,2);p.Fill({body.right-82,y+7,body.right-36,y+step-9},p.theme.Film(),5);p.Text(tag,{body.right-82,y+4,body.right-36,y+step-6},10,p.theme.MaterialMuted(),400,DWRITE_TEXT_ALIGNMENT_CENTER);
        p.c->DrawEllipse({{body.right-18,y+(step-2)/2},8.5f,8.5f},p.Color(p.theme.MaterialMuted()),1.5f);
    }
    if(fade)p.c->PopLayer();p.c->PopAxisAlignedClip();
}
inline std::pair<std::wstring,int> NativeTodoRowHit(JsonObject const &settings,float x,float y,D2D1_RECT_F body,float step,float offset,std::wstring const &date=L""){
    if(!Inside(body,x,y))return {L"todo-edit",-1};auto values=NativeTodoVisible(settings,date);offset=std::clamp(offset,0.f,NativeTodoScrollMax(settings,body,step,date));int at=static_cast<int>((y-body.top+offset)/step);
    if(at<0||at>=static_cast<int>(values.size()))return {L"todo-edit",-1};return {x>=body.right-32?L"todo-toggle":L"todo-edit",values[at].index};
}
inline std::pair<std::wstring,int> NativeTodoHit(JsonObject const &settings,float x,float y,float width=376,float height=184,float offset=0){
    if(x<0||y<0||x>width||y>height)return {};
    if(x>=width-46&&y>=12&&y<=42)return {L"todo-add",-1};
    return NativeTodoRowHit(settings,x,y,{14,52,width-14,height-16},32,offset);
}
inline void PaintNativeTodo(NativePaint &p,JsonObject const &settings,float width,float height,float offset=0){
    auto values=NativeTodoVisible(settings);auto orange=D2D1::ColorF(p.theme.dark?0xf0a46b:0xb96c36);auto ink=p.theme.Ink(),muted=p.theme.MaterialMuted();
    p.Text(std::to_wstring(values.size()),{14,9,66,44},30,values.empty()?muted:ink,800);p.Text(L"待办",{70,18,width-54,42},13,orange,600);
    p.c->FillEllipse({{width-29,27},15,15},p.Color(orange));p.Icon(L"add",{width-37,19,width-21,35},D2D1::ColorF(0xffffff));
    auto error=Str(settings,L"error");if(!error.empty())p.Text(error,{14,55,width-14,height-14},12,muted,400,DWRITE_TEXT_ALIGNMENT_CENTER,0,true);
    else if(values.empty())p.Text(L"还没有待办，点击添加",{14,58,width-14,height-14},12.5f,muted,400,DWRITE_TEXT_ALIGNMENT_CENTER);
    else PaintNativeTodoRows(p,settings,{14,52,width-14,height-16},32,offset);
}
