import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { BLOG_POSTS, getBlogPost } from './articles.ts';

const expectedSlugs = [
	'why-your-rental-spreadsheet-will-fail-an-audit',
	'lease-ledgers-what-every-landlord-should-track',
	'security-deposits-bookkeeping-mistake-that-costs-landlords'
];

describe('public bookkeeping blog', () => {
	it('ships the three stable landlord bookkeeping posts', () => {
		assert.deepEqual(BLOG_POSTS.map((post) => post.slug), expectedSlugs);
		assert.equal(new Set(expectedSlugs).size, BLOG_POSTS.length);
		for (const slug of expectedSlugs) assert.equal(getBlogPost(slug)?.slug, slug);
	});

	it('provides useful SEO metadata, article depth, and a sign-up action', () => {
		for (const post of BLOG_POSTS) {
			assert.ok(post.seoTitle.length >= 45 && post.seoTitle.length <= 70);
			assert.ok(post.description.length >= 120 && post.description.length <= 165);
			assert.ok(post.sections.length >= 4);
			assert.ok(post.sections.every((section) => section.paragraphs.join(' ').length >= 100));
			assert.equal(post.cta.href, '/register');
			assert.ok(post.cta.label.length > 0);
		}
	});
});
