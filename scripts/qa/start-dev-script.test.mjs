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
});
