import { invoke, SeelenCommand, SeelenEvent, subscribe, Widget } from "@seelen-ui/lib";
import { ZOrder } from "@seelen-ui/lib/types";
import { lazyRune } from "libs/ui/svelte/utils";
import { ConfigState } from "./config.svelte";
import { listen } from "@tauri-apps/api/event";
import { brightnessFeedbackEvent, type BrightnessFeedback } from "libs/ui/svelte/utils/brightness-feedback";

let showing = $state(false);

const monitors = lazyRune(() => invoke(SeelenCommand.SystemGetMonitors));
subscribe(SeelenEvent.SystemMonitorsChanged, monitors.setByPayload);

await monitors.init();
let contentSize = $state.raw<{ width: number; height: number; ratio: number } | null>(null);
let stopObserving: (() => void) | undefined;

function observeContent(element: HTMLElement) {
  stopObserving?.();
  const measure = () => {
    const ratio = window.devicePixelRatio;
    const width = Math.ceil(element.scrollWidth * ratio);
    const height = Math.ceil(element.scrollHeight * ratio);
    if (width > 0 && height > 0 &&
        (width !== contentSize?.width || height !== contentSize?.height || ratio !== contentSize?.ratio)) {
      contentSize = { width, height, ratio };
    }
  };
  const observer = new ResizeObserver(measure);
  observer.observe(element, { box: "border-box" });
  window.addEventListener("resize", measure);
  const stop = () => {
    observer.disconnect();
    window.removeEventListener("resize", measure);
    window.removeEventListener("pagehide", stop);
  };
  stopObserving = stop;
  window.addEventListener("pagehide", stop, { once: true });
  measure();
}

let drafts = $state<Record<string, { source: string; sequence: number; value: number }>>({});
const revisions = new Map<string, number>();
const expiry = new Map<string, ReturnType<typeof setTimeout>>();
const unlisten = await listen<BrightnessFeedback>(brightnessFeedbackEvent, ({ payload: edit }) => {
  if (!edit || typeof edit.source !== "string" || edit.source.length > 80 ||
      !Number.isSafeInteger(edit.sequence) || edit.sequence <= (revisions.get(edit.source) ?? -1) ||
      !monitors.value.some(m => m.id === edit.id) ||
      edit.value !== null && (!Number.isFinite(edit.value) || edit.value < 0 || edit.value > 100)) return;
  revisions.set(edit.source, edit.sequence);
  if (revisions.size > 64) revisions.delete(revisions.keys().next().value!);
  if (edit.value === null && drafts[edit.id]?.source !== edit.source) return;
  clearTimeout(expiry.get(edit.id));
  expiry.delete(edit.id);
  if (edit.value === null) { delete drafts[edit.id]; return; }
  drafts[edit.id] = { source: edit.source, sequence: edit.sequence, value: edit.value };
  // A sender that closes or crashes cannot leave the resident flyout stuck.
  expiry.set(edit.id, setTimeout(() => { delete drafts[edit.id]; expiry.delete(edit.id); }, 2000));
});
window.addEventListener("pagehide", () => {
  unlisten();
  for (const timer of expiry.values()) clearTimeout(timer);
  expiry.clear();
}, { once: true });
const displayMonitors = $derived(monitors.value.map(m => drafts[m.id] ? { ...m, brightness: drafts[m.id].value } : m));

const primaryMonitor = $derived.by(() => {
  return monitors.value.find((m) => m.isPrimary) || monitors.value[0];
});

// One scalar frame and one native writer: resize and centering are atomic.
// Brightness/HDR snapshots with unchanged geometry send no position requests.
const position = $derived.by(() => {
    const monitor = primaryMonitor;
    if (!monitor || !contentSize) {
      return null;
    }

    const placement = ConfigState.config.placement;
    const padding = Math.max(0, ConfigState.config.margin * contentSize.ratio);

    const monitorWidth = monitor.rect.right - monitor.rect.left;
    const monitorHeight = monitor.rect.bottom - monitor.rect.top;

    const monitorCenterX = monitor.rect.left + monitorWidth / 2;
    const monitorCenterY = monitor.rect.top + monitorHeight / 2;

    const width = Math.min(contentSize.width, monitorWidth);
    const height = Math.min(contentSize.height, monitorHeight);

    let x: number, y: number;
    if (placement === "left") {
      x = monitor.rect.left + padding;
      y = Math.round(monitorCenterY - height / 2);
    } else if (placement === "top") {
      x = Math.round(monitorCenterX - width / 2);
      y = monitor.rect.top + padding;
    } else if (placement === "right") {
      x = monitor.rect.right - width - padding;
      y = Math.round(monitorCenterY - height / 2);
    } else if (placement === "top-left") {
      x = monitor.rect.left + padding;
      y = monitor.rect.top + padding;
    } else if (placement === "top-right") {
      x = monitor.rect.right - width - padding;
      y = monitor.rect.top + padding;
    } else if (placement === "bottom-left") {
      x = monitor.rect.left + padding;
      y = monitor.rect.bottom - height - padding;
    } else if (placement === "bottom-right") {
      x = monitor.rect.right - width - padding;
      y = monitor.rect.bottom - height - padding;
    } else {
      x = Math.round(monitorCenterX - width / 2);
      y = monitor.rect.bottom - height - padding;
    }

    x = Math.max(monitor.rect.left, Math.min(x, monitor.rect.right - width));
    y = Math.max(monitor.rect.top, Math.min(y, monitor.rect.bottom - height));
    return `${Math.round(x)},${Math.round(y)},${width},${height}`;
});

let pendingFrame: string | null = null;
let writingFrame = false;
async function writeFrame(frame: string) {
  pendingFrame = frame;
  if (writingFrame) return;
  writingFrame = true;
  try {
    while (pendingFrame !== null) {
      const current = pendingFrame;
      pendingFrame = null;
      const [left, top, width, height] = current.split(",").map(Number) as [number, number, number, number];
      try {
        await Widget.self.setPosition({ left, top, right: left + width, bottom: top + height });
      } catch (error) {
        console.error("Failed to position flyout", error);
      }
    }
  } finally {
    writingFrame = false;
  }
}
$effect.root(() => {
  $effect(() => {
    if (position === null) return;
    void writeFrame(position);
  });

  $effect(() => {
    Widget.self.window.setIgnoreCursorEvents(!showing);
    if (showing) {
      invoke(SeelenCommand.SetSelfZOrder, { zOrder: ZOrder.Top });
    }
  });
});

export const RendererState = {
  observeContent,
  get all() {
    return displayMonitors;
  },
  get primary() {
    return primaryMonitor;
  },
  get showing() {
    return showing;
  },
  set showing(value: boolean) {
    showing = value;
  },
};
