#!/usr/bin/env node
import { promises as fs } from 'node:fs';
import path from 'node:path';

const root = process.cwd();
const routesRoot = path.join(root, 'web', 'src', 'routes');
const outDir = path.join(root, 'output', 'qa');

const SURFACE_GROUPS = [
  {
    id: 'shell-guards-errors',
    title: 'Shell, Guards, Errors',
    roles: ['anonymous', 'staff', 'tenant', 'admin', 'superadmin'],
    routes: ['root/protected/admin/portal/superadmin/docs layouts', 'app/docs errors'],
    acceptance: [
      'Anonymous users redirect from protected surfaces to login or welcome.',
      'Tenant, staff, admin, and superadmin layouts enforce their role gates.',
      'First-login onboarding gate redirects staff users until setup mode is chosen.',
      'App and docs error surfaces fail soft without leaking sensitive details.',
    ],
    edgeCases: ['browser back after logout', 'expired refresh token', 'wrong role route', 'unknown route'],
  },
  {
    id: 'public-auth-oauth',
    title: 'Public Auth And OAuth',
    roles: ['anonymous', 'new landlord'],
    routes: ['/welcome', '/login', '/register', '/forgot-password', '/reset-password', '/verify-email', '/choose-setup', '/setting-up', '/logout', '/auth/google', '/auth/google/callback', '/api/auth/refresh'],
    acceptance: [
      'Auth forms validate required fields and preserve non-secret input on failure.',
      'Registration and verification support local dev confirmation without production email.',
      'Refresh and logout use app-namespaced cookies and clear auth state.',
      'Google routes handle unavailable provider, state errors, callback errors, and success redirects.',
    ],
    edgeCases: ['duplicate registration email', 'invalid or expired token', 'unverified login', 'Google disabled', 'cookie collision'],
  },
  {
    id: 'public-docs-apply-sign',
    title: 'Public Docs, Apply, And Sign',
    roles: ['anonymous applicant', 'anonymous signer'],
    routes: ['/docs', '/docs/[slug]', '/apply/[token]', '/sign/[token]'],
    acceptance: [
      'Docs index, detail, and docs error states render without authentication.',
      'Public applications support property/unit preference, scan autofill, consent, and submit states.',
      'Signing supports document open, typed or drawn signature, clear, decline, invalid token, expired token, and already-signed states.',
    ],
    edgeCases: ['missing docs slug', 'invalid application token', 'invalid signing token', 'provider unavailable'],
  },
  {
    id: 'core-staff-app',
    title: 'Core Staff App',
    roles: ['owner/admin', 'manager', 'agent'],
    routes: ['/', '/get-started', '/properties', '/properties/[id]', '/units', '/units/[id]', '/tenants', '/tenants/[id]', '/leases', '/leases/[id]', '/applications', '/applications/[id]', '/owners', '/owners/[id]', '/vendors', '/vendors/[id]', '/deposits', '/deposits/[id]', '/appointments', '/appointments/[id]', '/messages', '/notices', '/audit', '/ai'],
    acceptance: [
      'Empty and populated states render for zero, one, page-sized, and production-scale data.',
      'Create, edit, delete/status, detail tabs, documents, and history controls are role appropriate.',
      'Cross-portfolio ids cannot be viewed or linked.',
      'All list filtering, sorting, joins, paging, and aggregation are DB-side.',
    ],
    edgeCases: ['0 records', '1 record', '20 records', '200+ records', 'duplicate names', 'deactivated records', 'stale detail route'],
  },
  {
    id: 'scan-intake',
    title: 'Scan Intake',
    roles: ['owner/admin', 'manager', 'agent'],
    routes: ['/scan', '/scan/new-rental', '/scan/batch', '/scan/batch/[id]', '/scan/[draftId]'],
    acceptance: [
      'Lease scans bootstrap properties, units, tenants, and leases from an empty portfolio.',
      'PDFs and camera-style JPEGs use the same extraction engine and review behavior.',
      'Review refuses confirm until required data is present and preserves failed drafts for retry/reject.',
      'Batch counts are DB-side and match draft statuses.',
    ],
    edgeCases: ['blank document', 'unsupported type', 'oversized file', 'skewed image', 'duplicate lease', '0/1/40/101 file batch', 'stuck processing draft'],
  },
  {
    id: 'file-proxies',
    title: 'File Proxies',
    roles: ['authenticated staff'],
    routes: ['/scan-file/[id]', '/lease-file/[id]', '/payment-file/[id]', '/expense-file/[id]', '/workorder-file/[id]', '/application-file/[id]', '/document-file/[id]'],
    acceptance: [
      'Proxy routes require auth, scope by portfolio, and return 404 for missing files.',
      'Safe inline content renders inline and unsafe content is forced to attachment.',
      'Upstream failures degrade as explicit proxy errors.',
    ],
    edgeCases: ['no cookie', 'wrong portfolio id', 'missing file', 'unsafe MIME type', 'thumb=true passthrough'],
  },
  {
    id: 'accounting-reports',
    title: 'Accounting And Reports',
    roles: ['owner/admin', 'manager'],
    routes: ['/accounting', '/accounting/past-due', '/accounting/year-end', '/accounting/expenses/[id]', '/tenant-accounts/[tenantAccountId]/entries/[tenantLedgerEntryId]', '/reports', '/reports/[report]', '/owners-report', '/banking', '/tax'],
    acceptance: [
      'Filters, tabs, append-only receipt/charge posting, correction, matching, CSV/export/print, and packet flows work on scanned data.',
      'Owner, year-end, Schedule E, banking, and report grids use DB-side filtering, sorting, joins, paging, and aggregation.',
      'External provider unavailable states are explicit and non-destructive.',
    ],
    edgeCases: ['no bank connection', 'unmatched bank transaction', 'partial payment', 'empty report', 'large ledger', 'date range crossing year'],
  },
  {
    id: 'settings-setup-import',
    title: 'Settings, Setup, And Import',
    roles: ['owner/admin', 'manager'],
    routes: ['/settings', '/settings/security', '/settings/accounting', '/onboarding', '/import', '/plaid/auth'],
    acceptance: [
      'Settings forms validate and persist profile, company, notification, security, and accounting options.',
      'Password change covers reveal, mismatch, policy, success, and error states.',
      'Accounting/provider setup covers connect, reconnect, disconnect, pull/push toggles, backfill range, mapping confirmation, review queue, and OAuth return states.',
      'Onboarding and import can start from empty live data and resume safely.',
    ],
    edgeCases: ['provider callback failure', 'invalid password', 'partial spreadsheet import', 'stale onboarding step', 'portfolio id storage mismatch'],
  },
  {
    id: 'maintenance-detail',
    title: 'Maintenance Detail',
    roles: ['owner/admin', 'manager', 'agent'],
    routes: ['/maintenance', '/maintenance/[id]', '/maintenance/recurring', '/maintenance/inspections/[id]', '/maintenance/work-orders/[id]'],
    acceptance: [
      'Work-order list, details, status-note modal, vendor dispatch/text, contact actions, ratings, docs, and history render and mutate safely.',
      'Recurring maintenance supports create/edit/pause/delete flows.',
      'Inspections support checklist, photos, complete confirmation, and report download.',
      'Legacy work-order detail redirects preserve params and auth guards.',
    ],
    edgeCases: ['vendor missing contact', 'offline attachment', 'inspection without photos', 'cancelled work order', 'recurring task due today'],
  },
  {
    id: 'portal',
    title: 'Tenant Portal',
    roles: ['tenant', 'staff helper'],
    routes: ['/portal', '/portal/messages', '/portal/notifications', '/portal/maintenance', '/portal/payments', '/portal/lease', '/portal/appointments'],
    acceptance: [
      'Tenant portal sees only the tenant-owned lease, payments, maintenance, messages, and appointments.',
      'Messaging compose/reply, maintenance photo/detail, lease Q&A, payment/autopay, and provider-unavailable states are explicit.',
      'Staff/helper portal access follows route guard rules.',
    ],
    edgeCases: ['no active lease', 'multiple leases', 'provider unavailable', 'checkout return', 'invalid notification preference'],
  },
  {
    id: 'admin-superadmin',
    title: 'Admin And Superadmin',
    roles: ['platform admin', 'superadmin'],
    routes: ['/admin/users', '/admin/audit', '/superadmin/engine'],
    acceptance: [
      'Admin routes reject non-admin users and superadmin routes reject non-superadmins.',
      'User invite, one-time password, role, active/deactivated, audit filter/diff/export, and engine refresh/provider/model/heartbeat/last-error states are covered where present.',
    ],
    edgeCases: ['staff attempts admin', 'admin attempts superadmin', 'invite duplicate email', 'engine provider down', 'stale heartbeat'],
  },
  {
    id: 'compat-redirects',
    title: 'Compatibility Redirects',
    roles: ['authenticated staff'],
    routes: ['/activity', '/analytics', '/owners/vendors', '/owners/vendors/[id]', '/maintenance/work-orders/[id]'],
    acceptance: [
      'Compatibility routes issue expected redirects to canonical routes.',
      'Route params and protected auth guards are preserved.',
    ],
    edgeCases: ['unauthenticated redirect', 'detail id preserved', 'query string preserved'],
  },
];

