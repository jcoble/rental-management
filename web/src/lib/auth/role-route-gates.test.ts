import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const protectedLayout = readFileSync(
	new URL('../../routes/(protected)/+layout.server.ts', import.meta.url),
	'utf8'
);
const appShell = readFileSync(new URL('../components/AppShell.svelte', import.meta.url), 'utf8');
const leasingListPage = readFileSync(
	new URL('../components/leasing/LeasingListPage.svelte', import.meta.url),
	'utf8'
);
const experiencePolicy = readFileSync(new URL('./experience-policy.ts', import.meta.url), 'utf8');
const propertiesList = readFileSync(
	new URL('../../routes/(protected)/properties/+page.svelte', import.meta.url),
	'utf8'
);
const propertyDetail = readFileSync(
	new URL('../../routes/(protected)/properties/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const technicianApi = readFileSync(
	new URL('../api/endpoints/technician.ts', import.meta.url),
	'utf8'
);
const technicianDetail = readFileSync(
	new URL('../../routes/(protected)/my-work/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const workOrderDetail = readFileSync(
	new URL('../components/records/WorkOrderDetail.svelte', import.meta.url),
	'utf8'
);
const typedMessageDetail = readFileSync(
	new URL('../../routes/(protected)/messages/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const scanPage = readFileSync(
	new URL('../../routes/(protected)/scan/+page.svelte', import.meta.url),
	'utf8'
);
const settingsPage = readFileSync(
	new URL('../../routes/(protected)/settings/+page.svelte', import.meta.url),
	'utf8'
);

test('direct staff routes use the same role-capability gates as navigation', () => {
	assert.match(
		protectedLayout,
		/import \{ canAccessRoute, CAPABILITY \} from '\$lib\/auth\/experience-policy'/
	);
	assert.match(protectedLayout, /canAccessRoute\(url\.pathname, activeExperience, effectiveCapabilities\)/);
	assert.match(
		appShell,
		/import \{ canAccessRoute, CAPABILITY, safeLandingForAccess \} from '\$lib\/auth\/experience-policy'/
	);
	assert.match(appShell, /canAccessRoute\(item\.href, activeExperience, activeCapabilities\)/);
	assert.match(experiencePolicy, /prefix: '\/admin\/users'/);
	assert.match(experiencePolicy, /prefix: '\/settings'/);
	assert.match(experiencePolicy, /prefix: '\/settings\/notifications\/my-alerts'/);
	assert.match(experiencePolicy, /prefix: '\/settings\/notifications\/team-routing'/);
	assert.match(experiencePolicy, /prefix: '\/onboarding'/);
	assert.match(experiencePolicy, /prefix: '\/audit'/);
	assert.match(experiencePolicy, /prefix: '\/ai'/);
});

test('property mutation controls use active-experience capability gates', () => {
	for (const source of [propertiesList, propertyDetail]) {
		assert.match(source, /activeExperience === 'Management'/);
		assert.match(source, /activeCapabilities\.has\(CAPABILITY\.rentalsManage\)/);
		assert.match(source, /\{#if canManageRentals\}/);
	}
	assert.match(propertiesList, /emptyOnAction=\{canManageRentals \? openCreate : undefined\}/);
	assert.match(
		propertyDetail,
		/PropertyLoansSection propertyId=\{id\} canManage=\{canManageMoneyExpenses\}/
	);
	assert.match(
		propertyDetail,
		/PropertyDispositionsSection propertyId=\{id\} canManage=\{canManageRentals\}/
	);
});

test('rental collections keep owners in Rentals and leasing inbox uses canonical conversation state', () => {
	const rentalsStart = appShell.indexOf("id: 'rentals'");
	const workStart = appShell.indexOf("id: 'work'", rentalsStart);
	const rentalsGroup = appShell.slice(rentalsStart, workStart);
	const settingsStart = appShell.indexOf("const settingsGroup: NavGroup");
	const settingsEnd = appShell.indexOf('const navGlyphByHref', settingsStart);
	const settings = appShell.slice(settingsStart, settingsEnd);

	assert.match(rentalsGroup, /href: '\/owners', label: 'Owners'/);
	assert.doesNotMatch(settings, /href: '\/owners'/);
	assert.match(
		leasingListPage,
		/kind === 'inbox'\) return `\/leasing\/conversations\/\$\{item\.id\}`/
	);
	assert.doesNotMatch(leasingListPage, /kind === 'inbox'\) return `\/messages/);
});

test('technician commands stay on the canonical assignment-scoped mutation', () => {
	assert.match(technicianApi, /\/technician\/assignments/);
	assert.match(technicianApi, /api\.patch\(`\/technician\/assignments\/\$\{id\}`/);
	assert.doesNotMatch(technicianApi, /\/work-orders\//);
	assert.doesNotMatch(technicianApi, /assigned-update/);
	assert.match(workOrderDetail, /technician\.update/);
	assert.match(technicianDetail, /data-testid="assignment-progress-update"/);
	assert.match(technicianDetail, /workOrderStatusActionTargets\(assignment\.data\.status, true\)/);
	assert.match(technicianDetail, /technician\.update\(work\.id/);
	assert.match(technicianDetail, /expectedUpdatedAtUtc: work\.updatedAtUtc/);
	assert.match(technicianDetail, /data-testid="assignment-progress-submit"/);
	assert.doesNotMatch(technicianDetail, /technician\.updateAssignment/);
	assert.match(technicianDetail, /activeCapabilities\.has\(CAPABILITY\.assignedWorkUpdate\)/);
	assert.match(technicianDetail, /activeCapabilities\.has\(CAPABILITY\.assignedWorkTimeMaterialsManage\)/);
	assert.match(technicianDetail, /activeCapabilities\.has\(CAPABILITY\.assignedWorkConverse\)/);
	assert.match(technicianDetail, /\{#if allowedEntryKinds\.length > 0\}/);
	assert.match(technicianDetail, /\{#if canConverse\}<section id="conversation"/);
	assert.match(scanPage, /technician\.assignments\(\{ openOnly: true, sort: 'scheduledForUtc', take: 50 \}\)/);
	assert.doesNotMatch(scanPage, /workOrders\.listPage/);
});

test('staff action URLs open route-backed message details without query aliases', () => {
	assert.match(typedMessageDetail, /import MessagesPage from '\.\.\/\+page\.svelte'/);
	assert.match(experiencePolicy, /prefix: '\/messages'/);
	assert.doesNotMatch(typedMessageDetail, /conversationId=/);
});

test('notification settings expose independent personal, team, and tenant policy capabilities', () => {
	assert.match(settingsPage, /canManageTeamRouting = \$derived\(hasCapability\('notifications\.manage'\)\)/);
	assert.match(settingsPage, /canManageTenantNotices = \$derived\(hasCapability\('notifications\.tenant-notices\.manage'\)\)/);
	assert.match(settingsPage, /\{#if canManageTeamRouting\}<a[\s\S]*href="\/settings\/notifications\/team-routing"/);
	assert.match(settingsPage, /\{#if canManageTenantNotices\}<a[\s\S]*href="\/settings\/notifications\/tenant-notices"/);
});
