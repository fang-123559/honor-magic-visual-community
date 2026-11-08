// One visible cell is reserved for opening the folder. Under capacity it is
// the add affordance; at capacity or above it previews the remaining items.
export function folderCover(items, columns, rows) {
  const capacity = columns * rows, directCount = capacity - 1;
  return { direct: items.slice(0, directCount), empty: Math.max(0, directCount - items.length),
    preview: items.slice(directCount, directCount + 4), plus: items.length < capacity,
    overflow: Math.max(0, items.length - directCount - 4) };
}
