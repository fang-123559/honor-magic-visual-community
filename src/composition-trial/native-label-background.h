#pragma once
// Read the configured wallpaper once per settings/display event, never capture the
// desktop. A bounded thumbnail supplies local label contrast without a polling loop.
struct NativeLabelBackground {
    bool valid=false,available=false;
    RECT monitor{},canvas{};
    DESKTOP_WALLPAPER_POSITION position=DWPOS_FILL;
    COLORREF background=RGB(0,0,0);
    UINT imageWidth=0,imageHeight=0,pixelWidth=0,pixelHeight=0;
    std::vector<BYTE> pixels;
    void Invalidate(){valid=false;available=false;pixels.clear();}
    void Load(Graphics &graphics,HWND window){
        if(valid)return;valid=true;available=false;pixels.clear();
        try{
            com_ptr<IDesktopWallpaper> wallpaper;
            check_hresult(CoCreateInstance(__uuidof(DesktopWallpaper),nullptr,CLSCTX_ALL,__uuidof(IDesktopWallpaper),wallpaper.put_void()));
            MONITORINFO display{sizeof(display)};if(!GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&display))return;
            monitor=canvas=display.rcMonitor;wallpaper->GetPosition(&position);wallpaper->GetBackgroundColor(&background);
            UINT count=0;check_hresult(wallpaper->GetMonitorDevicePathCount(&count));
            std::wstring path;
            for(UINT i=0;i<std::min(count,32u);++i){
                LPWSTR id=nullptr;check_hresult(wallpaper->GetMonitorDevicePathAt(i,&id));
                RECT rect{};HRESULT hr=wallpaper->GetMonitorRECT(id,&rect);
                LPWSTR file=nullptr;if(SUCCEEDED(hr)&&EqualRect(&rect,&monitor))hr=wallpaper->GetWallpaper(id,&file);
                CoTaskMemFree(id);if(file){path=file;CoTaskMemFree(file);break;}
            }
            available=true; // An empty path represents the solid Windows background.
            if(path.empty())return;
            if(position==DWPOS_SPAN)canvas={GetSystemMetrics(SM_XVIRTUALSCREEN),GetSystemMetrics(SM_YVIRTUALSCREEN),GetSystemMetrics(SM_XVIRTUALSCREEN)+GetSystemMetrics(SM_CXVIRTUALSCREEN),GetSystemMetrics(SM_YVIRTUALSCREEN)+GetSystemMetrics(SM_CYVIRTUALSCREEN)};
            if(path.rfind(L"\\\\",0)==0){available=false;return;}
            com_ptr<IWICBitmapDecoder> decoder;check_hresult(graphics.wic->CreateDecoderFromFilename(path.c_str(),nullptr,GENERIC_READ,WICDecodeMetadataCacheOnDemand,decoder.put()));
            com_ptr<IWICBitmapFrameDecode> frame;check_hresult(decoder->GetFrame(0,frame.put()));check_hresult(frame->GetSize(&imageWidth,&imageHeight));
            if(!imageWidth||!imageHeight||static_cast<uint64_t>(imageWidth)*imageHeight>64000000){available=false;return;}
            float scale=std::min(1.f,256.f/std::max(imageWidth,imageHeight));
            pixelWidth=std::max(1u,static_cast<UINT>(std::lround(imageWidth*scale)));pixelHeight=std::max(1u,static_cast<UINT>(std::lround(imageHeight*scale)));
            com_ptr<IWICBitmapScaler> scaler;check_hresult(graphics.wic->CreateBitmapScaler(scaler.put()));check_hresult(scaler->Initialize(frame.get(),pixelWidth,pixelHeight,WICBitmapInterpolationModeFant));
            com_ptr<IWICFormatConverter> converter;check_hresult(graphics.wic->CreateFormatConverter(converter.put()));check_hresult(converter->Initialize(scaler.get(),GUID_WICPixelFormat32bppBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom));
            pixels.resize(pixelWidth*pixelHeight*4);check_hresult(converter->CopyPixels(nullptr,pixelWidth*4,static_cast<UINT>(pixels.size()),pixels.data()));
        }catch(...){available=false;pixels.clear();}
    }
    double Luminance(double x,double y)const{
        auto linear=[](double v){v/=255;return v<=.04045?v/12.92:std::pow((v+.055)/1.055,2.4);};
        auto luminance=[&](int r,int g,int b){return .2126*linear(r)+.7152*linear(g)+.0722*linear(b);};
        if(pixels.empty())return luminance(GetRValue(background),GetGValue(background),GetBValue(background));
        double w=canvas.right-canvas.left,h=canvas.bottom-canvas.top,sx=w/imageWidth,sy=h/imageHeight;
        x-=canvas.left;y-=canvas.top;
        if(position==DWPOS_TILE){x=std::fmod(x+imageWidth,imageWidth);y=std::fmod(y+imageHeight,imageHeight);}
        else{
            if(position==DWPOS_CENTER)sx=sy=1;
            else if(position==DWPOS_FIT)sx=sy=std::min(sx,sy);
            else if(position!=DWPOS_STRETCH)sx=sy=std::max(sx,sy);
            x=(x-(w-imageWidth*sx)/2)/sx;y=(y-(h-imageHeight*sy)/2)/sy;
        }
        if(x<0||y<0||x>=imageWidth||y>=imageHeight)return luminance(GetRValue(background),GetGValue(background),GetBValue(background));
        UINT px=std::min(pixelWidth-1,static_cast<UINT>(x*pixelWidth/imageWidth)),py=std::min(pixelHeight-1,static_cast<UINT>(y*pixelHeight/imageHeight));
        auto p=&pixels[(py*pixelWidth+px)*4];return luminance(p[2],p[1],p[0]);
    }
    bool LightInk(Graphics &graphics,HWND window,D2D1_RECT_F label,float dpi){
        Load(graphics,window);if(!available)return true;
        POINT origin{};ClientToScreen(window,&origin);double total=0;float scale=dpi/96;
        for(int y=0;y<3;++y)for(int x=0;x<5;++x)total+=Luminance(origin.x+(label.left+(label.right-label.left)*(x+.5f)/5)*scale,origin.y+(label.top+(label.bottom-label.top)*(y+.5f)/3)*scale);
        double l=total/15;return 1.05/(l+.05)>=(l+.05)/.05;
    }
};
