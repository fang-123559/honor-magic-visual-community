// Compact presentation of the same Seelen Keyboard scope; popup behavior stays upstream.
if (!activeLang || !activeKeyboard) return "…";
const code = (activeLang.code || "").toLowerCase();
if (code.startsWith("zh")) {
  // When IME state has not arrived, fall back to the selected Chinese layout.
  if (!imeState) return "中";
  return imeState.open && imeState.native ? "中" : "英";
}
if (code.startsWith("en")) return "英";
// Preserve identification of other languages instead of mislabelling them English.
return activeLangPrefix || activeLang.code || "…";
