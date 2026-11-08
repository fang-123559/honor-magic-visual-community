import { mount } from "svelte";
import App from "./app.svelte";
import { Widget } from "@seelen-ui/lib";
import { RendererState } from "./state/placement.svelte";

import "@seelen-ui/lib/styles/reset.css";

const root = document.getElementById("root")!;

// Placement owns both size and position. The generic auto-sizer also moves the
// window and can overwrite centering with a position captured before a resize.
await Widget.self.init();
await Widget.self.window.setResizable(false);

mount(App, {
  target: root,
});
RendererState.observeContent(root);
