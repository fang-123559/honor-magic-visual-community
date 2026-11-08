#pragma once
inline void NativeCardText(NativePaint &p,std::wstring const &text,D2D1_RECT_F bounds,float font,int weight=400){
    if(bounds.right<=bounds.left||text.empty())return;auto format=p.graphics.Format(font,false,weight);com_ptr<IDWriteTextLayout> layout;check_hresult(p.graphics.text->CreateTextLayout(text.c_str(),static_cast<UINT32>(text.size()),format,bounds.right-bounds.left,bounds.bottom-bounds.top,layout.put()));
    layout->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);layout->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);DWRITE_TRIMMING trim{DWRITE_TRIMMING_GRANULARITY_CHARACTER,0,0};com_ptr<IDWriteInlineObject> dots;check_hresult(p.graphics.text->CreateEllipsisTrimmingSign(format,dots.put()));layout->SetTrimming(&trim,dots.get());p.c->DrawTextLayout({bounds.left,bounds.top},layout.get(),p.Color(p.theme.Ink()),D2D1_DRAW_TEXT_OPTIONS_CLIP);
}
