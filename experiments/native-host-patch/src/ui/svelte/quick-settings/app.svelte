<script lang="ts">
  import { invoke, SeelenCommand, Widget } from "@seelen-ui/lib";
  import type { WidgetId } from "@seelen-ui/lib/types";
  import { Icon } from "libs/ui/svelte/components/Icon";
  import BrightnessControl from "./components/BrightnessControl.svelte";
  import MediaDevices from "./components/MediaDevices.svelte";
  import RadioButtons from "./components/RadioButtons.svelte";
  import { t } from "./i18n";
  import { flushSync, onMount } from "svelte";
  import PowerPanel from "./components/PowerPanel.svelte";
  import {powerPanel} from "./power-state.svelte";
  import { CssHandled } from "libs/ui/svelte/utils/animations";
  let powerView=$state(false);
  const animationsEnabled=()=>document.documentElement.dataset.performanceMode!=='Extreme'
    && !matchMedia('(prefers-reduced-motion: reduce)').matches;
  onMount(()=>{
    void powerPanel.start();
    return Widget.self.onTrigger(({customArgs})=>{
      // Commit the selected content before the SDK's async position/show resumes.
      flushSync(()=>{powerView=customArgs?.view==='power';});
      if(powerView)void powerPanel.refresh();
    });
  });

  function openPowerMenu() {
    invoke(SeelenCommand.TriggerWidget, {
      payload: { id: "@seelen/power-menu" as WidgetId },
    });
  }

  $effect(() => {
    Widget.getCurrent().ready({ show: false });
  });
</script>

<div class={["slu-std-popover", "quick-settings"]}>
  <div class="magic-power-views">
  {#key powerView}
  <!-- Keep only the incoming animation. An outro retains the old reactive branch
       and can paint it above/through the newly selected page during a switch. -->
  <div class="magic-power-view" in:CssHandled={{duration:150,enabled:animationsEnabled}}>
  {#if powerView}
  <PowerPanel />
  {:else}
  <RadioButtons />
  <BrightnessControl />
  <MediaDevices />
  {/if}
  </div>
  {/key}
  </div>

  <div class="quick-settings-footer">
    <button data-skin="transparent" onclick={()=>{powerView=!powerView;if(powerView)void powerPanel.refresh();}} title={powerView?'返回快速设置':'电量与性能'}>{powerView?'返回':'电量与性能'}</button>

    <button data-skin="transparent" onclick={openPowerMenu} title={$t("power")}>
      <Icon iconName="IoPower" />
    </button>
  </div>
</div>
