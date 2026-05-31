import { defineConfig, devices } from '@playwright/test';

/**
 * Minimal E2E config for Rental Command's web app. Targets the dev server
 * (Vite, https self-signed, port 5667). A full green run requires both the
 * .NET API (https://localhost:5666) and this web app booted with the seeded
 * admin account; `--list` works without either.
 *
 * Override the target with PW_BASE_URL and credentials with PW_EMAIL /
 * PW_PASSWORD when running against a different environment.
 */
export const BASE_URL = process.env.PW_BASE_URL ?? 'https://localhost:5667';

export default defineConfig({
	testDir: './e2e',
	timeout: 30_000,
	expect: { timeout: 10_000 },
	fullyParallel: false,
	workers: 1,
	retries: 0,
	reporter: process.env.CI ? [['github'], ['list']] : [['list']],
	use: {
		baseURL: BASE_URL,
		ignoreHTTPSErrors: true,
		trace: 'on-first-retry',
		screenshot: 'only-on-failure',
	},
	projects: [
		{
			name: 'chromium',
			use: { ...devices['Desktop Chrome'] },
		},
	],
});
