# Native toolbar and Dock reference assets

Generated from the existing locked react-icons package (5.7.0). Icon geometry is unchanged; native rendering resolves currentColor from the Windows theme.

- Font Awesome 5: CC BY 4.0 License; https://creativecommons.org/licenses/by/4.0/; https://fontawesome.com/
- Font Awesome 6: CC BY 4.0 License; https://creativecommons.org/licenses/by/4.0/; https://fontawesome.com/
- Ionicons 4: MIT; https://github.com/ionic-team/ionicons/blob/master/LICENSE; https://ionicons.com/
- Ionicons 5: MIT; https://github.com/ionic-team/ionicons/blob/master/LICENSE; https://ionicons.com/
- Material Design icons: Apache License Version 2.0; https://github.com/google/material-design-icons/blob/master/LICENSE; http://google.github.io/material-design-icons/
- Github Octicons icons: MIT; https://github.com/primer/octicons/blob/master/LICENSE; https://octicons.github.com/
- Lucide: ISC; https://github.com/lucide-icons/lucide/blob/main/LICENSE; https://lucide.dev/
- Game Icons: CC BY 3.0; https://creativecommons.org/licenses/by/3.0/; https://game-icons.net/
- Ant Design Icons: MIT; https://opensource.org/licenses/MIT; https://github.com/ant-design/ant-design-icons
- Bootstrap Icons: MIT; https://opensource.org/licenses/MIT; https://github.com/twbs/icons
- Remix Icon: Apache License Version 2.0; http://www.apache.org/licenses/; https://github.com/Remix-Design/RemixIcon
- Heroicons: MIT; https://opensource.org/licenses/MIT; https://github.com/tailwindlabs/heroicons
- Heroicons 2: MIT; https://opensource.org/licenses/MIT; https://github.com/tailwindlabs/heroicons
- BoxIcons: MIT; https://github.com/atisawd/boxicons/blob/master/LICENSE; https://github.com/atisawd/boxicons
- css.gg: MIT; https://opensource.org/licenses/MIT; https://github.com/astrit/css.gg
- VS Code Icons: CC BY 4.0; https://creativecommons.org/licenses/by/4.0/; https://github.com/microsoft/vscode-codicons
- Tabler Icons: MIT; https://opensource.org/licenses/MIT; https://github.com/tabler/tabler-icons
- Phosphor Icons: MIT; https://github.com/phosphor-icons/core/blob/main/LICENSE; https://github.com/phosphor-icons/core
- Icons8 Line Awesome: MIT; https://github.com/icons8/line-awesome/blob/master/LICENSE.md; https://icons8.com/line-awesome

Community battery icons: profiles/power-panel/0.1.2/template.js, SHA256 a41f362e33dd858b26038d4715690487ed2e308aad6091e20f8e810985005d55.

Bluetooth classification table: libs/ui/svelte/utils/bluetoothIcons.ts, SHA256 c3cdac234f164a172c7a52467f1c927b1b04ef4deb9fd16c5d5590017ac05a85. The JSON tables are generated from these same reference dictionaries.

Regenerate with: node tools/dev/export-toolbar-reference.mjs

Dock default SVGs and toolbar/Dock translations are copied from the bundled Seelen source. See assets/native-shell/licenses/Seelen-LICENSE. Calendar locale data is copied from the locked Moment package; see assets/native-shell/licenses/moment-LICENSE.

All rendering assets are committed as ordinary files and embedded native headers. Attribution URLs above are references, not runtime asset dependencies. Normal regeneration reads only assets/native-shell; --refresh-reference explicitly imports the locked reference again.

Community modification (2026-11-04): Japanese product-name strings now identify Magic Community. Original translation and asset attribution above is retained; icon geometry and license files are unchanged.
