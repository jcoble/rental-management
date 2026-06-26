#!/usr/bin/env node
import { spawn } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, '..', '..');
const outputDir = path.join(repoRoot, 'output', 'qa', 'tsk-433-extraction-quality');
const swiftHelper = path.join(outputDir, 'apple-vision-ocr.swift');
const leaseImageBuilder = path.join(outputDir, 'make-lease-bedbath-image.swift');
const claudeBin = process.env.CLAUDE_CLI_PATH || 'claude';
const model = process.env.CLAUDE_MODEL || 'sonnet';

const schemaPrompts = {
  lease:
    'Extract a residential lease into compact JSON. Include target_entity_type, tenant_name, property_address, unit_number, unit_bedrooms, unit_bathrooms, unit_square_feet, start_date, end_date, monthly_rent, security_deposit, rent_due_day. Read common shorthand such as 2BR/1BA, 1.5BA, half bath, and one-and-one-half baths. Never default missing bed/bath counts to 0; use an empty string when absent.',
  receipt:
    'Extract a vendor receipt, bill, or invoice into compact JSON. Include vendor_name, receipt_number, transaction_date, subtotal, tax, total, document_kind, and line_items. line_items must be one object per printed charge row, preserving printed order, with description, quantity, unit_price, and amount only when visible. Do not include subtotal, tax, tip, discount, shipping, payment, balance, or total rows as line items. OCR may flatten tables into separate description, quantity, unit-price, and amount columns; reconstruct rows by matching values in printed column order and do not invent missing cells.',
};

const cases = [
  {
    id: 'lease-bedbath-generated-photo',
    kind: 'lease',
    imagePath: path.join(outputDir, 'lease-bedbath-camera.jpg'),
    expected: {
      unit_bedrooms: 2,
      unit_bathrooms: 1.5,
      unit_square_feet: 920,
      monthly_rent: 1375,
      security_deposit: 1375,
    },
  },
  {
    id: 'comfortzone-invoice-photo',
    kind: 'receipt',
    imagePath: path.join(repoRoot, 'samples', 'scans', 'vendor_invoice_comfortzone_hvac_oct2025_photo.jpg'),
    expected: {
      total: 660,
      amounts: [95, 145, 110, 97.5, 212.5],
      quantities: [1, 1, 1, 1.5, 2.5],
      unitPrices: [95, 145, 110, 65, 85],
    },
  },
  {
    id: 'apex-repair-receipt-photo',
    kind: 'receipt',
    imagePath: path.join(repoRoot, 'samples', 'scans', 'repair_receipt_apex_plumbing_dec2025_photo.jpg'),
    expected: {
      total: 190,
      amounts: [28.5, 19, 142.5],
    },
  },
];

await fs.mkdir(outputDir, { recursive: true });
await writeSwiftHelpers();
await run('swift', [leaseImageBuilder, cases[0].imagePath], { timeoutMs: 30000 });

const results = {
  generatedAt: new Date().toISOString(),
  environment: {
    claudeBin,
    model,
    ocr: 'macOS Apple Vision via Swift helper',
  },
  cases: [],
};

for (const testCase of cases) {
  const ocr = await run('swift', [swiftHelper, testCase.imagePath], { timeoutMs: 30000 });
  const strategies = [];
  for (const strategy of ['vision', 'ocr', 'hybrid']) {
    const prompt = buildPrompt(strategy, testCase, ocr.stdout);
    const args = ['-p', prompt, '--model', model];
    if (strategy !== 'ocr') {
      args.push('--allowed-tools', 'Read');
    }

    const runResult = await run(claudeBin, args, { timeoutMs: 180000, okExitCodes: [0] });
    const parsed = parseJsonObject(runResult.stdout);
    strategies.push({
      strategy,
      ok: runResult.exitCode === 0 && parsed.ok,
      exitCode: runResult.exitCode,
      stderr: runResult.stderr.trim(),
      parsed: parsed.value,
      parseError: parsed.error,
      score: parsed.ok ? score(testCase, parsed.value) : { points: 0, possible: 1, notes: [parsed.error] },
      raw: runResult.stdout.trim(),
    });
  }

  results.cases.push({
    id: testCase.id,
    kind: testCase.kind,
    imagePath: testCase.imagePath,
    ocrText: ocr.stdout.trim(),
    ocrExitCode: ocr.exitCode,
    strategies,
  });
}

await fs.writeFile(path.join(outputDir, 'summary.json'), `${JSON.stringify(results, null, 2)}\n`);
await fs.writeFile(path.join(outputDir, 'report.md'), renderMarkdown(results));
console.log(`Wrote ${path.join(outputDir, 'report.md')}`);

