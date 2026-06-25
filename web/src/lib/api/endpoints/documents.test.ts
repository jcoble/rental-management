import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(new URL('./documents.ts', import.meta.url), 'utf8');
const proxySource = readFileSync(
	new URL('../../../routes/document-file/[id]/+server.ts', import.meta.url),
	'utf8'
);
const depositDetailSource = readFileSync(
	new URL('../../../routes/(protected)/deposits/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const inspectionDetailSource = readFileSync(
	new URL('../../../routes/(protected)/maintenance/inspections/[id]/+page.svelte', import.meta.url),
	'utf8'
);

describe('document file endpoints', () => {
	it('builds same-origin stored-file proxy URLs with optional thumbnails', () => {
		assert.match(endpointSource, /export function documentFileHref/);
		assert.match(endpointSource, /`\/document-file\/\$\{id\}`/);
		assert.match(endpointSource, /thumb \? `\$\{base\}\?thumb=true` : base/);
	});

	it('downloads and previews generic documents through the same-origin proxy', () => {
		assert.match(endpointSource, /fetch\(documentFileHref\(id\)/);
		assert.doesNotMatch(endpointSource, /CLIENT_API_BASE_URL/);
		assert.doesNotMatch(endpointSource, /Authorization/);
	});

	it('forwards thumbnail requests through the stored-file proxy route', () => {
		assert.match(proxySource, /async \(\{ params, cookies, url \}\)/);
		assert.match(proxySource, /url\.searchParams\.get\('thumb'\)/);
		assert.match(proxySource, /documents\/\$\{params\.id\}\/file\$\{thumb \? '\?thumb=true' : ''\}/);
	});

	it('requests stored-file thumbnails for generic document image previews', () => {
		assert.match(depositDetailSource, /fileObjectUrl\(doc\.id, \{ thumb: true \}\)/);
		assert.match(inspectionDetailSource, /fileObjectUrl\(storedFileId, \{ thumb: true \}\)/);
	});
});
