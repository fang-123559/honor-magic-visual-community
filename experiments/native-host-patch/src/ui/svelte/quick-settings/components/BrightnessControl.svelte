<script lang="ts">
  import { state } from "../state.svelte";
  import MonitorBrightness from "./MonitorBrightness.svelte";
</script>

{#each state.monitors as monitor (monitor.id)}
  {#if monitor.brightness != null}
    <MonitorBrightness {monitor} />
  {/if}
{/each}
