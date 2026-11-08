#pragma once
// In-place editor uses the desktop's retained surface. No WebView, second
// window, new full-screen texture, polling or continuously running animation.
inline void Window::GridRequest(std::wstring const &action)
{
    if(gridBusy){if(action==L"cancel")gridCancelPending=true;return;}
    JsonObject request;Put(request,L"op",std::wstring(L"grid-settings"));Put(request,L"board",boardId);Put(request,L"action",action);
    if(gridEdit){
        Put(request,L"editId",Str(gridEdit,L"editId"));
        for(auto key:{L"columns",L"rows",L"columnSide",L"rowSide"})if(auto v=gridEdit.TryLookup(key))request.Insert(key,v);
    }
    gridBusy=true;auto state=providerState;
    app.Request(request,[state,action](JsonObject const &reply){
        auto w=Live(state);if(!w)return;w->gridBusy=false;
        if(!Bool(reply,L"ok")){
            w->gridError=Str(reply,L"error");
            if(action==L"begin"||action==L"cancel"){
                w->gridOpening=false;w->gridEdit=nullptr;w->gridPreview.reset();w->gridControls.clear();w->gridEnabled.clear();w->gridButtons.clear();w->gridCancelPending=false;
                w->focusKey=w->gridPreviousFocus;w->gridPreviousFocus.clear();w->gridBarHidden=false;
            }else if(w->gridEdit)w->gridEdit.Insert(L"canCommit",JsonValue::CreateBooleanValue(false));
            if(w->gridCancelPending){w->gridCancelPending=false;w->GridRequest(L"cancel");return;}
            w->desktopStamp.valid=false;w->Draw();return;
        }
        auto result=reply.GetNamedObject(L"result");
        if(Bool(result,L"closed")){
            w->AnimateGrid(true);w->gridEdit=nullptr;w->gridOpening=false;w->gridPreview.reset();w->gridControls.clear();w->gridEnabled.clear();w->gridButtons.clear();w->gridError.clear();w->gridCancelPending=false;
            w->focusKey=w->gridPreviousFocus;w->gridPreviousFocus.clear();w->gridBarHidden=false;
            w->desktopStamp.valid=false;w->Apply();w->NotifyFocus();return;
        }
        w->gridOpening=false;w->gridEdit=result;w->gridError.clear();
        if(auto board=w->app.model.Find(w->boardId)){
            w->gridPreview=*board;auto &b=*w->gridPreview;
            auto projection=result.GetNamedObject(L"projection");
            b.originX=static_cast<float>(Num(projection,L"originX"));b.originY=static_cast<float>(Num(projection,L"originY"));
            b.stepX=static_cast<float>(Num(projection,L"stepX",96));b.stepY=b.stepX;
            b.gridColumns=static_cast<int>(Num(result,L"columns"));b.gridRows=static_cast<int>(Num(result,L"rows"));b.positions.clear();
            for(auto const &v:result.GetNamedArray(L"placements")){auto p=v.GetObject();b.positions[Str(p,L"key")]={static_cast<int>(Num(p,L"x")),static_cast<int>(Num(p,L"y")),static_cast<int>(Num(p,L"w")),static_cast<int>(Num(p,L"h"))};}
        }
        if(w->gridCancelPending){w->gridCancelPending=false;w->GridRequest(L"cancel");return;}
        w->desktopStamp.valid=false;w->Draw();
        if(action==L"begin"){w->focusKey=L"grid:col-plus";w->NotifyFocus();}
        if(UiaClientsAreListening()){auto provider=MakeProvider(w->providerState);UiaRaiseStructureChangedEvent(provider,StructureChangeType_ChildrenInvalidated,nullptr,0);provider->Release();}
    });
}
inline void Window::BeginGridEdit()
{
    if(GridEditing()||!folderId.empty())return;
    EndRename();ResetPageMotion();ResetFolderAvoidance();marquee=false;MarqueePreview();pressed=false;KillTimer(hwnd,98);
    if(GetCapture()==hwnd)ReleaseCapture();if(interactionHover){interactionHover->Release(root);interactionHover.reset();}
    gridWorkspace=app.model.workspaceVersion;gridPreviousFocus=focusKey;gridOpening=true;gridBarHidden=false;gridError.clear();SetFocus(hwnd);GridRequest(L"begin");Draw();
}
inline void Window::GridAction(std::wstring const &key)
{
    if(!gridEdit||gridBusy)return;
    if(key==L"grid:done"){if(Bool(gridEdit,L"canCommit"))GridRequest(L"commit");return;}
    if(key==L"grid:cancel"){GridRequest(L"cancel");return;}
    if(gridBarCollapsing)return;
    if(key==L"grid:toggle"){
        if(!gridBarHidden&&gridLayer&&hwnd&&NativeMotion(app.model)){
            gridBarCollapsing=true;NativeContextMotion::Exit(app.graphics.compositor,gridLayer,false);
            if(!SetTimer(hwnd,109,NativeContextMotion::CloseMs+16,nullptr))FinishGridMotion();return;
        }
        gridBarHidden=!gridBarHidden;focusKey=gridBarHidden?L"grid:toggle":L"grid:col-plus";desktopStamp.valid=false;Draw();if(!gridBarHidden)AnimateGrid(false);return;
    }
    int cols=static_cast<int>(Num(gridEdit,L"columns")),rows=static_cast<int>(Num(gridEdit,L"rows"));
    if(key==L"grid:col-minus")cols--;else if(key==L"grid:col-plus")cols++;
    else if(key==L"grid:row-minus")rows--;else if(key==L"grid:row-plus")rows++;
    else if(key==L"grid:reset"){cols=14;rows=9;Put(gridEdit,L"columnSide",std::wstring(L"split"));Put(gridEdit,L"rowSide",std::wstring(L"split"));}
    else if(key==L"grid:fill")cols=static_cast<int>(Num(gridEdit,L"fillColumns",cols));
    else if(key.rfind(L"grid:col-side:",0)==0)Put(gridEdit,L"columnSide",key.substr(14));
    else if(key.rfind(L"grid:row-side:",0)==0)Put(gridEdit,L"rowSide",key.substr(14));
    else return;
    // Existing saved counts may exceed the prototype limits. They remain
    // visible and may be reduced; increasing beyond current capacity is denied.
    if(cols<1||rows<1)return;
    if(key!=L"grid:reset"){
    if(cols<Num(gridEdit,L"minColumns",1)&&cols<Num(gridEdit,L"columns"))return;
    if(rows<Num(gridEdit,L"minRows",1)&&rows<Num(gridEdit,L"rows"))return;
    if(cols>Num(gridEdit,L"maxColumns",24)&&cols>Num(gridEdit,L"columns"))return;
    if(rows>Num(gridEdit,L"maxRows",16)&&rows>Num(gridEdit,L"rows"))return;
    }
    Put(gridEdit,L"columns",cols);Put(gridEdit,L"rows",rows);GridRequest(L"update");
}
inline void Window::GridKey(UINT key)
{
    if(key==VK_ESCAPE){GridRequest(L"cancel");return;}
    if(key==VK_RETURN){GridAction(L"grid:done");return;}
    if(key==VK_LEFT||key==VK_RIGHT){GridAction(key==VK_LEFT?L"grid:col-minus":L"grid:col-plus");return;}
    if(key==VK_UP||key==VK_DOWN){GridAction(key==VK_UP?L"grid:row-minus":L"grid:row-plus");return;}
    if(key==VK_SPACE){GridAction(focusKey);return;}
    if(key==VK_TAB&&!gridControls.empty()){
        auto it=std::find_if(gridControls.begin(),gridControls.end(),[&](auto const &t){return t.key==focusKey;});
        int at=it==gridControls.end()?0:static_cast<int>(it-gridControls.begin())+((GetKeyState(VK_SHIFT)&0x8000)?-1:1);
        int direction=(GetKeyState(VK_SHIFT)&0x8000)?-1:1;
        at=(at+static_cast<int>(gridControls.size()))%static_cast<int>(gridControls.size());
        for(size_t n=0;n<gridControls.size()&&!gridEnabled.contains(gridControls[at].key);n++)at=(at+direction+static_cast<int>(gridControls.size()))%static_cast<int>(gridControls.size());
        focusKey=gridControls[at].key;desktopStamp.valid=false;Draw();NotifyFocus();
    }
}
inline bool Window::GridPointer(UINT message,float x,float y)
{
    if(!GridEditing())return false;
    if(gridBarCollapsing&&message==WM_LBUTTONDOWN)return true;
    if(message==WM_LBUTTONDOWN){
        if(gridLayer){gridLayer.StopAnimation(L"Scale");gridLayer.StopAnimation(L"Opacity");gridLayer.Scale({1,1,1});gridLayer.Opacity(1);}
        app.inputEpoch++;SetFocus(hwnd);
        for(auto const &button:gridButtons)if(Inside(button.second,x,y)){focusKey=button.first;GridAction(button.first);NotifyFocus();return true;}
        if(!gridBarHidden&&Inside(gridPanel,x,y))return true;
        if(!gridBusy)GridAction(L"grid:toggle");return true;
    }
    return message==WM_LBUTTONUP||message==WM_LBUTTONDBLCLK||message==WM_MOUSEMOVE||message==WM_RBUTTONDOWN||message==WM_RBUTTONUP||message==WM_CONTEXTMENU||message==WM_MOUSEWHEEL||message==WM_MBUTTONDOWN;
}
inline void Window::PaintGridLines(ID2D1DeviceContext *c)
{
    if(!gridPreview)return;
    auto const &b=*gridPreview;NativePaint p(c,app.graphics,Preview70Theme::systemDark);
    bool overflow=Num(gridEdit,L"overflow")>0;
    auto color=D2D1::ColorF(p.theme.dark?0xffffff:0x000000,.90f),halo=D2D1::ColorF(p.theme.dark?0x000000:0xffffff,.76f);
    float right=b.originX+b.gridColumns*b.stepX;
    com_ptr<ID2D1Factory> factory;c->GetFactory(factory.put());
    auto stroke=[&](float weight){com_ptr<ID2D1StrokeStyle> result;auto style=D2D1::StrokeStyleProperties();style.dashStyle=D2D1_DASH_STYLE_CUSTOM;float pattern[]={5/weight,4/weight};check_hresult(factory->CreateStrokeStyle(style,pattern,2,result.put()));return result;};
    // Both strokes share the same 5/4 DIP dash phase, even with different widths.
    auto dashed=stroke(1.6f),outline=stroke(3.5f);
    for(bool back:{true,false}){auto brush=p.Color(back?halo:color);float weight=back?3.5f:1.6f;auto dash=back?outline.get():dashed.get();
        for(int x=0;x<=b.gridColumns;x++)c->DrawLine({b.originX+x*b.stepX,b.originY},{b.originX+x*b.stepX,b.originY+b.gridRows*b.stepY},brush,weight,dash);
        for(int y=0;y<=b.gridRows;y++)c->DrawLine({b.originX,b.originY+y*b.stepY},{right,b.originY+y*b.stepY},brush,weight,dash);
        for(auto const &[key,_]:b.positions)c->DrawRectangle(RootBounds(key),back?brush:p.Color(overflow?p.theme.Danger():color),weight,dash);
    }
    // Bands represent the true workspace margins; their preview width is 90%.
    float leftEdge=b.originX-static_cast<float>(Num(gridEdit,L"paddingX")),rightEdge=leftEdge+static_cast<float>(Num(gridEdit,L"workspaceWidth"));
    p.Fill({leftEdge,0,b.originX,height},D2D1::ColorF(0x3987db,.10f),0);
    p.Fill({right,0,rightEdge,height},D2D1::ColorF(0x3987db,.10f),0);
    auto stripe=p.Color(D2D1::ColorF(0x3987db,.22f));
    c->PushAxisAlignedClip({leftEdge,0,b.originX,height},D2D1_ANTIALIAS_MODE_ALIASED);
    for(float y=-width;y<height+width;y+=12)c->DrawLine({leftEdge,y},{b.originX,y+b.originX-leftEdge},stripe,1);
    c->PopAxisAlignedClip();c->PushAxisAlignedClip({right,0,rightEdge,height},D2D1_ANTIALIAS_MODE_ALIASED);
    for(float y=-width;y<height+width;y+=12)c->DrawLine({right,y},{rightEdge,y+rightEdge-right},stripe,1);
    c->PopAxisAlignedClip();
}
inline D2D1_RECT_F Window::GridPanelBounds()const {
    if(gridBarHidden){float w=std::min(420.f,width-24);return {(width-w)/2,height-52,(width+w)/2,height-12};}
    float w=std::min(620.f,width-24),h=w<580?410.f:278.f;
    if(!gridError.empty()||gridEdit&&!Str(gridEdit,L"warning").empty())h+=22;
    h=std::min(h,height-24);return {(width-w)/2,height-h-12,(width+w)/2,height-12};
}
inline void Window::PaintGridEditor(ID2D1DeviceContext *c)
{
    if(!GridEditing())return;
    NativePaint p(c,app.graphics,Preview70Theme::systemDark);gridButtons.clear();gridControls.clear();gridEnabled.clear();
    gridPanel=GridPanelBounds();float left=gridPanel.left,top=gridPanel.top,w=gridPanel.right-left,h=gridPanel.bottom-top;bool narrow=w<580;
    bool glass=!NativeLowPower(app.model)&&(gridBackdrop||materialPreview);
    p.Material(gridPanel,NativeMaterial::Panel,glass,16);
    if(!gridEdit){p.Text(gridError.empty()?L"正在读取桌面图标设置…":gridError,{left+16,top+8,left+w-16,top+40},13,p.theme.Ink());return;}
    auto number=[](double n){wchar_t text[40];swprintf_s(text,L"%.1f",n);return std::wstring(text);};
    auto integer=[&](wchar_t const *key){return std::to_wstring(static_cast<int>(Num(gridEdit,key)));};
    auto button=[&](std::wstring id,std::wstring label,D2D1_RECT_F rect,bool enabled=true,bool chosen=false,bool clear=false){
        if(!clear)p.Fill(rect,chosen?p.theme.Accent():p.theme.Raised(),7);
        if(id==focusKey)p.Interaction(NativeRegion(rect,gridPanel,7),false,false,true);
        p.Text(label,{rect.left+3,rect.top+3,rect.right-3,rect.bottom-2},12,chosen?D2D1::ColorF(0xffffff):enabled?p.theme.Ink():p.theme.Muted(),500,DWRITE_TEXT_ALIGNMENT_CENTER);
        gridButtons.push_back({id,rect});Tile t;t.key=id;t.name=label;gridControls.push_back(t);if(enabled&&!gridBusy)gridEnabled.insert(id);
    };
    if(gridBarHidden){button(L"grid:toggle",L"桌面图标设置 · 点击显示编辑条 · Esc 取消",gridPanel,true,false,true);return;}
    p.Text(L"桌面图标设置",{left+16,top+12,left+w-180,top+38},17,p.theme.Ink(),600);
    button(L"grid:cancel",L"取消",{left+w-150,top+10,left+w-84,top+36},true,false,true);
    button(L"grid:done",L"完成",{left+w-76,top+10,left+w-16,top+36},Bool(gridEdit,L"canCommit"),true);
    auto stepper=[&](bool column,float y){
        float x=left+48,control=140;auto key=column?L"columns":L"rows";
        p.Text(column?L"列数":L"行数",{left+16,y,left+44,y+27},13,p.theme.Muted(),500);
        p.Fill({x,y,x+control,y+28},p.theme.Raised(),7);
        button(column?L"grid:col-minus":L"grid:row-minus",L"−",{x,y,x+36,y+28},Num(gridEdit,key)>Num(gridEdit,column?L"minColumns":L"minRows",1),false,true);
        p.Text(integer(key),{x+36,y,x+control-36,y+28},15,p.theme.Ink(),600,DWRITE_TEXT_ALIGNMENT_CENTER);
        button(column?L"grid:col-plus":L"grid:row-plus",L"+",{x+control-36,y,x+control,y+28},Num(gridEdit,key)<Num(gridEdit,column?L"maxColumns":L"maxRows"),false,true);
        auto side=Str(gridEdit,column?L"columnSide":L"rowSide");
        std::vector<std::pair<std::wstring,std::wstring>> sides=column?std::vector<std::pair<std::wstring,std::wstring>>{{L"split",L"两侧"},{L"left",L"左侧"},{L"right",L"右侧"}}:std::vector<std::pair<std::wstring,std::wstring>>{{L"split",L"两端"},{L"top",L"顶部"},{L"bottom",L"底部"}};
        float sx=x;for(auto const &[value,label]:sides){button(std::wstring(column?L"grid:col-side:":L"grid:row-side:")+value,label,{sx,y+34,sx+control/3,y+60},true,side==value);sx+=control/3;}
    };
    stepper(true,top+52);stepper(false,top+126);
    float statsX=narrow?left+16:left+270,statsY=narrow?top+226:top+52,dy=narrow?17.f:22.f,valueX=statsX+64;
    if(!narrow)p.Line(left+248,top+48,left+248,top+h-40,p.theme.Gray(900,.12f));
    auto stat=[&](std::wstring label,std::wstring value,bool strong=false){p.Text(label,{statsX,statsY,valueX-4,statsY+dy},11.5f,p.theme.Muted());p.Text(value,{valueX,statsY,left+w-16,statsY+dy},11.5f,p.theme.Ink(),strong?600:400);statsY+=dy;};
    stat(L"网格",integer(L"columns")+L" × "+integer(L"rows"),true);
    stat(L"格子",number(Num(gridEdit,L"cell"))+L" DIP",true);
    stat(L"图标",number(Num(gridEdit,L"icon"))+L" DIP · 字号 "+number(Num(gridEdit,L"font")));
    stat(L"可用区",integer(L"workspaceWidth")+L" × "+integer(L"workspaceHeight")+L" · 缩放 "+std::to_wstring(static_cast<int>(Num(gridEdit,L"scale")*100))+L"%（只读）");
    stat(L"真实边距",number(Num(gridEdit,L"paddingX"))+L" / "+number(Num(gridEdit,L"paddingY")));
    stat(L"预留边距",number(width*.05+Num(gridEdit,L"paddingX")*.9)+L" / "+number(height*.05+Num(gridEdit,L"paddingY")*.9));
    stat(L"占用",integer(L"usedColumns")+L" 列 × "+integer(L"usedRows")+L" 行 · "+integer(L"pageCount")+L" 页（共用行列）");
    stat(L"增量",L"+1 列 → "+integer(L"nextColumnCell")+L" DIP · +1 行 → "+integer(L"nextRowCell")+L" DIP");
    button(L"grid:reset",L"恢复 14 × 9",{left+16,top+197,left+124,top+221},true,false,true);
    if(Num(gridEdit,L"fillColumns")>Num(gridEdit,L"columns"))button(L"grid:fill",L"加到 "+integer(L"fillColumns")+L" 列",{left+130,top+197,left+236,top+221},true,false,true);
    float footer=top+h-32;auto warning=gridError.empty()?Str(gridEdit,L"warning"):gridError;
    if(!warning.empty())p.Text(warning,{left+16,footer-22,left+w-16,footer},10.5f,D2D1::ColorF(0xe35845),400,DWRITE_TEXT_ALIGNMENT_LEADING,0,true);
    p.Line(left,footer-5,left+w,footer-5,p.theme.Gray(900,.10f));
    p.Text(L"← → 调列数 · ↑ ↓ 调行数 · Enter 完成 · Esc 取消 · 点击空白收起编辑条",{left+16,footer,left+w-16,top+h-4},10.5f,p.theme.Muted(),400,DWRITE_TEXT_ALIGNMENT_LEADING,0,true);
}
