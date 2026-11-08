// Uses the existing SDK visibility attribute. It never changes window/focus state.
export function observeVisibleClock({ element, onTick, period = 250,
  now = Date.now, Observer = MutationObserver, schedule = setInterval, cancel = clearInterval }: {
  element: Element; onTick: (timestamp: number) => void; period?: number;
  now?: () => number; Observer?: typeof MutationObserver;
  schedule?: typeof setInterval; cancel?: typeof clearInterval;
}) {
  let timer: ReturnType<typeof setInterval> | null = null;
  let disposed = false;
  const sync = () => {
    if (disposed) return;
    const visible = !element.hasAttribute("data-widget-hidden");
    if (visible && timer === null) {
      onTick(now());
      timer = schedule(() => onTick(now()), period);
    } else if (!visible && timer !== null) {
      cancel(timer);
      timer = null;
    }
  };
  const observer = new Observer(sync);
  observer.observe(element, { attributes: true, attributeFilter: ["data-widget-hidden"] });
  sync();
  return () => {
    disposed = true;
    observer.disconnect();
    if (timer !== null) cancel(timer);
    timer = null;
  };
}
