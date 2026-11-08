/** One resource subscription shared by all mounted consumers in one webview.
 * Backend counts connections, so another screen can independently retain it.
 * No timer/retry loop: transport reconnect belongs to the existing events channel.
 */
export class ResourceSubscription<T> {
  private users = 0;
  private generation = 0;
  private unsubscribe: (() => void) | null = null;

  constructor(private readonly api: {
    subscribe: (receive: (value: T) => void) => Promise<() => void>;
    snapshot: () => Promise<T>;
    receive: (value: T) => void;
    reset: () => void;
    error: (error: unknown) => void;
  }) {}

  retain(): () => void {
    if (++this.users === 1) void this.start(++this.generation);
    let released = false;
    return () => {
      if (released) return;
      released = true;
      if (--this.users !== 0) return;
      ++this.generation;
      this.unsubscribe?.();
      this.unsubscribe = null;
      this.api.reset();
    };
  }

  private async start(generation: number): Promise<void> {
    const active = () => this.users > 0 && this.generation === generation;
    let revision = 0;
    try {
      // Await the subscription ACK before asking for a snapshot. An event that
      // arrives during the snapshot must win over that older response.
      const unsubscribe = await this.api.subscribe((value) => {
        if (!active()) return;
        ++revision;
        this.api.receive(value);
      });
      if (!active()) {
        unsubscribe();
        return;
      }
      this.unsubscribe = unsubscribe;
      const before = revision;
      const snapshot = await this.api.snapshot();
      if (active() && revision === before) this.api.receive(snapshot);
    } catch (error) {
      if (active()) this.api.error(error);
    }
  }
}
