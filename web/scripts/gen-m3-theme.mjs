import { writeFileSync } from "node:fs";
import { genCSS, colors } from "m3-svelte/etc/colors";
import { Hct, SchemeTonalSpot } from "@ktibow/material-color-utilities-nightly";

const src = Hct.fromInt(0xff7c6ff5); // periwinkle violet #7C6FF5 (TSK-596 refresh)
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

  // Pinned accent ranges (ported from the EdiPlatform TSK-162 color pass).
  // TonalSpot from the violet seed collapses secondary into grey-mauve and has
  // no warm role at all, so the two accent families are pinned to the
  // design-spec hues instead:
  //   secondary = vivid cyan (#22d3ee seed) — selection/active "pop" accent.
  //   tertiary  = warm amber (#f59e0b seed) — maintenance/work-order warmth.
  // Tones follow the M3 dark mapping (role 80 / dim 75 / on 20 / container 30 /
  // on-container 90 / fixed 90 / fixed-dim 80 / on-fixed 10 / subtle 25 / on-subtle 85),
  // computed with this package's TonalPalette. Coral stays reserved for error.
  ["--m3c-secondary", "#2fd9f4"],
  ["--m3c-secondary-dim", "#00cbe6"],
  ["--m3c-on-secondary", "#00363e"],
  ["--m3c-secondary-container", "#004e5a"],
  ["--m3c-on-secondary-container", "#a2eeff"],
  ["--m3c-secondary-fixed", "#a2eeff"],
  ["--m3c-secondary-fixed-dim", "#2fd9f4"],
  ["--m3c-on-secondary-fixed", "#001f25"],
  ["--m3c-on-secondary-fixed-variant", "#004e5a"],
  ["--m3c-secondary-container-subtle", "#00424c"],
  ["--m3c-on-secondary-container-subtle", "#5de6ff"],

  ["--m3c-tertiary", "#ffb95f"],
  ["--m3c-tertiary-dim", "#fea619"],
  ["--m3c-on-tertiary", "#472a00"],
  ["--m3c-tertiary-container", "#653e00"],
  ["--m3c-on-tertiary-container", "#ffddb8"],
  ["--m3c-tertiary-fixed", "#ffddb8"],
  ["--m3c-tertiary-fixed-dim", "#ffb95f"],
  ["--m3c-on-tertiary-fixed", "#2a1700"],
  ["--m3c-on-tertiary-fixed-variant", "#653e00"],
  ["--m3c-tertiary-container-subtle", "#563400"],
  ["--m3c-on-tertiary-container-subtle", "#ffcb8e"],

  // Error → cooler ROSE/raspberry (TSK-596): the M3 default orange-salmon clashed
  // with the violet/periwinkle scheme. A magenta-leaning rose still reads as
  // "alert/negative" but harmonizes. Drives --destructive + .m3-tone--error app-wide.
  ["--m3c-error", "#ffb2c1"],
  ["--m3c-on-error", "#5f1130"],
  ["--m3c-error-container", "#7e2741"],
  ["--m3c-on-error-container", "#ffd9e0"]
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

  // Same pinned accent families in light mode (M3 light mapping: role 40 / dim 35 /
  // on white / container 90 / on-container 30 / fixed-dim 70 / subtle 95 / on-subtle 35).
  ["--m3c-secondary", "#006877"],
  ["--m3c-secondary-dim", "#005b68"],
  ["--m3c-on-secondary", "#ffffff"],
  ["--m3c-secondary-container", "#a2eeff"],
  ["--m3c-on-secondary-container", "#004e5a"],
  ["--m3c-secondary-fixed", "#a2eeff"],
  ["--m3c-secondary-fixed-dim", "#00bcd5"],
  ["--m3c-on-secondary-fixed", "#001f25"],
  ["--m3c-on-secondary-fixed-variant", "#004e5a"],
  ["--m3c-secondary-container-subtle", "#d4f7ff"],
  ["--m3c-on-secondary-container-subtle", "#005b68"],

  ["--m3c-tertiary", "#855300"],
  ["--m3c-tertiary-dim", "#754900"],
  ["--m3c-on-tertiary", "#ffffff"],
  ["--m3c-tertiary-container", "#ffddb8"],
  ["--m3c-on-tertiary-container", "#653e00"],
  ["--m3c-tertiary-fixed", "#ffddb8"],
  ["--m3c-tertiary-fixed-dim", "#ee9800"],
  ["--m3c-on-tertiary-fixed", "#2a1700"],
  ["--m3c-on-tertiary-fixed-variant", "#653e00"],
  ["--m3c-tertiary-container-subtle", "#ffeede"],
  ["--m3c-on-tertiary-container-subtle", "#754900"],

  ["--m3c-error", "#b41a4e"],
  ["--m3c-on-error", "#ffffff"],
  ["--m3c-error-container", "#ffd9e0"],
  ["--m3c-on-error-container", "#400018"]
]);

applyOverrides(darkMap, darkOverrides);
applyOverrides(lightMap, lightOverrides);

const darkVars = serializeVars(darkMap);
const lightVars = serializeVars(lightMap);
const out =
  "/* Generated M3 theme - seed #A36BFF (landing violet), TonalSpot, dark default.\n" +
  "   Accent pins: secondary=cyan (#22d3ee seed), tertiary=amber (#f59e0b seed) - see gen-m3-theme.mjs.\n" +
  "   This file is the SINGLE source of truth for --m3c scheme roles (app.css no longer duplicates them).\n" +
  "   Regenerate: node scripts/gen-m3-theme.mjs (do not hand-edit). */\n" +
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
