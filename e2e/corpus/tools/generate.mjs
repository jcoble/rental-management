#!/usr/bin/env node
// E2E corpus artifact generator — zero npm deps. Fills HTML templates with data and renders
// them to born-digital PDFs (Chrome --print-to-pdf → text/scanner path) or "phone photo" PNGs
// (Chrome --screenshot of a CSS desk-photo → vision/scanner path). Poppler (pdftoppm/pdfunite)
// is used for raster/merge variants. See e2e/corpus/README.md.
//
// Usage:  node tools/generate.mjs [manifest.json]
//   manifest = [{ template, out, mode: "pdf"|"photo", size?: [w,h], data: {...} }, ...]
// Paths in the manifest are relative to e2e/corpus/.

import { readFileSync, writeFileSync, mkdirSync, statSync, mkdtempSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";

const HERE = dirname(fileURLToPath(import.meta.url));         // e2e/corpus/tools
const CORPUS = resolve(HERE, "..");                            // e2e/corpus
const CHROME = process.env.CHROME_BIN ||
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";

const manifestPath = process.argv[2]
  ? resolve(process.cwd(), process.argv[2])
  : join(HERE, "data.proof.json");

// {{token}} → data[token] ("" if absent). Values are treated as trusted template fragments
// (some, like lineItemsHtml/bodyHtml, are intentional HTML).
function fill(tpl, data) {
  return tpl.replace(/\{\{(\w+)\}\}/g, (_, k) => (k in data ? String(data[k]) : ""));
}

function run(cmd, args) {
  const r = spawnSync(cmd, args, { encoding: "utf8" });
  if (r.status !== 0) {
    process.stderr.write(`  ! ${cmd} exited ${r.status}\n${(r.stderr || "").split("\n").slice(-4).join("\n")}\n`);
  }
  return r.status === 0;
}

function kb(p) { try { return (statSync(p).size / 1024).toFixed(1) + " KB"; } catch { return "?"; } }

const work = mkdtempSync(join(tmpdir(), "corpus-"));         // filled HTML lives outside the repo
const items = JSON.parse(readFileSync(manifestPath, "utf8"));
let ok = 0, total = 0, bytes = 0;

for (const it of items) {
  total++;
  const tplPath = resolve(CORPUS, it.template);
  const outPath = resolve(CORPUS, it.out);
  mkdirSync(dirname(outPath), { recursive: true });
  const html = fill(readFileSync(tplPath, "utf8"), it.data || {});
  const htmlFile = join(work, `t${total}.html`);
  writeFileSync(htmlFile, html);
  const url = "file://" + htmlFile;

  let good = false;
  if (it.mode === "pdf") {
    good = run(CHROME, ["--headless=new", "--disable-gpu", "--no-pdf-header-footer",
      `--print-to-pdf=${outPath}`, url]);
  } else { // photo
    const [w, h] = it.size || [1200, 1600];
    good = run(CHROME, ["--headless=new", "--disable-gpu", "--hide-scrollbars",
      `--window-size=${w},${h}`, `--screenshot=${outPath}`, url]);
  }
  if (good) { ok++; bytes += statSync(outPath).size; }
  console.log(`  ${good ? "OK " : "ERR"}  ${it.out}  (${kb(outPath)})  [${it.mode}]`);
}

console.log(`\n${ok}/${total} artifacts generated · ${(bytes / 1024 / 1024).toFixed(2)} MB total · temp: ${work}`);
process.exit(ok === total ? 0 : 1);
