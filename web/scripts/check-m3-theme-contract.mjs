import { readFileSync } from "node:fs";

const themeCss = readFileSync(new URL("../src/lib/styles/m3-theme.css", import.meta.url), "utf8");
const appCss = readFileSync(new URL("../src/app.css", import.meta.url), "utf8");
const generator = readFileSync(new URL("./gen-m3-theme.mjs", import.meta.url), "utf8");

const failures = [];

function assert(condition, message) {
	if (!condition) failures.push(message);
}

function escapeRegex(value) {
	return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function ruleBody(css, selectorFragment) {
	const match = css.match(new RegExp(`(?:^|\\n)([^{}]*${escapeRegex(selectorFragment)}[^{}]*)\\{([\\s\\S]*?)\\n\\}`, "m"));
	return match?.[2] ?? "";
}

function cssVar(body, name) {
	const match = body.match(new RegExp(`${escapeRegex(name)}:\\s*([^;]+);`));
	return match?.[1]?.trim() ?? "";
}

function hexToRgb(hex) {
	const normalized = hex.trim().replace("#", "");
	if (!/^[0-9a-f]{6}$/i.test(normalized)) return null;
	return {
		r: parseInt(normalized.slice(0, 2), 16),
		g: parseInt(normalized.slice(2, 4), 16),
		b: parseInt(normalized.slice(4, 6), 16)
	};
}

function relativeLuminance(hex) {
	const rgb = hexToRgb(hex);
	if (!rgb) return 0;
	const channels = [rgb.r, rgb.g, rgb.b].map((channel) => {
		const value = channel / 255;
		return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
	});
	return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
}

function contrastRatio(a, b) {
	const first = relativeLuminance(a);
	const second = relativeLuminance(b);
	const lightest = Math.max(first, second);
	const darkest = Math.min(first, second);
	return (lightest + 0.05) / (darkest + 0.05);
}

const darkBody = ruleBody(themeCss, ':root[data-theme="dark"]');
const lightBody = ruleBody(themeCss, ':root[data-theme="light"]');
const appLightBody = ruleBody(appCss, ".light");
const bodyRule = ruleBody(appCss, "body");

assert(/new SchemeTonalSpot\(src,\s*false/.test(generator), "Generator must build the light scheme from the M3 seed.");
assert(/new SchemeTonalSpot\(src,\s*true/.test(generator), "Generator must build the dark scheme from the M3 seed.");
assert(!/light-dark\(/.test(generator), "Generator must not strip light-dark(...) down to one side.");
assert(!/light-dark\(/.test(themeCss), "Generated CSS must emit explicit light/dark token sets, not light-dark(...).");

assert(darkBody.includes("color-scheme: dark;"), "Generated CSS must keep dark as an explicit selector-scoped scheme.");
assert(lightBody.includes("color-scheme: light;"), "Generated CSS must include an explicit light selector-scoped scheme.");
assert(cssVar(darkBody, "--m3c-surface") === "#121217", "Dark default surface must preserve the current neutral dark baseline.");
assert(relativeLuminance(cssVar(lightBody, "--m3c-surface")) > 0.86, "Light surface must be a genuinely light M3 surface.");
assert(contrastRatio(cssVar(lightBody, "--m3c-surface"), cssVar(lightBody, "--m3c-on-surface")) >= 7, "Light on-surface text must keep strong contrast.");
assert(contrastRatio(cssVar(lightBody, "--m3c-primary"), cssVar(lightBody, "--m3c-on-primary")) >= 4.5, "Light primary/on-primary must meet normal text contrast.");

assert(/--background:\s*var\(--m3c-surface\);/.test(appLightBody), "App light tokens must source background from generated M3 surface.");
assert(/--foreground:\s*var\(--app-text-primary\);/.test(appLightBody), "App light tokens must use neutral app text roles.");
assert(!/oklch\(/.test(appLightBody), "App light mode must not carry a separate manual OKLCH palette.");
assert(/color-scheme:\s*var\(--app-color-scheme,\s*dark\);/.test(bodyRule), "Body color-scheme must follow the active theme token.");

if (failures.length > 0) {
	console.error("M3 theme contract failed:");
	for (const failure of failures) console.error(`- ${failure}`);
	process.exit(1);
}

console.log("M3 theme contract passed.");
