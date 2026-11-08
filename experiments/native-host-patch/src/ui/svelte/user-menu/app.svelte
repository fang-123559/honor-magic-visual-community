<script lang="ts">
  import { onMount } from "svelte";
  import { globalState } from "./state/mod.svelte";
  import { invoke, SeelenCommand, Widget } from "@seelen-ui/lib";
  import Icon from "libs/ui/svelte/components/Icon/Icon.svelte";
  import type { IconName } from "libs/ui/icons";
  import { t } from "./i18n";
  import UserProfile from "./components/UserProfile.svelte";
  import { communityRequest, type CommunityMenuState } from "./community";

  type Action = "account" | "settings" | "sleep" | "restart" | "shutdown" | "lock" | "update" | "feedback" | "about" | "exit";
  const groups: Array<Array<{ action: Action; icon: IconName }>> = [
    [{ action: "account", icon: "TbUserCircle" }, { action: "settings", icon: "TbSettings" }],
    [{ action: "sleep", icon: "TbMoon" }, { action: "restart", icon: "TbRefresh" }, { action: "shutdown", icon: "TbPower" }, { action: "lock", icon: "TbLock" }],
    [{ action: "update", icon: "TbArrowUp" }, { action: "feedback", icon: "TbPencil" }, { action: "about", icon: "TbInfoCircle" }],
    [{ action: "exit", icon: "TbLogout" }],
  ];

  let menu: HTMLDivElement;
  let community = $state<CommunityMenuState | null>(null);
  let busy = $state(false);
  let status = $state("");
  let statusError = $state(false);
  let armed = $state<"restart" | "shutdown" | null>(null);
  let expires = 0;
  let confirmTimer: ReturnType<typeof setTimeout> | undefined;
  let generation = 0;

  function resetConfirmation() {
    clearTimeout(confirmTimer);
    armed = null;
    expires = 0;
  }

  function showStatus(message: string, error = false) {
    status = message;
    statusError = error;
  }

  async function refresh() {
    const ticket = ++generation;
    community = null;
    showStatus("");
    try {
      const value = await communityRequest("state");
      if (ticket === generation) community = value;
    } catch (error) {
      if (ticket === generation) showStatus($t("magic_menu.adapter_unavailable"), true);
      console.warn("Community menu adapter unavailable", error);
    }
  }

  onMount(() => {
    const widget = Widget.self;
    void widget.ready({ show: false });
    let disposed = false;
    let receivedFocusEvent = false;
    let unlisten: (() => void) | undefined;
    void widget.window.onFocusChanged(({ payload }) => {
      receivedFocusEvent = true;
      if (!payload) {
        generation++;
        resetConfirmation();
        return;
      }
      void refresh();
      menu?.querySelector<HTMLButtonElement>("button:enabled")?.focus({ preventScroll: true });
    }).then((release) => { if (disposed) release(); else unlisten = release; });
    void widget.window.isVisible().then((visible) => { if (visible && !disposed && !receivedFocusEvent) void refresh(); });
    return () => { disposed = true; generation++; unlisten?.(); resetConfirmation(); };
  });

  function unavailable(action: Action) {
    return busy || action === "settings" || (["account", "feedback", "exit"].includes(action) && !community)
      || (action === "account" && !community?.accountAvailable);
  }

  async function activate(action: Action) {
    if (unavailable(action)) return;
    if (action === "restart" || action === "shutdown") {
      if (armed !== action || Date.now() >= expires) {
        resetConfirmation();
        armed = action;
        expires = Date.now() + 3_000;
        confirmTimer = setTimeout(resetConfirmation, 3_000);
        return;
      }
    }
    resetConfirmation();
    if (action === "update") {
      showStatus($t("magic_menu.update_unconfigured", { version: community?.version || "—" }));
      return;
    }
    if (action === "about") {
      showStatus($t("magic_menu.about_text", { version: community?.version || "—" }));
      return;
    }
    const actionGeneration = generation;
    busy = true;
    try {
      if (action === "account" || action === "feedback" || action === "exit") await communityRequest(action);
      else if (action === "sleep") await invoke(SeelenCommand.Suspend);
      else if (action === "lock") await invoke(SeelenCommand.Lock);
      else if (action === "restart") await invoke(SeelenCommand.Restart, { installUpdates: false });
      else if (action === "shutdown") await invoke(SeelenCommand.Shutdown, { installUpdates: false });
      if (generation === actionGeneration) await Widget.self.hide();
    } catch (error) {
      if (generation === actionGeneration) showStatus($t("magic_menu.action_failed"), true);
      console.warn("Community menu action failed", action, error);
    } finally {
      busy = false;
    }
  }

  function onKeyDown(event: KeyboardEvent) {
    if (event.repeat && (event.key === "Enter" || event.key === " ")) {
      event.preventDefault();
      return;
    }
    if (event.key === "Escape") {
      event.preventDefault();
      if (armed) resetConfirmation(); else void Widget.self.hide();
      return;
    }
    if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
    event.preventDefault();
    const buttons = Array.from(menu.querySelectorAll<HTMLButtonElement>("button:enabled"));
    const current = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1
      : (current + (event.key === "ArrowUp" ? -1 : 1) + buttons.length) % buttons.length;
    buttons[next]?.focus();
    resetConfirmation();
  }
</script>

<div bind:this={menu} class="slu-std-popover user-popup" role="menu" tabindex="-1" aria-label={$t("magic_menu.title")} onkeydown={onKeyDown}>
  <span class="user-label">{$t("magic_menu.title")}</span>
  <UserProfile user={globalState.user} honorSignedIn={community?.honorSignedIn ?? null} />
  {#each groups as group, index}
    <hr />
    <div class="user-menu-items" role="group" aria-label={$t("magic_menu.group_" + index)}>
      {#each group as row}
        <button
          data-skin="transparent"
          class="menu-item"
          class:menu-danger={row.action === "exit"}
          class:menu-confirming={armed === row.action}
          role="menuitem"
          disabled={unavailable(row.action)}
          title={row.action === "settings" ? $t("magic_menu.development") : row.action === "account" ? $t("magic_menu.account_hint") : undefined}
          onclick={() => activate(row.action)}
          onblur={() => { if (armed === row.action) resetConfirmation(); }}
        >
          <Icon iconName={row.icon} />
          <span class="menu-item-title">{$t(armed === row.action ? "magic_menu.confirm_" + row.action : "magic_menu." + row.action)}</span>
          {#if row.action === "settings"}<span class="menu-item-meta">{$t("magic_menu.development")}</span>{/if}
          {#if row.action === "about"}<span class="menu-item-meta">{community?.version || "—"}</span>{/if}
        </button>
      {/each}
    </div>
  {/each}
  {#if status}<p class="user-menu-status" class:menu-error={statusError} role="status">{status}</p>{/if}
</div>