function routeFromFile(file) {
  let rel = path.relative(routesRoot, path.dirname(file));
  rel = rel
    .split(path.sep)
    .filter((part) => !part.startsWith('('))
    .join('/');
  const route = `/${rel}`.replace(/\/\+/g, '').replace(/\/$/, '');
  return route === '' ? '/' : route;
}

function scopesFromFile(file) {
  return path.relative(routesRoot, file)
    .split(path.sep)
    .filter((part) => part.startsWith('(') && part.endsWith(')'))
    .map((part) => part.slice(1, -1));
}

function fileKind(file, text) {
  const name = path.basename(file);
  if (name === '+layout.server.ts') return 'layout-guard';
  if (name === '+layout.svelte') return 'layout-shell';
  if (name === '+error.svelte') return 'error-surface';
  if (name === '+server.ts') return 'server-route';
  if (name === '+page.server.ts') return 'page-server-load';
  if (name === '+page.ts') return /redirect\s*\(/.test(text) ? 'redirect' : 'page-load';
  if (name === '+page.svelte') return 'page';
  if (name === '+layout.ts') return 'layout-load';
  return 'other';
}

function count(pattern, text) {
  return [...text.matchAll(pattern)].length;
}

function labels(pattern, text) {
  return [...text.matchAll(pattern)]
    .map((match) => (match[1] ?? match[2] ?? '').trim().replace(/\s+/g, ' '))
    .filter(Boolean)
    .slice(0, 40);
}

async function walk(dir) {
  const entries = await fs.readdir(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      files.push(...await walk(full));
    } else if (/^\+(page|page\.server|layout|layout\.server|layout\.ts|server|error)\.(svelte|ts)$/.test(entry.name)) {
      files.push(full);
    }
  }
  return files;
}

