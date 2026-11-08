#pragma once
#include <algorithm>

// Prototype proportions fitted to an existing grid footprint. These same
// rectangles drive paint, the expand target, caption editing and icon demand.
namespace NativeFolderCover
{
template <class Rect> struct Layout
{
    Rect bounds{};
    int columns = 2, rows = 2;
    float tile = 0, padding = 0, footer = 0, pitchX = 0, pitchY = 0, scale = 0;
    bool names=false;
    Layout(Rect rect, int cols, int lines, bool showNames=false)
        : bounds(rect), columns(std::clamp(cols, 1, 8)), rows(std::clamp(lines, 1, 8)), names(showNames)
    {
        float width = std::max(0.f, rect.right - rect.left), height = std::max(0.f, rect.bottom - rect.top);
        scale = std::min(width / (columns * 76.f + (columns - 1) * 26.f + 52.f),
                         height / (rows * 76.f + (rows - 1) * 26.f + 102.f + (names?22.f:0.f)));
        tile = 76 * scale;
        padding = 26 * scale;
        footer = 50 * scale;
        pitchX = columns > 1 ? (width - 2 * padding - tile) / (columns - 1) : 0;
        pitchY = tile + 26.f * scale;
    }
    int Capacity() const
    {
        return columns * rows;
    }
    int MainCount(int count) const
    {
        return std::clamp(count, 0, Capacity() - 1);
    }
    int PreviewCount(int count) const
    {
        return std::clamp(count - (Capacity() - 1), 0, 4);
    }
    int VisibleCount(int count) const
    {
        return MainCount(count) + PreviewCount(count);
    }
    Rect Cell(int index) const
    {
        float x = bounds.left + padding + (index % columns) * pitchX,
              y = bounds.top + padding + (index / columns) * pitchY;
        return {x, y, x + tile, y + tile};
    }
    Rect Corner() const
    {
        return Cell(Capacity() - 1);
    }
    Rect Name(int index) const
    {
        auto r=Cell(index);return {r.left-8*scale,r.bottom+2*scale,r.right+8*scale,r.bottom+22*scale};
    }
    int HitMain(float x,float y,int count) const
    {
        for(int i=0;i<MainCount(count);++i){auto r=Cell(i);if(names){auto label=Name(i);r.left=label.left;r.right=label.right;r.bottom=label.bottom;}if(x>=r.left&&x<r.right&&y>=r.top&&y<r.bottom)return i;}
        return -1;
    }
    Rect Mini(int index) const
    {
        auto corner = Corner();
        float gap = 5 * scale, side = (tile - gap) / 2;
        float x = corner.left + (index % 2) * (side + gap), y = corner.top + (index / 2) * (side + gap);
        return {x, y, x + side, y + side};
    }
    Rect Caption() const
    {
        return {bounds.left + padding, bounds.bottom - padding - footer, bounds.right - padding,
                bounds.bottom - padding};
    }
};
} // namespace NativeFolderCover
