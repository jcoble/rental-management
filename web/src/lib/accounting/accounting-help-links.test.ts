import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, it } from 'node:test';
import { ACCOUNTING_HELP } from './accounting-help.ts';

const knowledgeBase = resolve(process.cwd(), '..', 'RentalCommand.Api', 'KnowledgeBase');

describe('accounting help links', () => {
	it('resolve every referenced article slug to matching knowledge-base content', () => {
		const slugs = new Set(Object.values(ACCOUNTING_HELP).map((entry) => entry.href.replace('/docs/', '')));

		for (const slug of slugs) {
			const articlePath = resolve(knowledgeBase, `${slug}.md`);
			assert.equal(existsSync(articlePath), true, `Missing knowledge-base article for /docs/${slug}`);
			const article = readFileSync(articlePath, 'utf8');
			assert.match(article, new RegExp(`^slug: ${slug}$`, 'm'));
			assert.match(article, /^summary: .+$/m);
		}
	});

	it('uses intentional docs URLs instead of an empty or default destination', () => {
		for (const [surface, entry] of Object.entries(ACCOUNTING_HELP)) {
			assert.match(entry.href, /^\/docs\/[a-z0-9-]+$/, `${surface} needs a stable docs URL`);
			assert.ok(entry.summary.length >= 40, `${surface} needs a useful plain-English summary`);
		}
	});
});
