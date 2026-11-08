#pragma once
#include <algorithm>
#include <cmath>
#include <utility>
inline long DragImageOrigin(long pointer,float grabDip,float targetScale){return static_cast<long>(std::lround(pointer-grabDip*targetScale));}
// DIP geometry only. Pointer motion does not mutate layout or require IPC.
template <class Rect> bool InRect(Rect r, float x, float y)
{
    return x >= r.left && x < r.right && y >= r.top && y < r.bottom;
}
template <class Rect> float FolderRowStep(Rect content, int columns)
{
    return (content.right-content.left-(columns-1)*15)/columns+15;
}

// Folder tiles are painted on a panel-local surface; hit testing remains in
// desktop coordinates. The clip must move into the same space as the paint.
template <class Rect> Rect FolderLocalViewport(Rect content, Rect panel)
{
    return {content.left-panel.left, content.top-panel.top, content.right-panel.left, content.bottom-panel.top};
}
template <class Rect> Rect FolderCellBounds(Rect content, int columns, int scrollRow, int index)
{
    float cell = (content.right - content.left - (columns - 1) * 15) / columns;
    float x = content.left + (index % columns) * (cell + 15);
    float y = content.top + (index / columns - scrollRow) * (cell+15);
    return {x, y, x + cell, y + cell};
}
inline std::pair<int, int> FolderVisibleRange(int count, int columns, int scrollRow, float height, float step)
{
    int first = std::clamp(scrollRow * columns, 0, count);
    int rows = std::max(0, static_cast<int>(std::floor((height+15)/step)));
    return {first, std::min(count, first + rows * columns)};
}
inline int FolderShownRows(int count,int columns,float maxHeight,float step)
{
    int rows=std::max(1,(count+columns-1)/columns);
    int fit=std::max(1,static_cast<int>(std::floor((maxHeight-84+15)/step)));
    return std::min({rows,fit,4});
}
template <class Rect>
int FolderHitIndex(Rect content, int columns, int scrollRow, int count, float x, float y)
{
    if (!InRect(content, x, y))
        return -1;
    float cell = (content.right - content.left - (columns - 1) * 15) / columns;
    if (cell <= 0)
        return -1;
    int column = static_cast<int>((x - content.left) / (cell + 15));
    int row = static_cast<int>((y - content.top) / (cell+15));
    int index = (row + scrollRow) * columns + column;
    return column < columns && index < count &&
                   InRect(FolderCellBounds(content, columns, scrollRow, index), x, y)
               ? index
               : -1;
}
enum class FolderDropScope
{
    Desktop,
    Folder
};
template <class Rect> FolderDropScope DropScope(bool open, Rect panel, float x, float y)
{
    return open && InRect(panel, x, y) ? FolderDropScope::Folder : FolderDropScope::Desktop;
}
