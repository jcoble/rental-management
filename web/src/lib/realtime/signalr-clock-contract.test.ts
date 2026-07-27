import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const signalr = readFileSync(new URL('./signalr.ts', import.meta.url), 'utf8');

test('SignalR keepalive timers use the real clock under the dev simulation Date shim', () => {
	assert.match(signalr, /private runWithRealDate<T>\(operation: \(\) => T\): T/);
	assert.match(signalr, /const realDate = browser \? window\.__RealDate : undefined/);
	assert.match(signalr, /window\.Date = realDate/);
	assert.match(signalr, /window\.Date = simulatedDate/);

	const binding = signalr.slice(
		signalr.indexOf('private bindSignalRTimersToRealDate'),
		signalr.indexOf('/**\n\t * Build a fresh HubConnection')
	);
	assert.match(binding, /'_resetKeepAliveInterval'/);
	assert.match(binding, /'_resetTimeoutPeriod'/);
	assert.match(binding, /this\.runWithRealDate\(\(\) => original\.apply\(connection, args\)\)/);
	assert.match(signalr, /this\.bindSignalRTimersToRealDate\(connection\)/);
});
