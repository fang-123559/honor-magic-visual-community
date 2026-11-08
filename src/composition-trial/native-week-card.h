#pragma once
inline std::vector<std::wstring> NativeWeekDates(JsonObject const &data){
    std::vector<std::wstring> result;for(auto const &v:data.GetNamedArray(L"weekDays",JsonArray{})){auto date=Str(v.GetObject(),L"date");if(date.size()==10)result.push_back(date);}
    if(result.size()==7)return result;result.clear();SYSTEMTIME now{};GetLocalTime(&now);using namespace std::chrono;auto today=sys_days{year{now.wYear}/month{now.wMonth}/day{now.wDay}},first=today-days{weekday{today}.c_encoding()};
    for(int i=0;i<7;i++){auto date=year_month_day{first+days{i}};wchar_t value[16]{};swprintf_s(value,L"%04d-%02u-%02u",static_cast<int>(date.year()),static_cast<unsigned>(date.month()),static_cast<unsigned>(date.day()));result.emplace_back(value);}return result;
}
inline std::wstring NativeWeekSelected(Window const &window,Tile const &tile){
    auto dates=NativeWeekDates(NativeTodoData(window,tile));auto found=window.todoSelectedDays.find(tile.key);auto selected=found==window.todoSelectedDays.end()?NativeTodoToday():found->second;
    return std::find(dates.begin(),dates.end(),selected)!=dates.end()?selected:NativeTodoToday();
}
inline D2D1_RECT_F NativeWeekBody(float width,float height){return {12,height>220?105.f:85.f,width-12,height-10};}
inline std::pair<std::wstring,int> NativeWeekHit(JsonObject const &data,float x,float y,float width,float height,float offset,std::wstring const &date){
    if(x<0||y<0||x>width||y>height)return {};bool large=height>220;
    if(y>=8&&y<=(large?72:54))return {L"todo-select-day",std::clamp(static_cast<int>((x-12)/((width-24)/7)),0,6)};
    float header=large?75.f:56.f;if(x>=width-43&&y>=header&&y<header+28)return {L"todo-add",-1};
    return NativeTodoRowHit(data,x,y,NativeWeekBody(width,height),31,offset,date);
}
inline void PaintNativeWeek(Window &window,NativePaint &p,Tile const &tile,float width,float height){
    auto data=NativeTodoData(window,tile);auto dates=NativeWeekDates(data);auto selected=NativeWeekSelected(window,tile),today=NativeTodoToday();bool large=height>220;
    float cell=(width-24)/7;auto orange=D2D1::ColorF(p.theme.dark?0xf0a46b:0xb96c36);auto values=NativeTodoItems(data);
    auto lunar=data.GetNamedArray(L"weekDays",JsonArray{});
    for(int i=0;i<7;i++){float x=12+i*cell,cx=x+cell/2,cy=large?38.f:32.f;bool isToday=dates[i]==today,isSelected=dates[i]==selected;
        p.Text(std::wstring(1,L"日一二三四五六"[i]),{x,8,x+cell,22},9,isToday?orange:p.theme.MaterialMuted(),400,DWRITE_TEXT_ALIGNMENT_CENTER);
        if(isToday)p.c->FillEllipse({{cx,cy},large?14.f:12.f,large?14.f:12.f},p.Color(orange));else if(isSelected){p.c->FillEllipse({{cx,cy},large?14.f:12.f,large?14.f:12.f},p.Color(p.theme.SectionFilm()));p.c->DrawEllipse({{cx,cy},large?14.f:12.f,large?14.f:12.f},p.Color(p.theme.MaterialMuted()),1);}
        p.Text(std::to_wstring(_wtoi(dates[i].substr(8).c_str())),{x,cy-12,x+cell,cy+12},14,isToday?D2D1::ColorF(0xffffff):p.theme.Ink(),isToday?700:500,DWRITE_TEXT_ALIGNMENT_CENTER,1);
        if(large&&i<static_cast<int>(lunar.Size()))p.Text(Str(lunar.GetAt(i).GetObject(),L"lunar"),{x,54,x+cell,70},8.5f,p.theme.MaterialMuted(),400,DWRITE_TEXT_ALIGNMENT_CENTER);
        bool dot=std::any_of(values.begin(),values.end(),[&](auto const &item){return !item.done&&item.date==dates[i];});if(dot)p.c->FillEllipse({{cx,large?72.f:50.f},1.6f,1.6f},p.Color(orange));
    }
    float y=large?77.f:56.f;p.Line(12,y-2,width-12,y-2,p.theme.Edge(),.6f);
    auto at=std::find(dates.begin(),dates.end(),selected);int day=at==dates.end()?0:static_cast<int>(std::distance(dates.begin(),at));
    auto count=NativeTodoVisible(data,selected).size();auto title=L"周"+std::wstring(1,L"日一二三四五六"[day])+L" · "+std::to_wstring(_wtoi(selected.substr(5,2).c_str()))+L"月"+std::to_wstring(_wtoi(selected.substr(8,2).c_str()))+L"日  "+std::to_wstring(count)+L" 项";
    p.Text(title,{12,y,width-46,y+26},11,p.theme.Ink(),500);p.c->FillEllipse({{width-26,y+12},12,12},p.Color(orange));p.Icon(L"add",{width-33,y+5,width-19,y+19},D2D1::ColorF(0xffffff));
    float offset=window.todoScrolls[tile.key];auto error=Str(data,L"error");if(!error.empty())p.Text(error,NativeWeekBody(width,height),11.5f,p.theme.MaterialMuted(),400,DWRITE_TEXT_ALIGNMENT_CENTER,0,true);
    else if(!count)p.Text(L"今天还没有待办，点击添加",NativeWeekBody(width,height),12,p.theme.MaterialMuted(),400,DWRITE_TEXT_ALIGNMENT_CENTER);
    else PaintNativeTodoRows(p,data,NativeWeekBody(width,height),31,offset,selected);
}
