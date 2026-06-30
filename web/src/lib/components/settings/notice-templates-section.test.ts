import { test } from 'node:test';
import assert from 'node:assert/strict';
import { insertFieldToken } from './insert-field-token.ts';

test('insertFieldToken inserts {{token}} at the caret', () => {
	const result = insertFieldToken('Hi , welcome', 3, 'tenant_name');
	assert.equal(result.text, 'Hi {{tenant_name}}, welcome');
	assert.equal(result.caret, 3 + '{{tenant_name}}'.length);
});

test('insertFieldToken appends at the end when caret is at end of text', () => {
	const result = insertFieldToken('Dear ', 5, 'property_name');
	assert.equal(result.text, 'Dear {{property_name}}');
	assert.equal(result.caret, '{{property_name}}'.length + 5);
});

test('insertFieldToken prepends when caret is 0', () => {
	const result = insertFieldToken('rest', 0, 'amount');
	assert.equal(result.text, '{{amount}}rest');
	assert.equal(result.caret, '{{amount}}'.length);
});

test('insertFieldToken clamps a negative caret to the start', () => {
	const result = insertFieldToken('abc', -5, 'x');
	assert.equal(result.text, '{{x}}abc');
	assert.equal(result.caret, '{{x}}'.length);
});

test('insertFieldToken clamps an over-long caret to the end', () => {
	const result = insertFieldToken('abc', 99, 'x');
	assert.equal(result.text, 'abc{{x}}');
	assert.equal(result.caret, 'abc{{x}}'.length);
});