function buildPrompt(strategy, testCase, ocrText) {
  const schema = schemaPrompts[testCase.kind];
  if (strategy === 'vision') {
    return `Read the document image at ${testCase.imagePath}.\n\n${schema}\n\nReturn ONLY one compact JSON object with no markdown fences.`;
  }

  if (strategy === 'ocr') {
    return `${schema}\n\nExtract from the OCR text below. The OCR may contain misreads or flattened tables; leave fields empty when they are absent. Return ONLY one compact JSON object with no markdown fences.\n\nOCR TEXT:\n${ocrText}`;
  }

  return `Read the document image at ${testCase.imagePath}.\n\n${schema}\n\nThe image is authoritative. OCR text from local image extraction is included below as a secondary hint only; ignore it when it conflicts with the image, appears misread, or omits table columns. Return ONLY one compact JSON object with no markdown fences.\n\nOCR TEXT:\n${ocrText}`;
}

function parseJsonObject(raw) {
  const text = raw.trim().replace(/^```(?:json)?/i, '').replace(/```$/i, '').trim();
  const start = text.indexOf('{');
  const end = text.lastIndexOf('}');
  if (start < 0 || end <= start) {
    return { ok: false, value: null, error: 'no JSON object found' };
  }

  try {
    return { ok: true, value: JSON.parse(text.slice(start, end + 1)), error: null };
  } catch (error) {
    return { ok: false, value: null, error: error.message };
  }
}

function score(testCase, actual) {
  const notes = [];
  let points = 0;
  let possible = 0;

  if (testCase.kind === 'lease') {
    for (const [field, expected] of Object.entries(testCase.expected)) {
      possible += 1;
      if (numberClose(actual[field], expected)) {
        points += 1;
      } else {
        notes.push(`${field}: expected ${expected}, got ${JSON.stringify(actual[field])}`);
      }
    }
  } else {
    possible += 1;
    if (numberClose(actual.total, testCase.expected.total)) points += 1;
    else notes.push(`total: expected ${testCase.expected.total}, got ${JSON.stringify(actual.total)}`);

    const rows = Array.isArray(actual.line_items) ? actual.line_items : [];
    possible += testCase.expected.amounts.length;
    testCase.expected.amounts.forEach((expected, index) => {
      if (numberClose(rows[index]?.amount, expected)) points += 1;
      else notes.push(`line_items[${index}].amount: expected ${expected}, got ${JSON.stringify(rows[index]?.amount)}`);
    });

    if (testCase.expected.quantities) {
      possible += testCase.expected.quantities.length;
      testCase.expected.quantities.forEach((expected, index) => {
        if (numberClose(rows[index]?.quantity, expected)) points += 1;
        else notes.push(`line_items[${index}].quantity: expected ${expected}, got ${JSON.stringify(rows[index]?.quantity)}`);
      });
    }

    if (testCase.expected.unitPrices) {
      possible += testCase.expected.unitPrices.length;
      testCase.expected.unitPrices.forEach((expected, index) => {
        if (numberClose(rows[index]?.unit_price, expected)) points += 1;
        else notes.push(`line_items[${index}].unit_price: expected ${expected}, got ${JSON.stringify(rows[index]?.unit_price)}`);
      });
    }
  }

  return { points, possible, percent: possible === 0 ? 0 : Math.round((points / possible) * 1000) / 10, notes };
}

function numberClose(value, expected) {
  const parsed = typeof value === 'number' ? value : Number(String(value ?? '').replace(/[$,]/g, ''));
  return Number.isFinite(parsed) && Math.abs(parsed - expected) < 0.01;
}

function renderMarkdown(data) {
  const lines = [
    '# TSK-433 Extraction Quality Comparison',
    '',
    `Generated: ${data.generatedAt}`,
    `Claude model: ${data.environment.model}`,
    `OCR: ${data.environment.ocr}`,
    '',
    '| Case | Strategy | Score | Notes |',
    '| --- | --- | ---: | --- |',
  ];

  for (const testCase of data.cases) {
    for (const strategy of testCase.strategies) {
      const scoreText = `${strategy.score.points}/${strategy.score.possible} (${strategy.score.percent}%)`;
      const notes = strategy.score.notes.length === 0 ? 'clean' : strategy.score.notes.join('; ');
      lines.push(`| ${testCase.id} | ${strategy.strategy} | ${scoreText} | ${escapeMd(notes)} |`);
    }
  }

  lines.push('', '## OCR Text');
  for (const testCase of data.cases) {
    lines.push('', `### ${testCase.id}`, '', '```text', testCase.ocrText, '```');
  }

  return `${lines.join('\n')}\n`;
}

