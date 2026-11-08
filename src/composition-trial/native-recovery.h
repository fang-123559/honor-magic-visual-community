#pragma once
// A single on-demand native surface. Reuses the desktop card/icon painters and
// App's D3D/Composition device; no browser, second renderer or hidden surface.
struct RecoveryLife { NativeRecovery *owner=nullptr; };
struct NativeRecovery {
    App &app; Window painter; HWND hwnd=nullptr; float dpi=96,width=1180,height=736;
    JsonObject data{nullptr}; Model view; Preview70Icons icons; std::set<std::wstring> pending,attempted; unsigned long long generation=0;
    std::shared_ptr<RecoveryLife> life=std::make_shared<RecoveryLife>();
    Windows::UI::Composition::Desktop::DesktopWindowTarget target{nullptr};
    ContainerVisual root{nullptr}; SpriteVisual visual{nullptr},glass{nullptr}; CompositionDrawingSurface surface{nullptr};
    NativeGlassSheet glassSheet;bool opening=false,closing=false,closeNotify=true,materialPreview=false;float frameInset=0;
    struct MovingTile{SpriteVisual visual{nullptr};CompositionDrawingSurface surface{nullptr};};
    ContainerVisual avoidanceLayer{nullptr};std::map<int,MovingTile> avoidance;size_t avoidanceBytes=0;std::vector<D2D1_RECT_F> baseRects;int previewTarget=-1;
    size_t surfaceBytes=0; std::vector<D2D1_RECT_F> rects; int columns=11,scroll=0,maxScroll=0,selected=-1,pressed=-1;
    POINT down{};bool dragging=false,menu=false,busy=false,closePending=false,closePendingNotify=true,closePendingForce=false;long long dragRevision=0;std::wstring note;
    NativeRecovery(App &a):app(a),painter(a,L""){life->owner=this;}
    ~NativeRecovery(){life->owner=nullptr;Close(false,true);}
    Board *BoardView(){return view.boards.empty()?nullptr:&view.boards[0];}
    void HoldDock(bool hold){if(app.shell)for(auto &[id,bar]:app.shell->bars)if(bar->dock){bar->menuOpen=hold;if(hold)bar->Reveal();else bar->ScheduleHide();}}
    void QueueClose(bool notify=true,bool force=false){
        if(!hwnd)return;
        bool queued=closePending;
        if(!queued){++generation;closePendingNotify=notify;closePendingForce=force;}
        else{closePendingNotify|=notify;closePendingForce|=force;}
        closePending=true;if(!queued)app.QueueRenderWork();
    }
    void FlushClose(){if(!closePending)return;auto notify=closePendingNotify,force=closePendingForce;closePending=false;Close(notify,force);}
    void Close(bool notify=true,bool force=false){
        if(!hwnd)return;if(NativeRenderScope::busy){QueueClose(notify,force);return;}if(closing&&!force)return;closePending=false;
        NativeRenderScope releasing(app.relay);
        if(!force&&!app.stopping&&root&&NativeFeedbackMotion(app.model)){
            ++generation;closing=true;closeNotify=notify;opening=false;KillTimer(hwnd,7);KillTimer(hwnd,8);if(GetCapture()==hwnd)ReleaseCapture();dragging=false;HoldDock(false);
            if(NativeLowPower(app.model))NativeInteractionMotion::Opacity(app.graphics.compositor,root,0,NativeContextMotion::CloseMs);else NativeContextMotion::Exit(app.graphics.compositor,root,false);
            if(SetTimer(hwnd,8,NativeContextMotion::CloseMs+16,nullptr))return;
        }
        JsonObject detail;detail.Insert(L"force",JsonValue::CreateBooleanValue(force));app.Telemetry(L"recovery-close-start",detail);
        closing=false;++generation;auto old=hwnd;hwnd=nullptr;if(GetCapture()==old)ReleaseCapture();busy=dragging=menu=false;dragRevision=0;KillTimer(old,7);KillTimer(old,8);opening=false;ResetAvoidance();glassSheet.Release(root);HoldDock(false);if(root)ReleaseNativeGlass(root,glass);
        if(target)target.Root(nullptr);surface=nullptr;surfaceBytes=0;root=nullptr;visual=nullptr;target=nullptr;icons.Clear();painter.icons.clear();pending.clear();attempted.clear();
        WTSUnRegisterSessionNotification(old);NativeGlassStates().erase(old);DestroyWindow(old);data=nullptr;view.boards.clear();rects.clear();app.Telemetry(L"recovery-close-complete",detail);
        if(notify&&!app.stopping){JsonObject q;Put(q,L"op",std::wstring(L"recovery-open"));Put(q,L"action",std::wstring(L"close"));app.Request(q);}
    }
    void Set(JsonObject const &value,HWND anchor=nullptr){
        if(closePending)return; // A response from before topology invalidation cannot reopen this HWND.
        if(Bool(value,L"closed")){Close();return;}if(closing)Close(false,true);bool created=!hwnd;
        auto oldBoard=data?Str(data,L"board"):L"",oldFolder=data?Str(data,L"folder"):L"";int oldOffset=data?static_cast<int>(Num(data,L"offset")):0;
        if(data&&Num(data,L"revision")!=Num(value,L"revision")){dragging=false;pressed=-1;ResetAvoidance();if(GetCapture()==hwnd)ReleaseCapture();}data=value;view.Read(value);if(!BoardView())return;painter.boardId=BoardView()->id;painter.materialPreview=true;
        if(oldBoard!=Str(data,L"board")||oldFolder!=Str(data,L"folder")||oldOffset!=static_cast<int>(Num(data,L"offset"))){dragging=false;pressed=-1;ResetAvoidance();if(GetCapture()==hwnd)ReleaseCapture();++generation;scroll=0;selected=-1;icons.Clear();pending.clear();attempted.clear();}
        if(!hwnd){
            WNDCLASSEXW wc{sizeof(wc)};wc.hInstance=GetModuleHandleW(nullptr);wc.lpfnWndProc=Proc;wc.lpszClassName=L"MagicDisplayRecovery";wc.style=CS_DBLCLKS;wc.hCursor=LoadCursor(nullptr,IDC_ARROW);RegisterClassExW(&wc);
            hwnd=CreateWindowExW(WS_EX_TOOLWINDOW|WS_EX_TOPMOST|WS_EX_NOREDIRECTIONBITMAP,wc.lpszClassName,L"副屏内容",WS_POPUP,0,0,1,1,nullptr,nullptr,wc.hInstance,this);if(!hwnd)throw_last_error();
            check_hresult(app.graphics.compositor.as<abiComposition::Desktop::ICompositorDesktopInterop>()->CreateDesktopWindowTarget(hwnd,FALSE,reinterpret_cast<abiComposition::Desktop::IDesktopWindowTarget**>(put_abi(target))));
            root=app.graphics.compositor.CreateContainerVisual();visual=app.graphics.compositor.CreateSpriteVisual();root.Children().InsertAtTop(visual);target.Root(root);
            MONITORINFO mi{sizeof(mi)};GetMonitorInfoW(MonitorFromWindow(anchor?anchor:GetForegroundWindow(),MONITOR_DEFAULTTOPRIMARY),&mi);
            SetWindowPos(hwnd,HWND_TOPMOST,mi.rcWork.left+12,mi.rcWork.top+12,1,1,SWP_NOACTIVATE);dpi=static_cast<float>(GetDpiForWindow(hwnd));if(dpi<=0)dpi=96;
            float scale=dpi/96;width=std::min(1180.f,(mi.rcWork.right-mi.rcWork.left)/scale-32);height=std::min(736.f,(mi.rcWork.bottom-mi.rcWork.top)/scale-100);
            width=std::max(240.f,width);height=std::max(220.f,height);double bytes=width*height*scale*scale*4;if(bytes>24*1024*1024)height=static_cast<float>(24*1024*1024/(width*scale*scale*4));
            int w=static_cast<int>(std::ceil(width*scale)),h=static_cast<int>(std::ceil(height*scale));
            SetWindowPos(hwnd,HWND_TOPMOST,mi.rcWork.left+(mi.rcWork.right-mi.rcWork.left-w)/2,mi.rcWork.top+std::max<LONG>(8,(mi.rcWork.bottom-mi.rcWork.top-h-72)/2),w,h,SWP_NOACTIVATE);
            // Leave enough transparent HWND room for the spring overshoot. Otherwise
            // the fixed region clips the material while the text keeps growing.
            frameInset=NativeContextMotion::Gutter(std::max(width,height));int inset=static_cast<int>(std::ceil(frameInset*scale));RECT body{};GetWindowRect(hwnd,&body);
            SetWindowPos(hwnd,HWND_TOPMOST,body.left-inset,body.top-inset,w+2*inset,h+2*inset,SWP_NOACTIVATE);SetWindowRgn(hwnd,nullptr,FALSE);root.Offset({frameInset*scale,frameInset*scale,0});
            root.Size({static_cast<float>(w),static_cast<float>(h)});visual.Size(root.Size());WTSRegisterSessionNotification(hwnd,NOTIFY_FOR_THIS_SESSION);HoldDock(true);
            if(NativeFeedbackMotion(app.model)){root.Scale(NativeLowPower(app.model)?float3{1,1,1}:float3{.78f,.78f,1});root.Opacity(0);}
            Navigate(Str(data,L"board"),Str(data,L"folder"),static_cast<int>(Num(data,L"offset"))); // Register the verified popup HWND.
        }
        Layout();Draw();if(created){ShowWindow(hwnd,SW_SHOWNORMAL);SetForegroundWindow(hwnd);BeginOpening();}RequestIcons();
    }
    void BeginOpening(){if(NativeFeedbackMotion(app.model)){opening=true;root.CenterPoint({root.Size().x/2,root.Size().y,0});root.Opacity(0);bool simple=NativeLowPower(app.model);root.Scale(simple?float3{1,1,1}:float3{.78f,.78f,1});if(simple)NativeInteractionMotion::Opacity(app.graphics.compositor,root,1,140);else NativeContextMotion::Enter(app.graphics.compositor,root,false);if(!SetTimer(hwnd,8,(simple?140:NativeContextMotion::RootMs)+16,nullptr))FinishOpening();}else FinishOpening();}
    void FinishOpening(){if(hwnd)KillTimer(hwnd,8);opening=false;if(root){root.StopAnimation(L"Scale");root.StopAnimation(L"Opacity");root.Scale({1,1,1});root.Opacity(1);}}
    void ResetAvoidance(){for(auto &[i,item]:avoidance)if(item.visual&&item.visual.Parent())item.visual.Parent().Children().Remove(item.visual);avoidance.clear();if(avoidanceLayer&&avoidanceLayer.Parent())avoidanceLayer.Parent().Children().Remove(avoidanceLayer);avoidanceLayer=nullptr;avoidanceBytes=0;baseRects.clear();previewTarget=-1;}
    std::vector<std::wstring> PreviewOrder(){
        auto b=BoardView();if(!b)return {};if(!dragging||pressed<0||pressed>=static_cast<int>(b->tiles.size())||previewTarget<0||previewTarget>=static_cast<int>(b->tiles.size())||previewTarget==pressed){std::vector<std::wstring> order;for(auto const &t:b->tiles)order.push_back(t.key);return order;}
        // The backend inserts BEFORE the target, including forward moves.
        return NativeFolderPlan::Build(b->tiles,{b->tiles[pressed].key},b->tiles[pressed].key,previewTarget-(pressed<previewTarget?1:0)).order;
    }
    void PreviewDrag(float x,float y){
        if(!dragging||!BoardView())return;if(baseRects.empty())baseRects=rects;int target=-1;
        if(y>=110&&y<height-46)for(size_t i=0;i<baseRects.size();++i)if(Inside(baseRects[i],x,y)){target=static_cast<int>(i);break;}
        if(target==previewTarget)return;auto prior=rects;previewTarget=target;Layout();
        if(NativeMotion(app.model,true)&&root){auto &g=app.graphics;float scale=dpi/96;
            if(!avoidanceLayer){avoidanceLayer=g.compositor.CreateContainerVisual();avoidanceLayer.Size(root.Size());auto clip=g.compositor.CreateInsetClip();clip.LeftInset(16*scale);clip.TopInset(110*scale);clip.RightInset(16*scale);clip.BottomInset(47*scale);avoidanceLayer.Clip(clip);root.Children().InsertAtTop(avoidanceLayer);}auto b=BoardView();auto overlay=app.model.overlay;app.model.overlay=b;
            try{for(int i=0;i<static_cast<int>(rects.size());++i){auto from=prior[i],to=rects[i];if((from.bottom<110||from.top>height-47)&&(to.bottom<110||to.top>height-47))continue;
                auto found=avoidance.find(i);if(found==avoidance.end()){
                    int w=static_cast<int>(std::ceil((from.right-from.left)*scale)),h=static_cast<int>(std::ceil((from.bottom-from.top)*scale));size_t cost=static_cast<size_t>(w)*h*4;if(avoidance.size()>=128||avoidanceBytes+cost>8*1024*1024)throw std::runtime_error("Avoidance budget");
                    MovingTile item;item.surface=g.composition.CreateDrawingSurface({static_cast<float>(w),static_cast<float>(h)},Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,Windows::Graphics::DirectX::DirectXAlphaMode::Premultiplied);
                    auto draw=item.surface.as<abiComposition::ICompositionDrawingSurfaceInterop>();com_ptr<ID2D1DeviceContext> c;POINT offset{};check_hresult(draw->BeginDraw(nullptr,__uuidof(ID2D1DeviceContext),c.put_void(),&offset));
                    try{c->SetDpi(dpi,dpi);c->SetTransform(D2D1::Matrix3x2F::Translation(offset.x/scale,offset.y/scale));c->Clear(D2D1::ColorF(0,0));painter.PaintTile(c.get(),b->tiles[i],{0,0,from.right-from.left,from.bottom-from.top},i,b->tiles[i].folder.empty()&&!b->tiles[i].card);}catch(...){draw->EndDraw();throw;}check_hresult(draw->EndDraw());
                    item.visual=g.compositor.CreateSpriteVisual();item.visual.Size({static_cast<float>(w),static_cast<float>(h)});item.visual.Offset({from.left*scale,from.top*scale,0});item.visual.Brush(g.compositor.CreateSurfaceBrush(item.surface));avoidanceLayer.Children().InsertAtTop(item.visual);found=avoidance.emplace(i,std::move(item)).first;avoidanceBytes+=cost;
                }
                auto a=g.compositor.CreateVector3KeyFrameAnimation();a.InsertExpressionKeyFrame(0,L"this.StartingValue");a.InsertKeyFrame(1,{to.left*scale,to.top*scale,0},g.compositor.CreateCubicBezierEasingFunction({.22f,1},{.28f,1}));a.Duration(std::chrono::milliseconds(260));found->second.visual.StartAnimation(L"Offset",a);
            }}catch(...){for(auto &[i,item]:avoidance)if(item.visual.Parent())item.visual.Parent().Children().Remove(item.visual);avoidance.clear();avoidanceBytes=0;}app.model.overlay=overlay;
        }Draw();
    }
    float Step()const{return std::min(96.f,(width-40)/columns);}
    void Layout(){
        columns=std::max(4,std::min(12,static_cast<int>((width-40)/96)));float step=Step();
        rects.clear();std::vector<Place> placed;int bottom=0;auto order=PreviewOrder();if(auto b=BoardView())rects.resize(b->tiles.size());
        if(auto b=BoardView())for(auto const &key:order){auto it=std::find_if(b->tiles.begin(),b->tiles.end(),[&](auto const &t){return t.key==key;});auto const &tile=*it;auto index=static_cast<size_t>(it-b->tiles.begin());int w=tile.card?static_cast<int>(Num(tile.card,L"columns",2)):tile.folder.empty()?1:2,h=tile.card?static_cast<int>(Num(tile.card,L"rows",2)):tile.folder.empty()?1:2;
            w=std::min(columns,w);Place p{0,0,w,h};bool found=false;for(int row=0;row<4096&&!found;++row)for(int col=0;col<=columns-w;++col){p.x=col;p.y=row;if(std::none_of(placed.begin(),placed.end(),[&](auto const &q){return p.x<q.x+q.w&&p.x+p.w>q.x&&p.y<q.y+q.h&&p.y+p.h>q.y;})){found=true;break;}}
            placed.push_back(p);bottom=std::max(bottom,p.y+h);rects[index]={20+p.x*step,112+(p.y-scroll)*step,20+(p.x+w)*step-8,112+(p.y+h-scroll)*step-8};}
        maxScroll=std::max(0,bottom-static_cast<int>((height-164)/step));if(scroll>maxScroll){scroll=maxScroll;Layout();}
        if(auto b=BoardView()){b->stepX=step;b->stepY=step;}painter.width=width;painter.height=height;painter.dpi=dpi;ArmClock();
    }
    void ArmClock(){
        if(!hwnd)return;KillTimer(hwnd,7);bool clock=false,seconds=false;auto b=BoardView();if(!b)return;
        for(size_t i=0;i<rects.size();++i)if(rects[i].bottom>=112&&rects[i].top<height-46){auto const &tile=b->tiles[i];if(!tile.card)continue;auto type=Str(tile.card,L"type");clock|=type==L"system.clock"||type==L"system.calendar";auto custom=tile.card.TryLookup(L"custom");if(custom&&custom.ValueType()==JsonValueType::Object){auto value=custom.GetObject();auto kind=Str(value,L"kind");clock|=kind==L"clock"||kind==L"countdown";if((kind==L"clock"||kind==L"countdown")&&value.HasKey(L"content")){auto content=value.GetNamedObject(L"content");seconds|=Bool(content,L"showSeconds")||Str(content,L"format")==L"hms";}}}
        if(clock){SYSTEMTIME now;GetLocalTime(&now);SetTimer(hwnd,7,seconds?1000-now.wMilliseconds:60000-now.wSecond*1000-now.wMilliseconds,nullptr);}
    }
    void Draw(){
        {NativeRenderScope rendering(app.relay);if(!rendering){if(hwnd)InvalidateRect(hwnd,nullptr,FALSE);return;}DrawCore();}
        if(closePending)FlushClose();
    }
    void DrawCore(){
        if(!hwnd||!visual||!BoardView()||closing||closePending)return;int w=static_cast<int>(std::ceil(width*dpi/96)),h=static_cast<int>(std::ceil(height*dpi/96));
        if(!surface){surface=app.graphics.composition.CreateDrawingSurface({static_cast<float>(w),static_cast<float>(h)},Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,Windows::Graphics::DirectX::DirectXAlphaMode::Premultiplied);visual.Brush(app.graphics.compositor.CreateSurfaceBrush(surface));surfaceBytes=static_cast<size_t>(w)*h*4;}
        ReleaseNativeGlass(root,glass);glassSheet.Sync(app.graphics,hwnd,root,{0,0,width,height},dpi/96,20,NativeMaterial::Menu,!NativeLowPower(app.model),visual,32*1024*1024);
        com_ptr<ID2D1DeviceContext> c;POINT offset{};auto interop=surface.as<abiComposition::ICompositionDrawingSurfaceInterop>();check_hresult(interop->BeginDraw(nullptr,__uuidof(ID2D1DeviceContext),c.put_void(),&offset));
        bool ended=false;try{c->SetDpi(dpi,dpi);c->SetTransform(D2D1::Matrix3x2F::Translation(offset.x*96/dpi,offset.y*96/dpi));c->Clear(D2D1::ColorF(0,0));Paint(c.get());ended=true;check_hresult(interop->EndDraw());}catch(...){if(!ended)interop->EndDraw();throw;}
    }
    void Paint(ID2D1DeviceContext *c){
        NativePaint p(c,app.graphics,Preview70Theme::systemDark);p.Material({0,0,width,height},NativeMaterial::Menu,!NativeLowPower(app.model)&&(glassSheet.root||materialPreview),20);
        p.Text(Str(data,L"title"),{24,12,width-175,42},23,p.theme.Ink(),600);p.Text(Str(data,L"subtitle"),{24,45,width-80,67},12,p.theme.Muted());
        p.Text(L"×",{width-47,8,width-10,44},24,p.theme.Ink(),400,DWRITE_TEXT_ALIGNMENT_CENTER);
        p.Text(Str(data,L"folder").empty()?L"选择副屏 ▾":L"‹ 副屏内容",{24,76,220,103},14,p.theme.Ink());p.Text(L"恢复至…",{width-125,76,width-24,103},14,p.theme.Ink());
        p.Line(20,106,width-20,106,p.theme.Muted(),.5f);c->PushAxisAlignedClip({16,110,width-16,height-47},D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
        auto b=BoardView();struct Overlay{Model &m;Board *prior;~Overlay(){m.overlay=prior;}} overlay{app.model,app.model.overlay};app.model.overlay=b;painter.icons=icons.values;painter.selected.clear();painter.contentRect={16,110,width-16,height-47};
        for(size_t i=0;i<b->tiles.size();++i){auto r=rects[i];if(avoidance.contains(static_cast<int>(i)))continue;if(r.bottom<112||r.top>height-48)continue;if(static_cast<int>(i)==selected)painter.selected.insert(b->tiles[i].key);painter.PaintTile(c,b->tiles[i],r,static_cast<int>(i),b->tiles[i].folder.empty()&&!b->tiles[i].card);painter.selected.clear();}
        c->PopAxisAlignedClip();p.Text(note.empty()?dragging?L"放开以放到桌面 · 在面板内拖动可调整顺序":L"双击打开 · 拖到桌面取出 · 卡片取出后可直接操作":note,{20,height-38,width-174,height-8},12,p.theme.Muted());
        p.Text(L"上一批",{width-166,height-39,width-94,height-8},12,p.theme.Ink());p.Text(L"下一批",{width-88,height-39,width-16,height-8},12,p.theme.Ink());
    }
    int Hit(float x,float y){if(y<110||y>=height-46)return -1;for(size_t i=0;i<rects.size();++i)if(Inside(rects[i],x,y))return static_cast<int>(i);return -1;}
    void Navigate(std::wstring board,std::wstring folder=L"",int offset=0){
        if(!hwnd||busy)return;JsonObject q;Put(q,L"op",std::wstring(L"recovery-open"));Put(q,L"board",board);if(!folder.empty())Put(q,L"folder",folder);Put(q,L"offset",offset);Put(q,L"window",static_cast<double>(reinterpret_cast<UINT_PTR>(hwnd)));auto state=life;auto epoch=generation;
        app.Request(q,[state,epoch](JsonObject const &r){if(auto self=state->owner;self&&self->hwnd&&self->generation==epoch){if(Bool(r,L"ok"))self->Set(r.GetNamedObject(L"result"));else{self->note=Str(r,L"error");self->Draw();}}});
    }
    void Action(std::wstring kind,std::wstring key=L"",std::wstring destination=L"",int column=-1,int row=-1,std::wstring before=L""){
        if(busy||!data||!hwnd)return;busy=true;JsonObject q;Put(q,L"op",std::wstring(L"recovery-action"));Put(q,L"window",static_cast<double>(reinterpret_cast<UINT_PTR>(hwnd)));Put(q,L"board",Str(data,L"board"));Put(q,L"kind",kind);Put(q,L"key",key);Put(q,L"expectedRevision",static_cast<double>(dragRevision?dragRevision:static_cast<long long>(Num(data,L"revision"))));if(!Str(data,L"folder").empty())Put(q,L"folder",Str(data,L"folder"));if(!destination.empty())Put(q,L"destination",destination);if(column>=0){Put(q,L"column",column);Put(q,L"row",row);}if(!before.empty())Put(q,L"before",before);auto state=life;auto epoch=generation;
        app.Request(q,[state,epoch](JsonObject const &r){if(auto self=state->owner;self&&self->generation==epoch){self->busy=false;self->dragRevision=0;if(!self->hwnd)return;if(Bool(r,L"ok")){auto value=r.GetNamedObject(L"result");if(value.HasKey(L"boards")||Bool(value,L"closed"))self->Set(value);}else{self->note=Str(r,L"error");self->Draw();}}});
    }
    void IconsReady(){attempted.clear();RequestIcons();}
    void RequestIcons(){
        if(!hwnd||!BoardView()||!pending.empty())return;JsonArray keys;auto add=[&](Tile const &t){if(keys.Size()<16&&!icons.values.contains(t.key)&&!pending.contains(t.key)&&!attempted.contains(t.key)&&HonorDesktopFile(t,HonorScheme(app.model.preferences),app.model.recycleFull).empty()){keys.Append(Text(t.key));pending.insert(t.key);attempted.insert(t.key);}};
        for(size_t i=0;i<rects.size();++i)if(rects[i].bottom>=112&&rects[i].top<height-46){auto const &tile=BoardView()->tiles[i];if(tile.card)continue;if(tile.folder.empty())add(tile);else for(auto const &child:tile.items)add(child);}
        if(!keys.Size())return;JsonObject q;Put(q,L"op",std::wstring(L"recovery-icons"));Put(q,L"window",static_cast<double>(reinterpret_cast<UINT_PTR>(hwnd)));Put(q,L"board",Str(data,L"board"));q.Insert(L"keys",keys);auto state=life;auto epoch=generation;
        app.Request(q,[state,epoch](JsonObject const &r){if(auto self=state->owner;self&&self->hwnd&&self->generation==epoch){self->pending.clear();bool loaded=false;if(Bool(r,L"ok")){for(auto const &v:r.GetNamedObject(L"result").GetNamedArray(L"icons")){auto icon=v.GetObject();auto png=Str(icon,L"png");if(!png.empty()){self->icons.Load(self->app.graphics,Str(icon,L"key"),png);loaded=true;}}self->Draw();if(loaded)self->RequestIcons();}}});
    }
    void Choose(bool restore){
        HMENU items=CreatePopupMenu();auto values=data.GetNamedArray(restore?L"screens":L"tabs");int id=1;for(auto const &v:values){auto item=v.GetObject();auto label=Str(item,restore?L"label":L"name");AppendMenuW(items,MF_STRING,id++,label.c_str());}POINT pt{};GetCursorPos(&pt);menu=true;int pick=TrackPopupMenu(items,TPM_RETURNCMD|TPM_NONOTIFY,pt.x,pt.y,0,hwnd,nullptr);menu=false;DestroyMenu(items);if(pick>0&&pick<=static_cast<int>(values.Size())){auto item=values.GetAt(pick-1).GetObject();if(restore)Action(L"restore",L"",Str(item,L"id"));else Navigate(Str(item,L"id"));}
    }
    void Click(float x,float y,bool twice=false){
        if(y<50&&x>width-54){Close();return;}if(y>=72&&y<106){if(x>width-135)Choose(true);else if(x<240){if(Str(data,L"folder").empty())Choose(false);else Navigate(Str(data,L"board"));}return;}
        if(y>height-45){if(x>width-92&&Num(data,L"next",-1)>=0)Navigate(Str(data,L"board"),Str(data,L"folder"),static_cast<int>(Num(data,L"next")));else if(x>width-170&&Num(data,L"offset")>0)Navigate(Str(data,L"board"),Str(data,L"folder"),std::max(0,static_cast<int>(Num(data,L"offset"))-64));return;}
        selected=Hit(x,y);if(twice&&selected>=0){auto const &tile=BoardView()->tiles[selected];if(!tile.folder.empty())Navigate(Str(data,L"board"),tile.folder);else if(!tile.card)Action(L"open",tile.key);}Draw();
    }
    static LRESULT CALLBACK Proc(HWND h,UINT msg,WPARAM w,LPARAM l){
        auto self=reinterpret_cast<NativeRecovery*>(GetWindowLongPtrW(h,GWLP_USERDATA));if(msg==WM_NCCREATE){self=static_cast<NativeRecovery*>(reinterpret_cast<CREATESTRUCTW*>(l)->lpCreateParams);SetWindowLongPtrW(h,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(self));}if(!self)return DefWindowProcW(h,msg,w,l);
        // The display broadcast must return without COM/Composition teardown or
        // an animation on a monitor Windows is removing. The relay drains it
        // after the current draw, even if no future paint/clock event arrives.
        if(msg==WM_DISPLAYCHANGE){self->QueueClose(true,true);return 0;}
        if(msg==WM_NCDESTROY){SetWindowLongPtrW(h,GWLP_USERDATA,0);return DefWindowProcW(h,msg,w,l);}
        try{float x=GET_X_LPARAM(l)*96/self->dpi-self->frameInset,y=GET_Y_LPARAM(l)*96/self->dpi-self->frameInset;
            if((self->closing||self->closePending)&&msg!=WM_TIMER&&msg!=WM_CLOSE&&msg!=WM_DESTROY&&msg!=WM_ERASEBKGND&&msg!=WM_PAINT)return msg==WM_NCHITTEST?HTTRANSPARENT:0;
            switch(msg){case WM_NCHITTEST:{POINT pt{GET_X_LPARAM(l),GET_Y_LPARAM(l)};ScreenToClient(h,&pt);return NativeContextMotion::Contains({pt.x*96/self->dpi-self->frameInset+4,pt.y*96/self->dpi-self->frameInset+4},{self->width+8,self->height+8},20)?HTCLIENT:HTTRANSPARENT;}
            case WM_ERASEBKGND:return 1;case WM_PAINT:{PAINTSTRUCT ps;BeginPaint(h,&ps);EndPaint(h,&ps);self->Draw();return 0;}
            case WM_POWERBROADCAST:if(NativeLowPower(self->app.model)){self->FinishOpening();self->dragging=false;self->ResetAvoidance();self->Layout();}self->Draw();return TRUE;
            case WM_CLOSE:self->Close();return 0;case WM_WTSSESSION_CHANGE:if(w==WTS_SESSION_LOCK)self->Close();return 0;case WM_TIMER:if(self->closePending)return 0;if(w==8){if(self->closing)self->Close(self->closeNotify,true);else self->FinishOpening();return 0;}if(w==7){self->ArmClock();self->Draw();}return 0;
            case WM_KEYDOWN:if(w==VK_ESCAPE){self->Close();return 0;}if(w==VK_RETURN&&self->selected>=0){auto const &tile=self->BoardView()->tiles[self->selected];if(!tile.folder.empty())self->Navigate(Str(self->data,L"board"),tile.folder);else if(!tile.card)self->Action(L"open",tile.key);}else if(w==VK_LEFT||w==VK_RIGHT||w==VK_UP||w==VK_DOWN){int count=static_cast<int>(self->BoardView()->tiles.size());if(count){int delta=w==VK_LEFT?-1:w==VK_RIGHT?1:w==VK_UP?-self->columns:self->columns;self->selected=std::clamp(self->selected<0?0:self->selected+delta,0,count-1);auto r=self->rects[self->selected];float step=self->Step();if(r.top<112)self->scroll=std::max(0,self->scroll-static_cast<int>(std::ceil((112-r.top)/step)));if(r.bottom>self->height-47)self->scroll=std::min(self->maxScroll,self->scroll+static_cast<int>(std::ceil((r.bottom-self->height+47)/step)));self->Layout();self->Draw();self->RequestIcons();}}return 0;
            case WM_MOUSEWHEEL:if(self->dragging)return 0;self->scroll=std::clamp(self->scroll-(GET_WHEEL_DELTA_WPARAM(w)>0?1:-1),0,self->maxScroll);self->Layout();self->Draw();self->RequestIcons();return 0;
            case WM_LBUTTONDBLCLK:if(self->opening)return 0;self->Click(x,y,true);return 0;
            case WM_LBUTTONDOWN:if(self->opening)return 0;self->pressed=self->Hit(x,y);self->down={GET_X_LPARAM(l),GET_Y_LPARAM(l)};self->dragRevision=static_cast<long long>(Num(self->data,L"revision"));SetCapture(h);return 0;
            case WM_MOUSEMOVE:if(GetCapture()==h&&self->pressed>=0&&!self->dragging&&std::hypot(static_cast<float>(GET_X_LPARAM(l)-self->down.x),static_cast<float>(GET_Y_LPARAM(l)-self->down.y))>=6*self->dpi/96){self->dragging=true;self->selected=self->pressed;self->Draw();}if(self->dragging)self->PreviewDrag(x,y);return 0;
            case WM_LBUTTONUP:{if(self->opening)return 0;bool drag=self->dragging;int index=self->pressed;self->dragging=false;self->pressed=-1;ReleaseCapture();if(drag&&index>=0&&self->BoardView()&&index<static_cast<int>(self->BoardView()->tiles.size())){auto key=self->BoardView()->tiles[index].key;int target=self->previewTarget;self->ResetAvoidance();self->Layout();if(target>=0&&target!=index)self->Action(L"reorder",key,L"",-1,-1,self->BoardView()->tiles[target].key);else if(x<0||x>=self->width||y<0||y>=self->height){POINT pt{GET_X_LPARAM(l),GET_Y_LPARAM(l)};ClientToScreen(h,&pt);auto hit=WindowFromPoint(pt);for(auto const &window:self->app.windows)if(window->hwnd&&(hit==window->hwnd||IsChild(window->hwnd,hit))){auto board=self->app.model.Find(window->boardId);if(!board)continue;POINT local=pt;ScreenToClient(window->hwnd,&local);float s=window->dpi/96;int col=static_cast<int>(std::floor((local.x/s-board->originX)/board->stepX)),row=static_cast<int>(std::floor((local.y/s-board->originY)/board->stepY));self->Action(L"extract",key,board->id,std::max(0,col),std::max(0,row));break;}}self->Draw();}else{self->dragRevision=0;self->Click(x,y);}return 0;}
            case WM_CAPTURECHANGED:if(self->dragging){self->dragging=false;self->ResetAvoidance();self->Layout();self->Draw();}self->pressed=-1;return 0;
            case WM_RBUTTONUP:{int index=self->Hit(x,y);if(index<0)return 0;auto tile=self->BoardView()->tiles[index];HMENU items=CreatePopupMenu();AppendMenuW(items,MF_STRING,1,L"取出到主屏幕");if(tile.folder.empty()&&!tile.card)AppendMenuW(items,MF_STRING,2,L"打开");POINT pt{};GetCursorPos(&pt);self->menu=true;int pick=TrackPopupMenu(items,TPM_RETURNCMD,pt.x,pt.y,0,h,nullptr);self->menu=false;DestroyMenu(items);if(pick==2)self->Action(L"open",tile.key);else if(pick==1){auto screens=self->data.GetNamedArray(L"screens");for(auto const &v:screens){auto screen=v.GetObject();if(Bool(screen,L"primary")){self->Action(L"extract",tile.key,Str(screen,L"id"));break;}}}return 0;}
            case WM_ACTIVATE:if(LOWORD(w)==WA_INACTIVE&&!self->menu&&!self->dragging&&GetCapture()!=h){bool dock=false;if(self->app.shell)for(auto const &[id,bar]:self->app.shell->bars)if(bar->hwnd==reinterpret_cast<HWND>(l)&&bar->dock)dock=true;if(!dock)self->Close();}return 0;
            }
        }catch(hresult_error const &){self->note=L"副屏面板暂不可用，原内容已保留。";}catch(std::exception const &){self->note=L"副屏面板暂不可用，原内容已保留。";}
        return DefWindowProcW(h,msg,w,l);
    }
};
inline void OpenDesktopRecovery(App &app,HWND anchor){
    if(app.recovery&&app.recovery->hwnd){app.recovery->Close();return;}
    JsonObject q;Put(q,L"op",std::wstring(L"recovery-open"));app.Request(q,[&app,anchor](JsonObject const &r){if(!Bool(r,L"ok")||app.stopping)return;if(!app.recovery)app.recovery=std::make_unique<NativeRecovery>(app);app.recovery->Set(r.GetNamedObject(L"result"),anchor);});
}
