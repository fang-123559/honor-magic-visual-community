<script lang="ts">
  import { onDestroy } from "svelte";
  import { invoke, SeelenCommand } from "@seelen-ui/lib";
  import type { PhysicalMonitor } from "@seelen-ui/lib/types";
  import { Icon } from "libs/ui/svelte/components/Icon";
  import { brightnessIcon } from "libs/ui/utils";
  import { brightnessEdit } from "libs/ui/svelte/utils/brightness-edit";
  import { brightnessFeedback } from "libs/ui/svelte/utils/brightness-feedback";

  let { monitor }: { monitor: PhysicalMonitor } = $props();
  let draft = $state<number | null>(null);
  const value = $derived(draft ?? monitor.brightness ?? 0);
  // Both parents key this component by monitor.id.
  // svelte-ignore state_referenced_locally
  const announce = brightnessFeedback(monitor.id);
  const edit = brightnessEdit(
    brightness => invoke(SeelenCommand.SetMonitorBrightness, { id: monitor.id, brightness }),
    next => { draft = next; announce(next); },
    error => console.error("Failed to set monitor brightness", error),
  );
  const rangeKeys = new Set(["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "PageUp", "PageDown"]);
  onDestroy(() => { edit.dispose(); announce(null); });
</script>

<svelte:window onpointerup={edit.end} onpointercancel={edit.end} onblur={() => edit.blur()} />
<svelte:document onvisibilitychange={() => { if (document.hidden) edit.end(); }} />
<span class="quick-settings-label">{monitor.name}</span>
<div class="quick-settings-item">
  <button data-skin="transparent">
    <Icon iconName={brightnessIcon(value)} />
  </button>
  <input
    type="range"
    data-skin="flat"
    aria-label={monitor.name}
    {value}
    onpointerdown={event => edit.begin(value, event)}
    onlostpointercapture={edit.end}
    onkeydown={e => { if (rangeKeys.has(e.key)) edit.begin(value); }}
    onkeyup={e => { if (rangeKeys.has(e.key)) edit.end(); }}
    onblur={() => edit.blur()}
    oninput={e => edit.input(Number(e.currentTarget.value))}
    min={0}
    max={100}
  />
</div>
