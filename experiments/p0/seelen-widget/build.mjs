import { build } from "esbuild-wasm";
const fixtureCount = process.env.P0_FIXTURE_COUNT === "100" ? 100 : 0;
await build({
  entryPoints: ["src/index.js"], outfile: "index.js", bundle: true,
  format: "esm", platform: "browser", target: "es2022", minify: true,
  sourcemap: false, logLevel: "info",
  define: { P0_FIXTURE_COUNT: String(fixtureCount) }
});
