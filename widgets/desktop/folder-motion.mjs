// Animate one existing dialog only. Geometry is read on open/close, never polled.
export function folderTransitionFrames(source, target) {
  const valid = rect => rect && [rect.left, rect.top, rect.width, rect.height].every(Number.isFinite) && rect.width > 0 && rect.height > 0;
  const start = valid(source) && valid(target)
    ? `translate(${source.left + source.width / 2 - target.left - target.width / 2}px, ${source.top + source.height / 2 - target.top - target.height / 2}px) scale(${source.width / target.width}, ${source.height / target.height})`
    : "translate(0px, 0px) scale(.96)";
  return [{ opacity: .35, transform: start }, { opacity: 1, transform: "translate(0px, 0px) scale(1)" }];
}

export function folderMotion(dialog, reduced = () => globalThis.matchMedia?.("(prefers-reduced-motion: reduce)").matches ?? false) {
  let animation = null, sequence = 0, anchor = null;
  const stop = () => { sequence++; animation?.cancel(); animation = null; };
  const currentFrame = () => {
    const style = animation && dialog.ownerDocument?.defaultView?.getComputedStyle(dialog);
    return style ? { opacity: style.opacity, transform: style.transform } : null;
  };
  const frames = () => {
    const source = typeof anchor === "function" ? anchor() : anchor;
    return folderTransitionFrames(source?.getBoundingClientRect?.() ?? source, dialog.getBoundingClientRect?.());
  };
  const play = (keyframes, duration, easing) => {
    const value = dialog.animate(keyframes, { duration, easing });
    animation = value;
    value.finished.then(() => { if (animation === value) { value.cancel(); animation = null; } }, () => {});
    return value;
  };
  return {
    open(source = null) {
      const current = currentFrame();
      stop(); anchor = source; if (!dialog.open) dialog.showModal();
      if (!reduced() && typeof dialog.animate === "function") {
        const keyframes = frames();
        if (current) keyframes[0] = current;
        play(keyframes, 180, "cubic-bezier(.2,.8,.2,1)");
      }
    },
    async close(immediate = false) {
      const currentFrameValue = currentFrame();
      stop(); if (!dialog.open) return;
      const current = sequence;
      if (!immediate && !reduced() && typeof dialog.animate === "function") {
        const keyframes = frames().reverse();
        if (currentFrameValue) keyframes[0] = currentFrameValue;
        try { await play(keyframes, 140, "cubic-bezier(.4,0,1,1)").finished; } catch { return; }
      }
      if (current === sequence) { dialog.close(); animation = null; anchor = null; }
    },
    cancel: stop,
  };
}
