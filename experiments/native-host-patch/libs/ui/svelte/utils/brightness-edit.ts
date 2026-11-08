// One writer per monitor. Keep system echoes from replacing an active edit.
// Timers exist only during an edit/write burst; idle controls do no work.
export function brightnessEdit(
  write: (value: number) => Promise<unknown>,
  display: (value: number | null) => void,
  failed: (error: unknown) => void,
) {
  let editing = false;
  let hasDraft = false;
  let disposed = false;
  let writing = false;
  let queued: number | null = null;
  let lastWrite = -Infinity;
  let sendTimer: ReturnType<typeof setTimeout> | undefined;
  let releaseTimer: ReturnType<typeof setTimeout> | undefined;
  let pointer: { target: Element; id: number } | null = null;

  function releasePointer() {
    const captured = pointer;
    pointer = null;
    if (captured?.target.hasPointerCapture(captured.id)) {
      captured.target.releasePointerCapture(captured.id);
    }
  }

  function cancelRelease() {
    clearTimeout(releaseTimer);
    releaseTimer = undefined;
  }
  function releaseWhenIdle() {
    cancelRelease();
    if (disposed || editing || writing || queued !== null) return;
    // WMI notifications can trail completion of the setter. Retain the final
    // local value across that burst, then resume keyboard/OSD/system updates.
    releaseTimer = setTimeout(() => {
      releaseTimer = undefined;
      hasDraft = false;
      if (!disposed) display(null);
    }, 500);
  }
  function pump() {
    if (disposed || writing || queued === null || sendTimer !== undefined) return;
    const delay = Math.max(0, 100 - (performance.now() - lastWrite));
    if (delay > 0) {
      sendTimer = setTimeout(() => { sendTimer = undefined; pump(); }, delay);
      return;
    }
    const value = queued;
    queued = null;
    writing = true;
    lastWrite = performance.now();
    Promise.resolve().then(() => write(value)).catch(error => {
      if (!disposed) failed(error);
    }).finally(() => {
      writing = false;
      if (disposed) return;
      if (queued !== null) pump();
      else releaseWhenIdle();
    });
  }
  return {
    begin(value: number, event?: PointerEvent) {
      if (disposed) return;
      if (event?.currentTarget instanceof Element) {
        pointer = { target: event.currentTarget, id: event.pointerId };
        // Retain the gesture across transient focus changes and mouse moves
        // outside the slider. The browser still handles the native range drag.
        try { pointer.target.setPointerCapture(pointer.id); } catch { /* Native range capture can own this pointer. */ }
      }
      hasDraft = true; editing = true; cancelRelease(); display(value);
    },
    input(value: number) {
      if (disposed) return;
      hasDraft = true;
      cancelRelease();
      const next = Math.max(0, Math.min(100, value));
      display(next);
      queued = next;
      pump();
    },
    end(event?: Event) {
      if (pointer && event instanceof PointerEvent && event.pointerId !== pointer.id) return;
      releasePointer();
      if (!hasDraft) return;
      editing = false; pump(); releaseWhenIdle();
    },
    blur() {
      // A window/input blur does not end a pointer that is still captured.
      // Releasing here used to expose the old system value after 500 ms, then
      // the next input immediately restored the value under the mouse.
      // Chromium's native range thumb can own capture in the UA shadow tree;
      // hasPointerCapture() on the input then returns false even during a drag.
      // Pointer up/cancel/lostcapture, rather than focus, ends that gesture.
      if (pointer) return;
      this.end();
    },
    dispose() {
      disposed = true;
      releasePointer();
      queued = null;
      clearTimeout(sendTimer);
      cancelRelease();
    },
  };
}
