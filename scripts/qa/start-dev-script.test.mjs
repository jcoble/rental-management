import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const script = readFileSync(new URL('../start-dev.sh', import.meta.url), 'utf8');

describe('start-dev launcher', () => {
	it('builds .NET hosts serially before launching no-build API and Engine processes', () => {
		const buildSection = script.indexOf('Building .NET hosts');
		const engineLaunch = script.indexOf('Starting Engine');
		const apiLaunch = script.indexOf('Starting API');

		assert.ok(buildSection > -1, 'expected an explicit serial .NET build section');
		assert.ok(buildSection < engineLaunch, 'build must happen before starting Engine');
		assert.ok(buildSection < apiLaunch, 'build must happen before starting API');

		assert.match(script, /dotnet build RentalCommand\.Engine\b/);
		assert.match(script, /dotnet build RentalCommand\.Api\b/);
		assert.match(script, /dotnet run --no-build --project RentalCommand\.Engine\b/);
		assert.match(script, /dotnet run --no-build --project RentalCommand\.Api\b/);
	});

	it('suppresses real outbound notification providers unless explicitly enabled', () => {
		const guard = script.indexOf('ALLOW_EXTERNAL_NOTIFICATIONS="${ALLOW_EXTERNAL_NOTIFICATIONS:-0}"');
		const buildSection = script.indexOf('Building .NET hosts');

		assert.ok(guard > -1, 'expected an explicit opt-in guard for external notifications');
		assert.ok(guard < buildSection, 'provider suppression must run before API/Engine launch');
		assert.match(script, /ALLOW_EXTERNAL_NOTIFICATIONS=1/);

		for (const key of [
			'Notifications__SendGrid__ApiKey',
			'Notifications__SendGrid__FromEmail',
			'Notifications__Smtp__Host',
			'Notifications__Smtp__Username',
			'Notifications__Smtp__Password',
			'Notifications__Smtp__FromEmail',
			'Notifications__SignalWire__ProjectId',
			'Notifications__SignalWire__Token',
			'Notifications__SignalWire__SpaceUrl',
			'Notifications__SignalWire__FromNumber',
			'Notifications__Twilio__AccountSid',
			'Notifications__Twilio__AuthToken',
			'Notifications__Twilio__FromNumber',
			'Notifications__Telnyx__ApiKey',
			'Notifications__Telnyx__FromNumber',
			'Notifications__Vonage__ApiKey',
			'Notifications__Vonage__ApiSecret',
			'Notifications__Vonage__FromNumber'
		]) {
			assert.match(script, new RegExp(`export ${key}=""`), `expected ${key} to be blanked`);
		}
	});
});