function hasAny(route, routes) {
  return routes.includes(route);
}

function classifySurfaceGroups(route, item) {
  const groups = new Set();
  const kinds = new Set(item.kinds);
  const scopes = new Set(item.scopes);

  if ([...kinds].some((kind) => kind === 'layout-guard' || kind === 'layout-shell' || kind === 'error-surface' || kind === 'layout-load')) {
    groups.add('shell-guards-errors');
  }
  if (hasAny(route, ['/welcome', '/login', '/register', '/forgot-password', '/reset-password', '/verify-email', '/choose-setup', '/setting-up', '/logout', '/auth/google', '/auth/google/callback', '/api/auth/refresh'])) {
    groups.add('public-auth-oauth');
  }
  if (route === '/docs' || route.startsWith('/docs/') || route.startsWith('/apply/') || route.startsWith('/sign/')) {
    groups.add('public-docs-apply-sign');
  }
  if (hasAny(route, ['/', '/get-started', '/properties', '/properties/[id]', '/units', '/units/[id]', '/tenants', '/tenants/[id]', '/leases', '/leases/[id]', '/applications', '/applications/[id]', '/owners', '/owners/[id]', '/vendors', '/vendors/[id]', '/deposits', '/deposits/[id]', '/appointments', '/appointments/[id]', '/messages', '/notices', '/audit', '/ai'])) {
    groups.add('core-staff-app');
  }
  if (route === '/scan' || route.startsWith('/scan/')) {
    groups.add('scan-intake');
  }
  if (/^\/(scan|lease|payment|expense|workorder|application|document)-file\/\[id\]$/.test(route)) {
    groups.add('file-proxies');
  }
  if (hasAny(route, ['/accounting', '/accounting/past-due', '/accounting/year-end', '/accounting/expenses/[id]', '/tenant-accounts/[tenantAccountId]/entries/[tenantLedgerEntryId]', '/reports', '/reports/[report]', '/owners-report', '/banking', '/tax'])) {
    groups.add('accounting-reports');
  }
  if (hasAny(route, ['/settings', '/settings/security', '/settings/accounting', '/onboarding', '/import', '/plaid/auth'])) {
    groups.add('settings-setup-import');
  }
  if (route === '/maintenance' || route.startsWith('/maintenance/')) {
    groups.add('maintenance-detail');
  }
  if (route === '/portal' || route.startsWith('/portal/')) {
    groups.add('portal');
  }
  if (route.startsWith('/admin/') || route.startsWith('/superadmin/')) {
    groups.add('admin-superadmin');
  }
  if (hasAny(route, ['/activity', '/analytics', '/owners/vendors', '/owners/vendors/[id]', '/maintenance/work-orders/[id]'])) {
    groups.add('compat-redirects');
  }
  if (scopes.has('admin') || scopes.has('superadmin')) {
    groups.add('admin-superadmin');
  }
  if (scopes.has('portal')) {
    groups.add('portal');
  }

  return [...groups].sort();
}

