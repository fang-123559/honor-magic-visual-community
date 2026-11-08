/** Local visual state: never synchronized with another toolbar's item order.
 * Keep the active source ID until this toolbar ends/cancels its own drag.
 * Do not use a possibly stale per-sortable reactive isDragging bridge.
 */
export function createDragVisualState(onChange: (id: string | null) => void) {
  let activeId: string | null = null;
  function set(id: string | null) {
    if (activeId === id) return;
    activeId = id;
    onChange(id);
  }
  return {
    start(id: string | number | undefined) { set(id == null ? null : String(id)); },
    end() { set(null); },
    get activeId() { return activeId; },
  };
}
