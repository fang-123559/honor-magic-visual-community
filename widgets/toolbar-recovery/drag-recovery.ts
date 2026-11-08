/** Candidate only: cancel the manager's active operation instead of forcing icon CSS.
 * No timer, background loop, extra view, item reordering, or popup focus changes.
 */
interface RecoveryManager {
  dragOperation: {status: {dragging: boolean; initializing: boolean}};
  actions: {stop(args: {event: Event; canceled: boolean}): void};
}

export function installDragRecovery(
  manager: RecoveryManager,
  onRecovery: () => void = () => {},
  target: Window = window,
  doc: Document = document,
  hasVisualDrag: () => boolean = () => false,
): () => void {
  let disposed = false;
  const cancel = (event: Event) => {
    if (disposed) return;
    const active = manager.dragOperation.status.dragging || manager.dragOperation.status.initializing;
    if (!active && !hasVisualDrag()) return;
    if (active) manager.actions.stop({event, canceled: true});
    onRecovery();
  };
  const visibility = (event: Event) => { if (doc.visibilityState === 'hidden') cancel(event); };
  const releasedMove = (event: PointerEvent) => {
    // Secondary contacts must not cancel a drag controlled by the primary pointer.
    if (event.isPrimary !== false && event.buttons === 0) cancel(event);
  };
  target.addEventListener('blur', cancel);
  target.addEventListener('pointercancel', cancel);
  target.addEventListener('pointermove', releasedMove, {passive: true});
  doc.addEventListener('visibilitychange', visibility);
  return () => {
    if (disposed) return;
    disposed = true;
    target.removeEventListener('blur', cancel);
    target.removeEventListener('pointercancel', cancel);
    target.removeEventListener('pointermove', releasedMove);
    doc.removeEventListener('visibilitychange', visibility);
  };
}
