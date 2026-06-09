import { writeFileSync } from "node:fs";
import { genCSS, colors } from "m3-svelte/etc/colors";
import { Hct, SchemeTonalSpot } from "@ktibow/material-color-utilities-nightly";

const src = Hct.fromInt(0xffa36bff); // landing violet #A36BFF
const light = new SchemeTonalSpot(src, false, 0.0);
const dark = new SchemeTonalSpot(src, true, 0.0);

const css = genCSS(light, dark, colors);
const expand = (s) => s.replace(/#([0-9a-f])([0-9a-f])([0-9a-f])(?![0-9a-f])/gi, "#$1$1$2$2$3$3");

function splitSchemeValue(value) {
  const expanded = expand(value.trim());
  const match = expanded.match(/^light-dark\(\s*([^,]+),\s*([^)]+)\s*\)$/);
  if (match) return { light: expand(match[1].trim()), dark: expand(match[2].trim()) };
  return { light: expanded, dark: expanded };
}

function buildSchemeMaps(sourceCss) {
  const lightMap = new Map();
  const darkMap = new Map();
  for (const m of sourceCss.matchAll(/(--m3c-[a-z0-9-]+):\s*([^;]+);/g)) {
    const value = splitSchemeValue(m[2]);
    lightMap.set(m[1], value.light);
    darkMap.set(m[1], value.dark);
  }
  return { lightMap, darkMap };
}

function applyOverrides(map, overrides) {
  for (const [token, value] of overrides) map.set(token, value);
}

function serializeVars(map) {
  return [...map.entries()].map(([k, v]) => `  ${k}: ${v};`).join("\n");
}

const { lightMap, darkMap } = buildSchemeMaps(css);

const darkOverrides = new Map([
  // Keep the M3 accent roles from the violet seed, but use true dark neutrals
  // for surfaces so operational screens do not inherit a rose/lavender wash.
  ["--m3c-surface", "#121217"],
  ["--m3c-surface-dim", "#121217"],
  ["--m3c-surface-bright", "#3a3a42"],
  ["--m3c-surface-container-lowest", "#0d0d11"],
  ["--m3c-surface-container-low", "#1a1a20"],
  ["--m3c-surface-container", "#202026"],
  ["--m3c-surface-container-high", "#2a2a31"],
  ["--m3c-surface-container-highest", "#363640"],
  ["--m3c-on-surface", "#f2f0f7"],
  ["--m3c-on-surface-variant", "#cbc7d2"],
  ["--m3c-outline", "#948f9c"],
  ["--m3c-outline-variant", "#494750"],
  ["--m3c-inverse-surface", "#e7e4ed"],
  ["--m3c-inverse-on-surface", "#303038"],

  // Use tertiary as the cool expressive accent. Warm/coral remains reserved for
  // the error role so neutral app chrome does not drift peach.
  ["--m3c-tertiary", "#8bd8ee"],
  ["--m3c-tertiary-dim", "#72c5dc"],
  ["--m3c-on-tertiary", "#003640"],
  ["--m3c-tertiary-container", "#174f5d"],
  ["--m3c-on-tertiary-container", "#c6f2ff"],
  ["--m3c-tertiary-fixed", "#c6f2ff"],
  ["--m3c-tertiary-fixed-dim", "#8bd8ee"],
  ["--m3c-on-tertiary-fixed", "#001f27"],
  ["--m3c-on-tertiary-fixed-variant", "#174f5d"],
  ["--m3c-tertiary-container-subtle", "#113f49"],
  ["--m3c-on-tertiary-container-subtle", "#a8dce9"]
]);

const lightOverrides = new Map([
  // Light surface ramp ported from Rental Command's proven scheme. The critical
  // property: the card tier (surface-container-low) is BRIGHTER than the page
  // (surface), so white-ish cards pop off a faintly tinted page (Cloud Console
  // feel) instead of reading as grey boxes on white. The m3-svelte TonalSpot
  // defaults invert this (cards darker than page) which is why light looked flat.
  ["--m3c-surface", "#fdf9ff"],
  ["--m3c-surface-dim", "#e4ddee"],
  ["--m3c-surface-bright", "#fffbff"],
  ["--m3c-surface-container-lowest", "#ffffff"],
  ["--m3c-surface-container-low", "#f7f1fb"],
  ["--m3c-surface-container", "#f1eaf7"],
  ["--m3c-surface-container-high", "#ebe3f2"],
  ["--m3c-surface-container-highest", "#e4dbee"],
  ["--m3c-on-surface", "#18181b"],
  ["--m3c-on-surface-variant", "#52525b"],
  ["--m3c-outline", "#71717a"],
  ["--m3c-outline-variant", "#d4d4d8"],

  // Keep the same cool expressive tertiary family in light mode so warm/coral
  // remains reserved for error and validation states.
  ["--m3c-tertiary", "#006878"],
  ["--m3c-tertiary-dim", "#005b69"],
  ["--m3c-on-tertiary", "#ffffff"],
  ["--m3c-tertiary-container", "#a7eeff"],
  ["--m3c-on-tertiary-container", "#004e5b"],
  ["--m3c-tertiary-fixed", "#a7eeff"],
  ["--m3c-tertiary-fixed-dim", "#82d3e5"],
  ["--m3c-on-tertiary-fixed", "#001f27"],
  ["--m3c-on-tertiary-fixed-variant", "#004e5b"],
  ["--m3c-tertiary-container-subtle", "#d5f7ff"],
  ["--m3c-on-tertiary-container-subtle", "#005b69"]
]);

applyOverrides(darkMap, darkOverrides);
applyOverrides(lightMap, lightOverrides);

const darkVars = serializeVars(darkMap);
const lightVars = serializeVars(lightMap);
const out =
  "/* Generated M3 theme - seed #A36BFF (landing violet), TonalSpot, dark default.\n" +
  "   Keep generated M3 color roles, with dense app UI overrides noted in gen-m3-theme.mjs.\n" +
  "   Regenerate: node gen-m3-theme.mjs (do not hand-edit). */\n" +
  ":root,\n" +
  ":root.dark,\n" +
  ":root[data-theme=\"dark\"],\n" +
  "[data-theme=\"dark\"] {\n" +
  "  color-scheme: dark;\n" + darkVars + "\n}\n\n" +
  ":root.light,\n" +
  ":root[data-theme=\"light\"],\n" +
  "[data-theme=\"light\"] {\n" +
  "  color-scheme: light;\n" + lightVars + "\n}\n";
writeFileSync("src/lib/styles/m3-theme.css", out);
console.log("wrote. dark surface=" + darkMap.get("--m3c-surface") + " light surface=" + lightMap.get("--m3c-surface") + " light primary=" + lightMap.get("--m3c-primary"));
