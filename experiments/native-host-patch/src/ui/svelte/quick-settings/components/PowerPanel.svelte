<script lang="ts">
  import { invoke, SeelenCommand } from "@seelen-ui/lib";
  import { Icon } from "libs/ui/svelte/components/Icon";
  import { t } from "../i18n";
  import { powerPanel } from "../power-state.svelte";

  const snapshot = $derived(powerPanel.snapshot);
  const percent = $derived(snapshot.percent);
  const batteries = $derived(snapshot.batteries ?? []);
  let openingSettings = $state(false);
  let settingsFailed = $state(false);

  async function openSettings() {
    if (openingSettings) return;
    openingSettings = true;
    settingsFailed = false;
    try {
      await invoke(SeelenCommand.OpenPowerSettings);
    } catch (error) {
      settingsFailed = true;
      console.error("Failed to open Windows power settings", error);
    } finally {
      openingSettings = false;
    }
  }
</script>

<section class="magic-power-panel" aria-label={$t("power_panel.title")}>
  <header class="magic-power-header">
    <h2>{$t("power_panel.title")}</h2>
    <button
      data-skin="transparent"
      onclick={() => powerPanel.refresh()}
      disabled={powerPanel.refreshing}
    >
      <Icon iconName="IoRefresh" aria-hidden="true" />
      {powerPanel.refreshing ? $t("power_panel.refreshing") : $t("power_panel.refresh")}
    </button>
  </header>

  <div class="magic-power-hero">
    <div class="magic-power-summary">
      <strong>{percent === null ? "—" : `${percent}%`}</strong>
      <span class="magic-power-badge">{snapshot.supply}</span>
    </div>
    <div
      class="magic-power-gauge"
      role="progressbar"
      aria-label={$t("power_panel.gauge_label")}
      aria-valuemin="0"
      aria-valuemax="100"
      aria-valuenow={percent === null ? undefined : percent}
      aria-valuetext={percent === null ? $t("power_panel.unavailable") : `${percent}%`}
    >
      <div class="magic-power-gauge-fill" style:width={`${percent ?? 0}%`}></div>
    </div>
  </div>

  <section class="magic-power-group" aria-label={$t("power_panel.modes")}>
    <h3>{$t("power_panel.modes")}</h3>
    <dl>
      <div><dt>{$t("power_panel.windows_mode")}</dt><dd>{snapshot.modeText}</dd></div>
      <div><dt>{$t("power_panel.saver")}</dt><dd>{snapshot.saver}</dd></div>
      <div class="magic-power-unavailable">
        <dt>{$t("power_panel.honor_mode")}</dt><dd>{$t("power_panel.unavailable")}</dd>
      </div>
    </dl>
  </section>

  {#each batteries as battery, index}
    <section class="magic-power-group">
      <h3>
        {batteries.length > 1
          ? $t("power_panel.battery_info_numbered", { number: String(index + 1) })
          : $t("power_panel.battery_info")}
      </h3>
      <dl>
        <div><dt>{$t("power_panel.battery_model")}</dt><dd>{battery.model || $t("power_panel.unavailable")}</dd></div>
        <div><dt>{$t("power_panel.battery_health")}</dt><dd>{snapshot.health(battery)}</dd></div>
        <div><dt>{$t("power_panel.cycles")}</dt><dd>{battery.cycleCount ?? $t("power_panel.unavailable")}</dd></div>
      </dl>
    </section>
  {/each}

  <p class="magic-power-note">
    <Icon iconName="IoInformationCircleOutline" aria-hidden="true" />
    <span>{$t("power_panel.note")}</span>
  </p>
  <button data-skin="default" class="magic-power-action" onclick={openSettings} disabled={openingSettings}>
    <span>{$t("power_panel.settings")}</span>
    <Icon iconName="IoChevronForward" aria-hidden="true" />
  </button>
  {#if settingsFailed}
    <p role="alert">{$t("power_panel.settings_failed")}</p>
  {/if}
</section>
