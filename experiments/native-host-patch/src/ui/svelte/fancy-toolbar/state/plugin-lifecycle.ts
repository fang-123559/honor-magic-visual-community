import type { PluginId, ToolbarItem2 } from "@seelen-ui/lib/types";

/** Resource availability is transient; only an explicit user removal deletes layout. */
export function applyPendingPluginEnables(
  items: ToolbarItem2[],
  pending: PluginId[],
  registered: ReadonlySet<PluginId>,
): { items: ToolbarItem2[]; pending: PluginId[] } {
  let next = items;
  const waiting: PluginId[] = [];
  const seen = new Set(items.map((item) => typeof item === "string" ? item : item.id));
  for (const id of pending) {
    if (seen.has(id)) continue;
    if (!registered.has(id)) {
      if (!waiting.includes(id)) waiting.push(id);
      continue;
    }
    // Newly enabled modules use the normal right-side insertion rule. Existing
    // modules keep their exact position, including across a resource reconnect.
    next = [...next, id];
    seen.add(id);
  }
  return { items: next, pending: waiting };
}
