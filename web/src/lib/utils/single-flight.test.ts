import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createSingleFlight, createSingleFlightWithReuse } from './single-flight.ts';

// Existing behaviour: overlapping calls share one in-flight run.
test('createSingleFlight coalesces overlapping calls', async () => {
	let calls = 0;
	let release!: () => void;
	const gate = new Promise<void>((r) => (release = r));
	const op = async () => {
		calls++;
		await gate;
		return calls;
	};
	const sf = createSingleFlight(op);
	const a = sf();
	const b = sf();
	release();
	assert.equal(await a, 1);
	assert.equal(await b, 1);
	assert.equal(calls, 1);
});

test('createSingleFlight runs again after the previous settled', async () => {
	let calls = 0;
	const op = async () => ++calls;
	const sf = createSingleFlight(op);
	await sf();
	await sf();
	assert.equal(calls, 2);
});

// New behaviour: a burst that is not perfectly overlapping still shares ONE run
// within the reuse window.
test('createSingleFlightWithReuse re-serves the last result within the window', async () => {
	let calls = 0;
	const op = async () => ++calls;
	const sf = createSingleFlightWithReuse(op, 1000);
	const first = await sf(); // runs once
	const second = await sf(); // within window -> reuse, no new run
	assert.equal(first, 1);
	assert.equal(second, 1);
	assert.equal(calls, 1);
});

test('createSingleFlightWithReuse refreshes again after the window elapses', async () => {
	let calls = 0;
	const op = async () => ++calls;
	const sf = createSingleFlightWithReuse(op, 20);
	await sf();
	await new Promise((r) => setTimeout(r, 40));
	await sf();
	assert.equal(calls, 2);
});

test('createSingleFlightWithReuse does NOT cache failures', async () => {
	let calls = 0;
	const op = async () => {
		calls++;
		throw new Error('boom');
	};
	const sf = createSingleFlightWithReuse(op, 1000);
	await assert.rejects(sf());
	await assert.rejects(sf());
	assert.equal(calls, 2); // each failure retried, no poisoned cache
});

test('createSingleFlightWithReuse coalesces strictly-overlapping calls too', async () => {
	let calls = 0;
	let release!: () => void;
	const gate = new Promise<void>((r) => (release = r));
	const op = async () => {
		calls++;
		await gate;
		return calls;
	};
	const sf = createSingleFlightWithReuse(op, 1000);
	const a = sf();
	const b = sf();
	const c = sf();
	release();
	assert.deepEqual(await Promise.all([a, b, c]), [1, 1, 1]);
	assert.equal(calls, 1);
});
