import assert from 'node:assert/strict';
import test from 'node:test';

import {
	assistantActionFieldLabel,
	formatAssistantMoney,
	looksLikeAssistantActionCommand
} from './actions.ts';

test('looksLikeAssistantActionCommand detects expense write commands', () => {
	assert.equal(
		looksLikeAssistantActionCommand('log a $200 plumbing expense for Eastland'),
		true
	);
	assert.equal(looksLikeAssistantActionCommand('record paid receipt for roof repair'), true);
});

test('looksLikeAssistantActionCommand leaves read questions on Q&A', () => {
	assert.equal(looksLikeAssistantActionCommand("who's late on rent?"), false);
	assert.equal(looksLikeAssistantActionCommand('how much did I collect this month?'), false);
});

test('formatAssistantMoney and field labels are stable for draft cards', () => {
	assert.equal(formatAssistantMoney(123.4), '$123.40');
	assert.equal(formatAssistantMoney(null), 'unknown amount');
	assert.equal(assistantActionFieldLabel('amount'), 'amount');
	assert.equal(assistantActionFieldLabel('propertyName'), 'property name');
});
