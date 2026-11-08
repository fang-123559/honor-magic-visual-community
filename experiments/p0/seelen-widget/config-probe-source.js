import { Widget, Settings } from "@seelen-ui/lib";
import { appDataDir } from "@tauri-apps/api/path";

async function main() {
  const widget = Widget.self;
  await widget.init({ useThemes: false, saveAndRestoreLastRect: false });
  await widget.ready();
  const path = await appDataDir();
  const fromFile = await Settings.loadCustom(`${path}/settings.json`);
  const current = await Settings.getAsync();
  console.info("P0_CONFIG_READ_COMPARE", JSON.stringify({ path,
    fileWeg: fromFile.byWidget["@seelen/weg"].enabled,
    memoryWeg: current.byWidget["@seelen/weg"].enabled,
    fileKeys: Object.keys(fromFile.inner)
  }));
  const system = new Set(["@seelen/settings", "@seelen/dialog", "@seelen/context-menu", "@seelen/tooltip", widget.id]);
  // v2.8.9 has implicitly enabled official widgets absent from byWidget.
  // Pinned list comes from the verified MSIX's 24 static/widgets/*/metadata.yml.
  const bundled = ["apps-menu", "bluetooth-popup", "calendar-popup", "context-menu", "dialog",
    "flyouts", "icon-editor", "keyboard-selector", "media-popup", "network-popup", "notifications",
    "power-menu", "quick-settings", "settings", "system-tray", "task-switcher", "fancy-toolbar",
    "tooltip", "user-menu", "wallpaper-manager", "weg", "weg-preview", "window-manager", "workspaces-viewer"];
  const ids = new Set([...Object.keys(current.inner.byWidget), ...bundled.map(id => `@seelen/${id}`)]);
  for (const id of ids) current.inner.byWidget[id] = { ...current.inner.byWidget[id], enabled: system.has(id) };
  current.inner.backupSyncEnabled = false;
  current.inner.drpc = false;
  console.info("P0_CONFIG_PERMISSION_REQUEST", "Seelen ModifyConfiguration dialog; no bypass or auto-grant");
  await current.save();
  const applied = await Settings.getAsync();
  console.info("P0_CONFIG_APPLIED", JSON.stringify({ weg: applied.byWidget["@seelen/weg"].enabled,
    toolbar: applied.byWidget["@seelen/fancy-toolbar"].enabled,
    wallpaper: applied.byWidget["@seelen/wallpaper-manager"].enabled,
    explicitWidgetStates: Object.fromEntries([...ids].map(id => [id, applied.inner.byWidget[id]?.enabled ?? null])) }));
  if ([...ids].some(id => !system.has(id) && applied.inner.byWidget[id]?.enabled !== false)) {
    throw new Error("Some non-system widgets remain enabled; profile cannot be accepted as minimal");
  }
}

main().catch((error) => console.error("P0_CONFIG_FAILED", error));
