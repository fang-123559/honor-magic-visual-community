#pragma once
// Per-popup latest-value queue. One request in flight, one pending value per
// offered row; only input/acknowledgement schedules the 40 ms one-shot timer.
inline double NativePopup::RangeValue(std::wstring const &id,double incoming){
    auto found=rangeEdits.find(id);if(found==rangeEdits.end())return incoming;
    auto &edit=found->second;
    if(edit.pending||edit.inFlight||rangeDragging==id)return edit.value;
    if(std::abs(incoming-edit.value)<=.0051||GetTickCount64()>=edit.holdUntil){rangeEdits.erase(found);return incoming;}
    return edit.value;
}
inline void NativePopup::SetRangeValue(std::wstring const &id,double value){
    if(!std::isfinite(value))return;
    auto row=std::find_if(rows.begin(),rows.end(),[&](auto const &r){return r.id==id&&AdjustableRange(r);});
    if(row==rows.end())return;
    auto [found,created]=rangeEdits.try_emplace(id);auto &edit=found->second;
    if(created)edit.confirmed=row->value;
    value=std::clamp(value,0.,1.);edit.value=value;edit.pending=true;edit.holdUntil=GetTickCount64()+1200;row->value=value;
    if(hwnd&&!rangeInFlight&&!rangeScheduled){rangeScheduled=true;SetTimer(hwnd,90,40,nullptr);}Draw();
}
inline void NativePopup::SendRangeValue(bool final){
    if(hwnd)KillTimer(hwnd,90);rangeScheduled=false;
    rangeFinalPending|=final;
    if(!visible||dismissing||rangeInFlight)return;
    auto found=std::find_if(rangeEdits.begin(),rangeEdits.end(),[](auto const &e){return e.second.pending;});
    if(found==rangeEdits.end())return;
    auto now=GetTickCount64();
    if(hwnd&&!rangeFinalPending&&now-rangeLastSent<40){rangeScheduled=true;SetTimer(hwnd,90,static_cast<UINT>(40-(now-rangeLastSent)),nullptr);return;}
    rangeFinalPending=false;
    auto id=found->first;auto &edit=found->second;edit.pending=false;edit.inFlight=true;rangeInFlight=true;rangeLastSent=now;
    JsonObject request;Put(request,L"op",std::wstring(L"flyout-adjust"));Put(request,L"token",token);Put(request,L"row",id);Put(request,L"value",edit.value);request.Insert(L"feedback",JsonValue::CreateBooleanValue(std::exchange(edit.feedback,false)));Put(request,L"window",static_cast<double>(reinterpret_cast<UINT_PTR>(hwnd)));
    auto owner=life;auto expected=token;
    app.Request(request,[owner,expected,id](JsonObject const &reply){
        auto self=owner?owner->owner:nullptr;if(!self||!self->visible||self->token!=expected)return;
        self->rangeInFlight=false;auto found=self->rangeEdits.find(id);if(found==self->rangeEdits.end()){self->SendRangeValue();return;}
        auto &edit=found->second;edit.inFlight=false;
        auto result=reply.GetNamedObject(L"result",JsonObject{});double actual=Num(result,L"value",-1);
        if(!Bool(reply,L"ok")||!Bool(result,L"accepted")||!std::isfinite(actual)||actual<0||actual>1){
            double previous=edit.confirmed;self->rangeEdits.erase(found);
            for(auto &row:self->rows)if(row.id==id)row.value=previous;
        }else{
            edit.confirmed=actual;edit.holdUntil=GetTickCount64()+1200;
            if(!edit.pending){edit.value=actual;for(auto &row:self->rows)if(row.id==id)row.value=actual;}
        }
        self->Draw();self->SendRangeValue();
    });
}
inline bool NativePopup::RangePointer(UINT message,WPARAM w,LPARAM l){
    if(Flyout()||!visible||dismissing)return false;
    POINT point{GET_X_LPARAM(l),GET_Y_LPARAM(l)};if(message==WM_MOUSEWHEEL)ScreenToClient(hwnd,&point);
    float x=point.x*96/dpi,y=point.y*96/dpi;
    auto change=[&](std::wstring const &id){for(auto const &row:rows)if(row.id==id){auto track=RangeTrack(row);SetRangeValue(id,(x-contentX-track.left)/std::max(1.f,track.right-track.left));break;}};
    if(message==WM_MOUSEWHEEL){
        int hit=Hit(x,y);if(hit<0||!AdjustableRange(rows[hit]))return false;
        auto row=rows[hit];auto track=RangeTrack(row);if(!Inside(track,x-contentX,y-contentY))return false;
        SetRangeValue(row.id,row.value+GET_WHEEL_DELTA_WPARAM(w)/120.*.02);return true;
    }
    if(message==WM_LBUTTONDOWN){
        int hit=Hit(x,y);if(hit<0||!AdjustableRange(rows[hit]))return false;
        focus=hit;rangeStartValue=rows[hit].value;rangeDragging=rows[hit].id;SetCapture(hwnd);change(rangeDragging);return true;
    }
    if(message==WM_MOUSEMOVE&&!rangeDragging.empty()){change(rangeDragging);return true;}
    if(message==WM_LBUTTONUP&&!rangeDragging.empty()){
        auto id=std::exchange(rangeDragging,L"");change(id);if(auto edit=rangeEdits.find(id);edit!=rangeEdits.end())edit->second.feedback=id.starts_with(L"volume:")&&id!=L"volume:microphone"&&std::abs(edit->second.value-rangeStartValue)>.0001;if(GetCapture()==hwnd)ReleaseCapture();SendRangeValue(true);return true;
    }
    if(message==WM_CAPTURECHANGED&&!rangeDragging.empty()){rangeDragging.clear();SendRangeValue(true);return true;}
    return false;
}