function escapeMd(value) {
  return String(value).replace(/\|/g, '\\|').replace(/\n/g, ' ');
}

function run(command, args, options = {}) {
  const timeoutMs = options.timeoutMs ?? 60000;
  const okExitCodes = options.okExitCodes ?? [0];

  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '';
    let stderr = '';
    const timer = setTimeout(() => {
      child.kill('SIGTERM');
      reject(new Error(`${command} timed out after ${timeoutMs}ms`));
    }, timeoutMs);

    child.stdout.on('data', chunk => {
      stdout += chunk.toString();
    });
    child.stderr.on('data', chunk => {
      stderr += chunk.toString();
    });
    child.on('error', error => {
      clearTimeout(timer);
      reject(error);
    });
    child.on('close', exitCode => {
      clearTimeout(timer);
      if (!okExitCodes.includes(exitCode)) {
        resolve({ exitCode, stdout, stderr });
        return;
      }
      resolve({ exitCode, stdout, stderr });
    });
  });
}

async function writeSwiftHelpers() {
  await fs.writeFile(swiftHelper, `import Foundation
import Vision

let url = URL(fileURLWithPath: CommandLine.arguments[1])
let request = VNRecognizeTextRequest()
request.recognitionLevel = .accurate
request.usesLanguageCorrection = true
request.recognitionLanguages = ["en-US"]
let handler = VNImageRequestHandler(url: url, options: [:])
try handler.perform([request])
let lines = (request.results ?? []).compactMap { $0.topCandidates(1).first?.string }
print(lines.joined(separator: "\\n"))
`);

  await fs.writeFile(leaseImageBuilder, `import AppKit
import Foundation

let outputPath = CommandLine.arguments[1]
let size = NSSize(width: 1400, height: 1800)
let image = NSImage(size: size)

func drawText(_ text: String, x: CGFloat, top: CGFloat, size fontSize: CGFloat, bold: Bool = false) {
    let font = bold ? NSFont.boldSystemFont(ofSize: fontSize) : NSFont.systemFont(ofSize: fontSize)
    let attrs: [NSAttributedString.Key: Any] = [
        .font: font,
        .foregroundColor: NSColor(calibratedWhite: 0.08, alpha: 1)
    ]
    let y = size.height - top - fontSize * 1.25
    text.draw(at: NSPoint(x: x, y: y), withAttributes: attrs)
}

image.lockFocus()
NSColor(calibratedRed: 0.77, green: 0.78, blue: 0.75, alpha: 1).setFill()
NSRect(origin: .zero, size: size).fill()
NSColor(calibratedRed: 1, green: 0.995, blue: 0.97, alpha: 1).setFill()
NSBezierPath(roundedRect: NSRect(x: 100, y: 110, width: 1200, height: 1580), xRadius: 8, yRadius: 8).fill()

let lines: [(String, CGFloat, Bool)] = [
    ("RESIDENTIAL LEASE AGREEMENT", 48, true),
    ("Synthetic QA document - no real person, account, or property", 24, false),
    ("Tenant: Nina Alvarez", 34, false),
    ("Premises: 742 Maple Ave, Unit 3B, Columbus, OH 43215", 34, false),
    ("Unit details: 2BR/1.5BA, approximately 920 square feet", 34, false),
    ("Lease term: March 1, 2026 through February 28, 2027", 34, false),
    ("Monthly rent: $1,375.00 due on the 1st day of each month", 34, false),
    ("Security deposit: $1,375.00", 34, false),
    ("Late fee: $75.00 after the 5th day of the month", 34, false),
    ("Utilities: tenant pays electric and gas; landlord pays trash", 34, false),
    ("Signatures: synthetic landlord and synthetic tenant", 34, false)
]

var top: CGFloat = 170
for (line, fontSize, bold) in lines {
    drawText(line, x: 160, top: top, size: fontSize, bold: bold)
    top += bold ? 88 : 62
}

image.unlockFocus()
guard let tiff = image.tiffRepresentation,
      let bitmap = NSBitmapImageRep(data: tiff),
      let jpeg = bitmap.representation(using: .jpeg, properties: [.compressionFactor: 0.88]) else {
    fatalError("failed to render lease image")
}
try jpeg.write(to: URL(fileURLWithPath: outputPath))
`);
}
