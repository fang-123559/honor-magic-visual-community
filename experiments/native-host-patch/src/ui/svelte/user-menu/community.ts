import { invoke, SeelenCommand } from "@seelen-ui/lib";

export interface CommunityMenuState {
  version: string;
  honorSignedIn: boolean | null;
  accountAvailable: boolean;
}

export async function communityRequest(kind: "state"): Promise<CommunityMenuState>;
export async function communityRequest(kind: "account" | "feedback" | "exit"): Promise<void>;
export async function communityRequest(kind: "state" | "account" | "feedback" | "exit"): Promise<CommunityMenuState | void> {
  const result = JSON.parse(await invoke(SeelenCommand.MagicCommunityMenuRequest, { kind }));
  if (kind !== "state") return;
  if (typeof result.version !== "string" || !/^0\.2\.0-preview\.\d+$/.test(result.version)
    || ![true, false, null].includes(result.honorSignedIn) || typeof result.accountAvailable !== "boolean") {
    throw new Error("Invalid community menu state");
  }
  return result;
}
