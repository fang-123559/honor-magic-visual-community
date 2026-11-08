import * as esbuild from "esbuild-wasm";
await esbuild.build({ entryPoints: ["config-probe-source.js"], outfile: "../seelen-config-probe/index.js", bundle: true, format: "esm", minify: true, platform: "browser" });
