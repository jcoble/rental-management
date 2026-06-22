#!/usr/bin/env node
import { promises as fs } from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const routesRoot = path.join(root, 'web', 'src', 'routes');
const outDir = path.join(root, 'output', 'qa');

function routeFromFile(file) {
  let rel = path.relative(routesRoot, path.dirname(file));
  rel = rel
    .split(path.sep)
    .filter((part) => !part.startsWith('('))
    .join('/');
  const route = `/${rel}`.replace(/\/\+/g, '').replace(/\/$/, '');
  return route === '' ? '/' : route;
}

function count(pattern, text) {
  return [...text.matchAll(pattern)].length;
}

function labels(pattern, text) {
  return [...text.matchAll(pattern)]
    .map((match) => (match[1] ?? match[2] ?? '').trim())
    .filter(Boolean)
    .slice(0, 30);
}

async function walk(dir) {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...await walk(full));
    } else if (/^\+(page|layout|server|page\.server)\.(svelte|ts)$/.test(entry.name)) {
      files.push(full);
    }
  }
  return files;
}

const files = await walk(routesRoot);
const byRoute = new Map();

for (const file of files) {
  const text = await fs.readFile(file, 'utf8');
  const route = routeFromFile(file);
  const item = byRoute.get(route) ?? {
    route,
    files: [],
    buttons: 0,
    inputs: 0,
    selects: 0,
    textareas: 0,
    forms: 0,
    modals: 0,
    loadingStates: 0,
    errorStates: 0,
    links: 0,
    buttonLabels: [],
    inputNames: [],
  };
  item.files.push(path.relative(root, file));
  item.buttons += count(/<button\b|<Button\b/g, text);
  item.inputs += count(/<input\b/g, text);
  item.selects += count(/<select\b|<Select\b/g, text);
  item.textareas += count(/<textarea\b/g, text);
  item.forms += count(/<form\b|use:enhance|enhance\(/g, text);
  item.modals += count(/Dialog|Modal|Sheet|Drawer|Popover/g, text);
  item.loadingStates += count(/loading|isLoading|pending|isSubmitting|Processing|Saving/g, text);
  item.errorStates += count(/error|Error|invalid|Invalid|failed|Failed/g, text);
  item.links += count(/<a\b|href=/g, text);
  item.buttonLabels.push(...labels(/<button[^>]*>([\s\S]{0,120}?)<\/button>|<Button[^>]*>([\s\S]{0,120}?)<\/Button>/g, text));
  item.inputNames.push(...labels(/<(?:input|select|textarea)[^>]*(?:name|aria-label|placeholder)=["']([^"']+)["']/g, text));
  byRoute.set(route, item);
}

const inventory = [...byRoute.values()].sort((a, b) => a.route.localeCompare(b.route));
await fs.mkdir(outDir, { recursive: true });
await fs.writeFile(
  path.join(outDir, 'web-surface-inventory.json'),
  JSON.stringify({ generatedAt: new Date().toISOString(), routeCount: inventory.length, routes: inventory }, null, 2),
);

const md = [
  '# Web Surface Inventory',
  '',
  `Generated at: ${new Date().toISOString()}`,
  `Route count: ${inventory.length}`,
  '',
  '| Route | Buttons | Inputs | Selects | Forms | Modals | Loading | Errors | Files |',
  '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |',
  ...inventory.map((r) => `| \`${r.route}\` | ${r.buttons} | ${r.inputs + r.textareas} | ${r.selects} | ${r.forms} | ${r.modals} | ${r.loadingStates} | ${r.errorStates} | ${r.files.map((f) => `\`${f}\``).join('<br>')} |`),
  '',
];
await fs.writeFile(path.join(outDir, 'web-surface-inventory.md'), md.join('\n'));
console.log(`Wrote ${inventory.length} route inventory rows to ${outDir}`);
