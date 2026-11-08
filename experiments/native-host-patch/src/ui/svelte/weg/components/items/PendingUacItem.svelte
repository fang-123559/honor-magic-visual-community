<script lang="ts">
  import type { PendingUacRequest } from "@seelen-ui/lib/types";
  import Icon from "libs/ui/svelte/components/Icon/Icon.svelte";
  import { t } from "../../i18n/index.ts";
  import { settingsState } from "../../state/settings.svelte.ts";
  import { uacState } from "../../state/uac.svelte.ts";

  let { request }: { request: PendingUacRequest } = $props();
  const label = $derived(uacState.failed === request.id ? $t("uac.retry") : $t("uac.pending"));
  const tooltip = $derived(`${request.title || $t("uac.pending")}\n${$t("uac.description")}`);
</script>

<div class="weg-item-overlay weg-uac-entry">
  <button
    type="button"
    class="weg-item weg-item-medium"
    class:weg-uac-failed={uacState.failed === request.id}
    aria-label={`${$t("uac.open")}: ${request.title || $t("uac.pending")}`}
    aria-busy={uacState.opening === request.id}
    disabled={uacState.opening !== null}
    data-tooltip={tooltip}
    data-tooltip-origin-y={settingsState.tooltipOrigin.y}
    data-tooltip-origin-x={settingsState.tooltipOrigin.x}
    data-tooltip-align-x={settingsState.popupAlignX}
    data-tooltip-align-y={settingsState.popupAlignY}
    onclick={() => uacState.open(request.id)}
    oncontextmenu={(event) => event.stopPropagation()}
  >
    <Icon name="TbShieldLock" class="weg-item-icon" />
    <span class="weg-uac-label" role="status">{label}</span>
  </button>
  <div class="weg-item-open-sign weg-item-open-sign-active weg-item-open-sign-attention"></div>
</div>
