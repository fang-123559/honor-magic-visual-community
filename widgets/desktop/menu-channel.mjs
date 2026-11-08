// Click-driven, scoped to this running desktop session. No timers or polling.
export function menuChannel({sessionId, monitorId, dismiss, create = name => new BroadcastChannel(name)}) {
  let channel;
  try {
    channel = create('magic-desktop-menu-' + sessionId);
    channel.onmessage = ({data}) => {
      if (data?.sessionId === sessionId && typeof data.source === 'string' && data.source !== monitorId && data.type === 'dismiss') dismiss();
    };
  } catch { /* Native menu ownership still uses the single broker instance. */ }
  return {claim() {channel?.postMessage({type:'dismiss',sessionId,source:monitorId});}, close() {channel?.close();}};
}
