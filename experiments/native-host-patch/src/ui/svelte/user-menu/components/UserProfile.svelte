<script lang="ts">
  import type { User } from "@seelen-ui/lib/types";
  import { convertFileSrc } from "@tauri-apps/api/core";
  import { t } from "../i18n";

  interface Props {
    user: User;
    honorSignedIn: boolean | null;
  }

  let { user, honorSignedIn }: Props = $props();
  let initial = $derived(Array.from(user.name.trim())[0]?.toLocaleUpperCase() || "?");
</script>

<div class="user-profile-container">
  <div class="user-profile-picture-container" aria-hidden="true">
    {#if user.profilePicturePath}
      <img class="user-profile-picture" src={convertFileSrc(user.profilePicturePath)} alt="" />
    {:else}
      <div class="user-profile-picture-fallback">{initial}</div>
    {/if}
  </div>
  <div class="user-profile-information">
    <div class="user-profile-name" title={user.name}>{user.name}</div>
    <div class="user-profile-account-status">
      {$t(honorSignedIn === true ? "magic_menu.signed_in" : honorSignedIn === false ? "magic_menu.signed_out" : "magic_menu.account_unknown")}
    </div>
  </div>
</div>
