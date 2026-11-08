# Seelen P0 compatibility probe

Application target: Seelen UI v2.8.9 / commit 507378aa01190617c18ee2c70ecc66b316169437.

The matching source SDK declares 2.8.9, but the npm registry observed on 2026-10-14 publishes latest 2.8.4; requesting 2.8.9 returns 404. This experiment explicitly pins SDK 2.8.4 to test compatibility. It does not silently treat that SDK as 2.8.9. If incompatible, build the SDK from the fixed application source instead. Runtime compatibility is not yet verified.

Build with project-local npm cache and `npm install --ignore-scripts --no-audit --no-fund`, then `npm run build`. Set `P0_FIXTURE_COUNT=100` before building the synthetic-content case; default is 0. The WebAssembly bundler avoids a binary-install postinstall script.

The widget uses public Widget init/ready methods, requires a real monitor ID, does no desktop hiding or file operations, and cannot be accepted through an ordinary browser. `P0_WIDGET_READY` is emitted only after the real SDK ready call succeeds. It uses synthetic items; the default sample is empty.

The compatibility probe now queries the real ConnectedMonitorList and positions each instance on its assigned monitor. The panel is 320x120 CSS pixels for the empty case or 320x752 for 100 labels; physical dimensions use the reported monitor scale. Compare actual HWND rectangles and devicePixelRatio at runtime before claiming equivalence to WPF.

Load only after a suitable Seelen test session has been arranged. Do not start the full application merely because this frontend bundles successfully: its normal startup creates a scheduled service task and launches an elevated helper.

Runtime status: not_run. This is a diagnostic compatibility probe, not a completed organizer or accepted integration.
