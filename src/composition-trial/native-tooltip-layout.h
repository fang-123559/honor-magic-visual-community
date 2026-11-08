#pragma once
// Preserve the measured layout for drawing. Shrinking to the fractional ink
// width and laying out again can add a line and crop the name and timestamp.
struct NativeTooltipLayout {
    com_ptr<IDWriteTextLayout> text;
    float width=0,height=0,top=6;
    void Build(Graphics &graphics,std::wstring const &value,float availableWidth,bool oneLine,bool preview){
        text=nullptr;top=preview?150.f:6.f;
        float maximum=std::max(1.f,std::min(oneLine?144.f:500.f,availableWidth));
        check_hresult(graphics.text->CreateTextLayout(value.c_str(),static_cast<UINT32>(value.size()),graphics.Format(12.8f,false,400),std::max(1.f,maximum-16),4096,text.put()));
        text->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
        text->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
        text->SetWordWrapping(oneLine?DWRITE_WORD_WRAPPING_NO_WRAP:DWRITE_WORD_WRAPPING_EMERGENCY_BREAK);
        DWRITE_TEXT_METRICS metrics{};check_hresult(text->GetMetrics(&metrics));
        width=std::min(maximum,std::max(preview?252.f:32.f,std::ceil(metrics.widthIncludingTrailingWhitespace)+16));
        // Measure once more at the exact final width. Do not modify the shared
        // text format or vertically center text inside an undersized box.
        text->SetMaxWidth(std::max(1.f,width-16));check_hresult(text->GetMetrics(&metrics));
        text->SetMaxHeight(oneLine?20.f:std::ceil(metrics.height));
        height=top+(oneLine?20.f:std::ceil(metrics.height))+6;
    }
};
