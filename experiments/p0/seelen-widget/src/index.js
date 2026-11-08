import { Widget, ConnectedMonitorList, Settings } from "@seelen-ui/lib";
import { appDataDir } from "@tauri-apps/api/path";

async function main() {
  // Source for Seelen 2.8.9 declares SDK 2.8.9, but npm currently publishes 2.8.4.
  // This is an explicitly versioned compatibility probe, not a confirmed matching SDK.
  const widget = Widget.self;
  await widget.init({ saveAndRestoreLastRect: false, useThemes: false });
  const monitorId = widget.decoded?.monitorId;
  if (!monitorId) throw new Error("真实 Seelen monitor ID 未提供");
  const count = P0_FIXTURE_COUNT;
  const monitors = await ConnectedMonitorList.getAsync();
  const monitor = monitors.all().find((item) => item.id === monitorId);
  if (!monitor) throw new Error("真实 monitor ID 未出现在 Seelen 显示器列表中");
  const scale = monitor.scaleFactor;
  if (!Number.isFinite(scale) || scale <= 0) throw new Error("无效的显示器缩放比例");
  document.getElementById("probe").style.height = `${count === 0 ? 120 : 752}px`;
  const items = document.getElementById("items");
  for (let index = 0; index < count; index++) {
    const item = document.createElement("div");
    item.className = "item";
    item.textContent = `P${index + 1}`;
    items.appendChild(item);
  }
  document.getElementById("status").textContent = `monitor: ${monitorId}; 合成项目: ${count}`;
  const rect = {
    left: monitor.rect.left + 32, top: monitor.rect.top + 32,
    right: monitor.rect.left + 32 + Math.round(320 * scale),
    bottom: monitor.rect.top + 32 + Math.round((count === 0 ? 120 : 752) * scale)
  };
  await widget.setPosition(rect);
  await widget.ready();
  const runtimeSettings = await Settings.getAsync();
  const runtimeDataPath = await appDataDir().catch((error) => `unavailable: ${error.message}`);
  console.info("P0_EFFECTIVE_CONFIG", JSON.stringify({
    runtimeDataPath,
    wegEnabled: runtimeSettings.byWidget?.["@seelen/weg"]?.enabled,
    toolbarEnabled: runtimeSettings.byWidget?.["@seelen/fancy-toolbar"]?.enabled,
    probeEnabled: runtimeSettings.byWidget?.[widget.id]?.enabled,
    settingsKeys: Object.keys(runtimeSettings.inner ?? {})
  }));
  console.info("P0_WIDGET_READY", JSON.stringify({
    widgetId: widget.id, monitorId, syntheticItems: count,
    sdkVersion: "2.8.4", intendedApplicationVersion: "2.8.9",
    rect, scale, devicePixelRatio: globalThis.devicePixelRatio,
    desktopTakeover: false, fileOperations: false
  }));
}

main().catch((error) => {
  const status = document.getElementById("status");
  if (status) status.textContent = `初始化失败：${error.message}`;
  console.error("P0_WIDGET_NOT_READY", error);
});
