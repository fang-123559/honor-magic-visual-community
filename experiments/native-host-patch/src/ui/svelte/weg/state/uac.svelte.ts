import { invoke, SeelenCommand, SeelenEvent, subscribe } from "@seelen-ui/lib";
import { type PendingUacRequest, UacActivationOutcome } from "@seelen-ui/lib/types";
import { lazyRune } from "libs/ui/svelte/utils";

const requests = lazyRune<PendingUacRequest[]>(() => invoke(SeelenCommand.GetPendingUacRequests));
let opening = $state<string | null>(null);
let failed = $state<string | null>(null);

// Subscribe before fetching: a request may appear/disappear during Dock startup.
await subscribe(SeelenEvent.PendingUacRequestsChanged, requests.setByPayload);
await requests.init();

class UacState {
  get requests() { return requests.value; }
  get opening() { return opening; }
  get failed() { return failed; }

  async open(id: string): Promise<void> {
    if (opening !== null) return;
    opening = id;
    failed = null;
    try {
      const outcome = await invoke(SeelenCommand.OpenPendingUacRequest, { id });
      if (outcome === UacActivationOutcome.StillPending) failed = id;
      if (outcome === UacActivationOutcome.NoLongerPending) {
        // Also clear a stale UI entry if its lifecycle event was missed. Use the
        // current list and exact generation id, preserving requests arriving during await.
        requests.value = requests.value.filter(request => request.id !== id);
      }
    } catch (error) {
      console.error("Could not open pending Windows permission request", error);
      failed = id;
    } finally {
      opening = null;
    }
  }
}

export const uacState = new UacState();
