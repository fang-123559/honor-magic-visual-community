/** A new trigger invalidates blur work belonging to the previous opening. */
export class PopupFocusLoss {
  private revision = 0;
  private opening = false;
  private focused = false;

  begin(): number {
    this.opening = true;
    this.focused = false;
    return ++this.revision;
  }

  end(revision: number): void {
    if (revision === this.revision) this.opening = false;
  }

  focusChanged(focused: boolean): number | null {
    if (focused) {
      this.focused = true;
      return null;
    }
    if (this.opening || !this.focused) return null;
    this.focused = false;
    return this.revision;
  }

  canHide(revision: number): boolean {
    return revision === this.revision && !this.opening && !this.focused;
  }
}
