// Measure rendered group widths in CSS pixels, independent of monitor DPI.
// The groups keep intrinsic widths in both modes, avoiding resize feedback loops.
export function requiresCompactLayout(available: number, left: number, center: number, right: number) {
  return 2 * Math.max(left, right) + center > available + 0.5;
}

export function installResponsiveToolbar(bar: HTMLElement) {
  let frame = 0;
  let disposed = false;
  let groups: HTMLElement[] = [];
  function update() {
    frame = 0;
    if (disposed || groups.length !== 3) return;
    const style = getComputedStyle(bar);
    const available = bar.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    const [left, center, right] = groups.map(group => group.getBoundingClientRect().width);
    bar.dataset.compact = String(requiresCompactLayout(available, left!, center!, right!));
  }
  function schedule() {
    if (!frame && !disposed) frame = requestAnimationFrame(update);
  }
  const observer = new ResizeObserver(schedule);
  // Attachments may run before child components have finished mounting.
  queueMicrotask(() => {
    if (disposed) return;
    groups = [...bar.querySelectorAll<HTMLElement>(".ft-bar-container")];
    observer.observe(bar);
    for (const group of groups) observer.observe(group);
    update();
  });
  return () => {
    disposed = true;
    observer.disconnect();
    if (frame) cancelAnimationFrame(frame);
    delete bar.dataset.compact;
  };
}
