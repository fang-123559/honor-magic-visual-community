import { emit } from "@tauri-apps/api/event";

export const brightnessFeedbackEvent = "magic-brightness-edit";
export interface BrightnessFeedback {
  id: string;
  source: string;
  sequence: number;
  value: number | null;
}

// Share local intent with the existing flyout webview. No hardware write,
// new webview, polling or permission change is involved in this signal.
export function brightnessFeedback(id: string) {
  const source = crypto.randomUUID();
  let sequence = 0;
  let lastSent = -Infinity;
  let queued: number | undefined;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const publish = (value: number | null) => {
    lastSent = performance.now();
    void emit(brightnessFeedbackEvent, { id, source, sequence: ++sequence, value })
      .catch(error => console.error("Failed to publish brightness feedback", error));
  };
  return (value: number | null) => {
    if (value === null) {
      clearTimeout(timer);
      timer = undefined;
      queued = undefined;
      publish(null);
      return;
    }
    queued = value;
    if (timer !== undefined) return;
    const delay = Math.max(0, 50 - (performance.now() - lastSent));
    if (delay === 0) { queued = undefined; publish(value); return; }
    // Bound cross-webview IPC to 20 updates/s during an active burst.
    timer = setTimeout(() => {
      timer = undefined;
      const latest = queued;
      queued = undefined;
      if (latest !== undefined) publish(latest);
    }, delay);
  };
}