function routeKind(item) {
  const kinds = new Set(item.kinds);
  if (item.surfaceGroups.includes('file-proxies')) return 'file-proxy';
  if (item.surfaceGroups.includes('compat-redirects')) return 'redirect';
  if (kinds.has('layout-guard')) return 'guard/layout';
  if (kinds.has('error-surface')) return 'error';
  if (kinds.has('server-route') && item.controls.buttons === 0) return 'server';
  if (kinds.has('page')) return 'page';
  return [...kinds].sort().join(',') || 'unknown';
}

const files = await walk(routesRoot);
const byRoute = new Map();

for (const file of files) {
  const text = await fs.readFile(file, 'utf8');
  const route = routeFromFile(file);
  const item = byRoute.get(route) ?? {
    route,
    files: [],
    scopes: [],
    kinds: [],
    controls: {
      buttons: 0,
      inputs: 0,
      selects: 0,
      checkboxes: 0,
      textareas: 0,
      forms: 0,
      tabs: 0,
      modals: 0,
      popovers: 0,
      uploads: 0,
      links: 0,
      testIds: 0,
    },
    states: {
      loading: 0,
      error: 0,
      empty: 0,
      success: 0,
      disabled: 0,
    },
    buttonLabels: [],
    inputNames: [],
    testIds: [],
  };

  const relFile = path.relative(root, file);
  item.files.push(relFile);
  item.scopes.push(...scopesFromFile(file));
  item.kinds.push(fileKind(file, text));
  item.controls.buttons += count(/<button\b|<Button\b/g, text);
  item.controls.inputs += count(/<input\b|<Input\b|<DatePicker\b|<RangeDatePicker\b|<AddressAutocomplete\b|<TimeZoneSelect\b|<StateSelect\b/g, text);
  item.controls.selects += count(/<select\b|<Select\b|Select\.Trigger|Select\.Item/g, text);
  item.controls.checkboxes += count(/<Checkbox\b|<Switch\b|type=["']checkbox["']/g, text);
  item.controls.textareas += count(/<textarea\b|<Textarea\b/g, text);
  item.controls.forms += count(/<form\b|use:enhance|enhance\(/g, text);
  item.controls.tabs += count(/<Tabs\b|Tabs\.Trigger|Tabs\.Content|data-testid=["'][^"']*tab/g, text);
  item.controls.modals += count(/Dialog|Modal|Sheet|Drawer|AlertDialog|ConfirmDialog/g, text);
  item.controls.popovers += count(/Popover|Dropdown|Menu|Tooltip/g, text);
  item.controls.uploads += count(/type=["']file["']|upload|Upload|Dropzone|DataTransfer/g, text);
  item.controls.links += count(/<a\b|href=/g, text);
  item.controls.testIds += count(/data-testid=/g, text);
  item.states.loading += count(/loading|isLoading|pending|isPending|Processing|Saving|Submitting/g, text);
  item.states.error += count(/error|Error|invalid|Invalid|failed|Failed|catch\s*\(/g, text);
  item.states.empty += count(/empty|Empty|No\s+\w+|0\s+\w+/g, text);
  item.states.success += count(/success|Success|complete|Complete|saved|Saved|confirmed|Confirmed/g, text);
  item.states.disabled += count(/disabled=|disabled\}/g, text);
  item.buttonLabels.push(...labels(/<button[^>]*>([\s\S]{0,160}?)<\/button>|<Button[^>]*>([\s\S]{0,160}?)<\/Button>/g, text));
  item.inputNames.push(...labels(/<(?:input|Input|select|textarea|Textarea)[^>]*(?:name|aria-label|placeholder)=["']([^"']+)["']/g, text));
  item.testIds.push(...labels(/data-testid=["']([^"']+)["']/g, text));
  byRoute.set(route, item);
}

const inventory = [...byRoute.values()]
  .map((item) => {
    item.scopes = [...new Set(item.scopes)].sort();
    item.kinds = [...new Set(item.kinds)].sort();
    item.buttonLabels = [...new Set(item.buttonLabels)].slice(0, 40);
    item.inputNames = [...new Set(item.inputNames)].slice(0, 40);
    item.testIds = [...new Set(item.testIds)].slice(0, 60);
    item.surfaceGroups = classifySurfaceGroups(item.route, item);
    item.kind = routeKind(item);
    return item;
  })
  .sort((a, b) => a.route.localeCompare(b.route));

const unclassifiedRoutes = inventory
  .filter((item) => item.surfaceGroups.length === 0)
  .map((item) => item.route);

await fs.mkdir(outDir, { recursive: true });
await fs.writeFile(
  path.join(outDir, 'web-surface-inventory.json'),
  JSON.stringify({
    generatedAt: new Date().toISOString(),
    routeCount: inventory.length,
    unclassifiedRoutes,
    surfaceGroups: SURFACE_GROUPS,
    routes: inventory,
  }, null, 2),
);

const md = [
  '# Web Surface Inventory',
  '',
  `Generated at: ${new Date().toISOString()}`,
  `Route count: ${inventory.length}`,
  `Unclassified routes: ${unclassifiedRoutes.length === 0 ? 'none' : unclassifiedRoutes.map((r) => `\`${r}\``).join(', ')}`,
  '',
  '## Acceptance Matrix',
  '',
  '| Group | Roles | Routes/files | Acceptance | Edge cases |',
  '| --- | --- | --- | --- | --- |',
  ...SURFACE_GROUPS.map((group) => [
    `\`${group.id}\` ${group.title}`,
    group.roles.join(', '),
    group.routes.map((route) => `\`${route}\``).join('<br>'),
    group.acceptance.join('<br>'),
    group.edgeCases.join('<br>'),
  ].map((cell) => String(cell).replace(/\|/g, '\\|')).join(' | ')).map((row) => `| ${row} |`),
  '',
  '## Route Inventory',
  '',
  '| Route | Groups | Kind | Scope | Buttons | Inputs | Selects | Checks | Forms | Modals | Tabs | Uploads | States L/E/Empty/S/D | Test IDs | Files |',
  '| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | --- |',
  ...inventory.map((r) => {
    const states = `${r.states.loading}/${r.states.error}/${r.states.empty}/${r.states.success}/${r.states.disabled}`;
    return `| \`${r.route}\` | ${r.surfaceGroups.map((g) => `\`${g}\``).join('<br>')} | ${r.kind} | ${r.scopes.join('<br>') || '-'} | ${r.controls.buttons} | ${r.controls.inputs + r.controls.textareas} | ${r.controls.selects} | ${r.controls.checkboxes} | ${r.controls.forms} | ${r.controls.modals + r.controls.popovers} | ${r.controls.tabs} | ${r.controls.uploads} | ${states} | ${r.controls.testIds} | ${r.files.map((f) => `\`${f}\``).join('<br>')} |`;
  }),
  '',
];

await fs.writeFile(path.join(outDir, 'web-surface-inventory.md'), md.join('\n'));
console.log(`Wrote ${inventory.length} classified route inventory rows to ${outDir}`);
if (unclassifiedRoutes.length > 0) {
  console.error(`Unclassified routes: ${unclassifiedRoutes.join(', ')}`);
  process.exitCode = 1;
}
