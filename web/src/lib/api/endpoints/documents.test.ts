import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(new URL('./documents.ts', import.meta.url), 'utf8');
const proxySource = readFileSync(new URL('../../../routes/document-file/[id]/+server.ts', import.meta.url), 'utf8');
const authenticatedFileProxySources = [
	'../../../routes/application-file/[id]/+server.ts',
	'../../../routes/document-file/[id]/+server.ts',
	'../../../routes/expense-file/[id]/+server.ts',
	'../../../routes/scan-file/[id]/+server.ts',
	'../../../routes/workorder-file/[id]/+server.ts'
].map((path) => [path, readFileSync(new URL(path, import.meta.url), 'utf8')] as const);
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

	it('sends caller-generated operation identities for document mutations', () => {
		assert.match(endpointSource, /form\.append\('clientOperationId', clientOperationId\)/);
		assert.match(endpointSource, /clientOperationId: string/);
		assert.doesNotMatch(endpointSource, /clientOperationId: string = crypto\.randomUUID\(\)/);
		assert.match(endpointSource, /clientOperationId=\$\{encodeURIComponent\(clientOperationId\)\}/);
	});

	it('forwards thumbnail requests through the stored-file proxy route', () => {
		assert.match(proxySource, /async \(\{ params, locals, url \}\)/);
		assert.match(proxySource, /url\.searchParams\.get\('thumb'\)/);
		assert.match(proxySource, /documents\/\$\{params\.id\}\/file\$\{thumb \? '\?thumb=true' : ''\}/);
	});

	it('uses the hook-refreshed token for authenticated file proxies', () => {
		for (const [path, source] of authenticatedFileProxySources) {
			assert.match(source, /locals\.accessToken/, path);
			assert.doesNotMatch(source, /getAccessToken\(cookies\)/, path);
		}
	});

	it('requests stored-file thumbnails for generic document image previews', () => {
		assert.match(depositDetailSource, /fileObjectUrl\(doc\.id, \{ thumb: true \}\)/);
		assert.match(inspectionDetailSource, /fileObjectUrl\(storedFileId, \{ thumb: true \}\)/);
	});
});
