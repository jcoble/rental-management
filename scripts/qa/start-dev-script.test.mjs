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

	it('leaves notification provider configuration to user secrets and explicit environment variables', () => {
		const buildSection = script.indexOf('Building .NET hosts');

		assert.ok(buildSection > -1, 'expected an explicit serial .NET build section');

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
			assert.doesNotMatch(script, new RegExp(`export ${key}=""`), `${key} must not be blanked by start-dev.sh`);
		}
	});
});
