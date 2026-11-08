export const compactImeId = "magic-community-ime-compact";

export function compactImeItem(template) {
  return {
    id: compactImeId,
    scopes: ["Keyboard"],
    template,
    render: null,
    canvasSize: null,
    tooltip: 'return [activeLang?.name || "!?", " - ", activeKeyboard?.displayName || "!?"]',
    badge: null,
    onClick: 'trigger("@seelen/keyboard-selector");',
    onWheelUp: null,
    onWheelDown: null,
    style: { flexShrink: 0, width: "min-content", fontWeight: 600 },
    remoteData: {},
  };
}

export function transformToolbarState(before, template) {
  const next = structuredClone(before);
  for (const side of ["left", "center", "right"]) {
    if (!Array.isArray(next[side])) throw new Error("Unexpected toolbar state shape");
  }
  const removed = [];
  const left = next.left;
  const hasFocusedApp = left.includes("@default/focused-app");
  next.left = left.filter((item, index) => {
    const knownPlugin = item === "@default/focused-app" || item === "@default/focused-app-title";
    const knownTitleSeparator = typeof item === "object" && item !== null &&
      item.template === 'return focusedApp.title ? "-" : ""' &&
      Array.isArray(item.scopes) && item.scopes.length === 1 && item.scopes[0] === "FocusedApp";
    const knownLeadingSeparator = hasFocusedApp && typeof item === "object" && item !== null &&
      item.template === 'return "|"' && Array.isArray(item.scopes) && item.scopes.length === 0 &&
      left[index + 1] === "@default/focused-app";
    if (knownPlugin || knownTitleSeparator || knownLeadingSeparator) {
      removed.push(typeof item === "string" ? item : item.id);
      return false;
    }
    return true;
  });
  let replacements = 0;
  for (const side of ["left", "center", "right"]) {
    next[side] = next[side].map(item => {
      if (item === "@seelen/tb-keyboard-selector" || item?.id === compactImeId) {
        replacements++;
        return compactImeItem(template);
      }
      return item;
    });
  }
  if (replacements !== 1) throw new Error("Expected exactly one existing keyboard indicator");
  return { state: next, removed, keyboardReplacements: replacements };
}
