import { cleanup, fireEvent, render, waitFor } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import LeaseManagementDetailHarness from './LeaseManagementDetailHarness.svelte';

const mocks = vi.hoisted(() => ({
	relationship: vi.fn(),
	agreements: vi.fn(),
	addenda: vi.fn(),
	household: vi.fn(),
	downloadArtifact: vi.fn(),
	showError: vi.fn()
}));

vi.mock('$app/state', () => ({
	page: {
		data: {
			access: {
				selectedContext: { activeExperience: 'Leasing' },
				navigation: [{ experience: 'Leasing', capabilityKeys: ['rentals.manage'] }]
			}
		}
	}
}));

vi.mock('$lib/api/endpoints/lease-managements', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/lease-managements')>(
		'$lib/api/endpoints/lease-managements'
	);
	return {
		...actual,
		leaseManagements: {
			...actual.leaseManagements,
			get: mocks.relationship,
			agreements: mocks.agreements,
			getCurrentPartiesContext: mocks.household,
			downloadArtifact: mocks.downloadArtifact
		}
	};
});

vi.mock('$lib/api/endpoints/lease-addendums', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/lease-addendums')>(
		'$lib/api/endpoints/lease-addendums'
	);
	return {
		...actual,
		leaseAddendums: {
			...actual.leaseAddendums,
			listPage: mocks.addenda
		}
	};
});

vi.mock('$lib/utils/toast', async () => {
	const actual = await vi.importActual<typeof import('$lib/utils/toast')>('$lib/utils/toast');
	return { ...actual, showError: mocks.showError };
});

vi.mock('$lib/components/leases/AddendumCreateDialog.svelte', async () => ({
	default: (await import('./stubs/AddendumCreateDialogStub.svelte')).default
}));
vi.mock('$lib/components/leases/AgreementSuccessorDialog.svelte', async () => ({
	default: (await import('./stubs/AgreementSuccessorDialogStub.svelte')).default
}));
vi.mock('$lib/components/leases/AgreementDraftDialog.svelte', async () => ({
	default: (await import('./stubs/AgreementDraftDialogStub.svelte')).default
}));
vi.mock('$lib/components/leases/AgreementSignatureProgress.svelte', async () => ({
	default: (await import('./stubs/AgreementSignatureProgressStub.svelte')).default
}));

vi.mock('$lib/components/leases/AddendumCorrectionDialog.svelte', async () => ({
	default: (await import('./stubs/NoopLeaseChild.svelte')).default
}));
vi.mock('$lib/components/leases/AddendumDraftDialog.svelte', async () => ({
	default: (await import('./stubs/NoopLeaseChild.svelte')).default
}));
vi.mock('$lib/components/leases/AgreementIssuedRecoveryDialog.svelte', async () => ({
	default: (await import('./stubs/NoopLeaseChild.svelte')).default
}));
vi.mock('$lib/components/leases/EndingDispositionDialog.svelte', async () => ({
	default: (await import('./stubs/NoopLeaseChild.svelte')).default
}));
vi.mock('$lib/components/leases/HouseholdManagementDialog.svelte', async () => ({
	default: (await import('./stubs/NoopLeaseChild.svelte')).default
}));
vi.mock('$lib/components/leases/LeaseEvictionCasesSection.svelte', async () => ({
	default: (await import('./stubs/NoopLeaseChild.svelte')).default
}));
vi.mock('$lib/components/leases/PossessionActions.svelte', async () => ({
	default: (await import('./stubs/NoopLeaseChild.svelte')).default
}));
vi.mock('$lib/components/notices/TenantNoticeDialog.svelte', async () => ({
	default: (await import('./stubs/NoopLeaseChild.svelte')).default
}));

afterEach(() => cleanup());

const leaseManagementId = 19;
const governingAgreementId = 101;
const draftAgreementId = 102;

const issuedArtifact = {
	legalDocumentArtifactId: 41,
	publicId: 'issued-41',
	artifactKind: 'Issued',
	fileName: 'lease-issued.pdf',
	contentType: 'application/pdf',
	byteLength: 12,
	contentSha256: 'issued-hash',
	createdAtUtc: '2027-01-01T12:00:00Z'
};

const executedArtifact = {
	...issuedArtifact,
	legalDocumentArtifactId: 42,
	publicId: 'executed-42',
	artifactKind: 'Executed',
	fileName: 'lease-executed.pdf',
	contentSha256: 'executed-hash'
};

const governingAgreement = {
	leaseManagementId,
	leaseAgreementId: governingAgreementId,
	publicId: 'agreement-101',
	versionNumber: 3,
	agreementNumber: 'LEASE-101',
	changeType: 'Original',
	correctionReason: null,
	replacesAgreementId: null,
	renewsAgreementId: null,
	reissuesAgreementId: null,
	reissueReason: null,
	hasLiveReissue: false,
	termType: 'FixedTerm',
	termStartOn: '2027-01-01',
	termEndOn: '2027-12-31',
	governingFromOn: '2027-01-01',
	supersededEffectiveOn: null,
	baseRentAmount: 1800,
	agreementStatus: 'Issued',
	isGoverning: true,
	signerCount: 1,
	hasSourceScan: false,
	signatureRequestId: 7001,
	hasSignatureRequest: true,
	issuedArtifact,
	executedArtifact,
	issuedAtUtc: '2027-01-01T12:00:00Z',
	fullyExecutedAtUtc: null,
	voidedAtUtc: null,
	draftCanceledAtUtc: null,
	draftCanceledByUserId: null,
	draftCancellationReason: null,
	createdAtUtc: '2027-01-01T12:00:00Z',
	updatedAtUtc: '2027-01-01T12:00:00Z'
};

const draftAgreement = {
	...governingAgreement,
	leaseAgreementId: draftAgreementId,
	publicId: 'agreement-102',
	versionNumber: 4,
	agreementNumber: 'LEASE-102',
	agreementStatus: 'Draft',
	isGoverning: false,
	hasSignatureRequest: false,
	signatureRequestId: null,
	issuedArtifact: null,
	executedArtifact: null,
	replacesAgreementId: governingAgreementId,
	issuedAtUtc: null,
	fullyExecutedAtUtc: null
};

const relationship = {
	summary: {
		leaseManagementId,
		leaseManagementPublicId: 'relationship-19',
		relationshipNumber: 'LM-19',
		propertyId: 7,
		propertyName: 'Example House',
		unitId: 8,
		unitNumber: '1A',
		lifecycle: 'Active',
		businessDate: '2027-01-01',
		leaseAgreementId: governingAgreementId,
		agreementNumber: 'LEASE-101',
		agreementStatus: 'Issued',
		termStartOn: '2027-01-01',
		termEndOn: '2027-12-31',
		baseRentAmount: 1800,
		upcomingLeaseAgreementId: draftAgreementId,
		upcomingAgreementNumber: 'LEASE-102',
		upcomingAgreementStatus: 'Draft',
		upcomingTermStartOn: '2028-01-01',
		upcomingTermEndOn: '2028-12-31',
		tenantAccountId: 55,
		primaryTenantId: 88,
		primaryTenantName: 'Ada Lovelace',
		currentPartyCount: 1,
		currentResidentCount: 1,
		currentFinanciallyResponsiblePartyCount: 1,
		hasReconciliationException: false,
		hasGoverningAgreementWithoutPossession: false,
		plannedPossessionAtUtc: null,
		possessionGivenAtUtc: '2027-01-01T12:00:00Z',
		plannedMoveOutAtUtc: null,
		possessionReturnedAtUtc: null,
		accountClosedAtUtc: null,
		canceledAtUtc: null,
		endingDisposition: 'Undecided',
		endingDispositionDecidedAtUtc: null,
		endingDispositionDecidedByUserId: null,
		noticeGivenAtUtc: null,
		cancellationReasonCode: null,
		cancellationNote: null,
		updatedAtUtc: '2027-01-01T12:00:00Z'
	},
	endingDisposition: 'Undecided',
	parties: [],
	agreementCount: 2,
	addendumCount: 0,
	legalArtifactCount: 2
};

function configureQueries() {
	mocks.relationship.mockResolvedValue(relationship);
	mocks.agreements.mockResolvedValue({ items: [governingAgreement, draftAgreement], totalCount: 2, skip: 0, take: 10 });
	mocks.addenda.mockResolvedValue({ items: [], totalCount: 0, skip: 0, take: 10 });
	mocks.household.mockResolvedValue({ parties: [], activeTenantUserAccesses: [] });
}

async function renderDetail() {
	configureQueries();
	const view = render(LeaseManagementDetailHarness, { props: { leaseManagementId } });
	await waitFor(() => expect(view.getByTestId(`agreement-version-${governingAgreementId}`)).toBeTruthy());
	return view;
}

describe('rendered lease-management action wiring', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		configureQueries();
	});

	it('opens issued and executed PDFs synchronously, then navigates the returned tabs with exact payloads', async () => {
		const events: string[] = [];
		const openedWindows = [
			{ location: { href: '' }, close: vi.fn() },
			{ location: { href: '' }, close: vi.fn() }
		];
		const open = vi.spyOn(window, 'open').mockImplementation(() => {
			const opened = openedWindows.shift();
			events.push('open');
			return opened as unknown as Window;
		});
		const createObjectURL = vi.fn()
			.mockReturnValueOnce('blob:issued-41')
			.mockReturnValueOnce('blob:executed-42');
		Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: createObjectURL });
		Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: vi.fn() });

		let resolveIssued!: (blob: Blob) => void;
		let resolveExecuted!: (blob: Blob) => void;
		const issuedDownload = new Promise<Blob>((resolve) => { resolveIssued = resolve; });
		const executedDownload = new Promise<Blob>((resolve) => { resolveExecuted = resolve; });
		mocks.downloadArtifact.mockImplementation((requestedLeaseManagementId: number, agreementId: number, artifactId: number) => {
			events.push(`download:${requestedLeaseManagementId}:${agreementId}:${artifactId}`);
			return artifactId === issuedArtifact.legalDocumentArtifactId ? issuedDownload : executedDownload;
		});

		const view = await renderDetail();
		await fireEvent.click(view.getByTestId(`lease-agreement-${governingAgreementId}-view-issued`));
		expect(events).toEqual(['open', `download:${leaseManagementId}:${governingAgreementId}:${issuedArtifact.legalDocumentArtifactId}`]);
		expect(openedWindows).toHaveLength(1);
		const issuedWindow = (open.mock.results[0]?.value ?? null) as typeof openedWindows[number];
		expect(issuedWindow.location.href).toBe('');
		resolveIssued(new Blob(['issued']));
		await waitFor(() => expect(issuedWindow.location.href).toBe('blob:issued-41'));

		await fireEvent.click(view.getByTestId(`lease-agreement-${governingAgreementId}-view-executed`));
		expect(events).toEqual([
			'open',
			`download:${leaseManagementId}:${governingAgreementId}:${issuedArtifact.legalDocumentArtifactId}`,
			'open',
			`download:${leaseManagementId}:${governingAgreementId}:${executedArtifact.legalDocumentArtifactId}`
		]);
		const executedWindow = (open.mock.results[1]?.value ?? null) as typeof openedWindows[number];
		resolveExecuted(new Blob(['executed']));
		await waitFor(() => expect(executedWindow.location.href).toBe('blob:executed-42'));
		expect(mocks.downloadArtifact.mock.calls).toEqual([
			[leaseManagementId, governingAgreementId, issuedArtifact.legalDocumentArtifactId],
			[leaseManagementId, governingAgreementId, executedArtifact.legalDocumentArtifactId]
		]);
	});

	it('surfaces blocked and failed PDF opens without leaving a placeholder open', async () => {
		const open = vi.spyOn(window, 'open');
		open.mockReturnValueOnce(null);
		mocks.downloadArtifact.mockRejectedValueOnce(new Error('should not be requested'));
		const view = await renderDetail();
		await fireEvent.click(view.getByTestId(`lease-agreement-${governingAgreementId}-view-issued`));
		expect(mocks.downloadArtifact).not.toHaveBeenCalled();
		expect(mocks.showError).toHaveBeenCalledWith('Could not open the lease document in a new tab.');

		const opened = { location: { href: '' }, close: vi.fn() };
		open.mockReturnValueOnce(opened as unknown as Window);
		mocks.downloadArtifact.mockReset();
		mocks.downloadArtifact.mockRejectedValueOnce(new Error('download unavailable'));
		await fireEvent.click(view.getByTestId(`lease-agreement-${governingAgreementId}-view-executed`));
		await waitFor(() => expect(opened.close).toHaveBeenCalledOnce());
		expect(mocks.showError).toHaveBeenLastCalledWith('download unavailable');
	});

	it('passes the governing agreement and relationship IDs to AddendumCreateDialog', async () => {
		const view = await renderDetail();
		await fireEvent.click(view.getAllByRole('button', { name: 'Add a page' })[0]);
		expect(view.getByTestId('stub-addendum-create-dialog')).toBeTruthy();
		expect(view.getByTestId('stub-addendum-lease-management-id').textContent).toBe(String(leaseManagementId));
		expect(view.getByTestId('stub-addendum-property-id').textContent).toBe('7');
		expect(view.getByTestId('stub-addendum-base-agreement-id').textContent).toBe(String(governingAgreementId));
		expect(view.getByTestId('stub-addendum-base-agreement-number').textContent).toBe('LEASE-101');
	});

	it.each([
		['Fix a typo', 'Correction'],
		['Rewrite the whole lease', 'Restatement'],
		['Renew it', 'Renewal'],
		['Switch to month-to-month', 'MonthToMonth']
	] as const)('passes %s to AgreementSuccessorDialog with the selected source agreement', async (label, changeType) => {
		const view = await renderDetail();
		await fireEvent.click(view.getByRole('button', { name: label }));
		expect(view.getByTestId('stub-agreement-successor-dialog')).toBeTruthy();
		expect(view.getByTestId('stub-successor-lease-management-id').textContent).toBe(String(leaseManagementId));
		expect(view.getByTestId('stub-successor-source-agreement-id').textContent).toBe(String(governingAgreementId));
		expect(view.getByTestId('stub-successor-source-agreement-number').textContent).toBe('LEASE-101');
		expect(view.getByTestId('stub-successor-change-type').textContent).toBe(changeType);
	});

	it('passes the returned draft ID and selected source agreement to AgreementDraftDialog', async () => {
		const view = await renderDetail();
		await fireEvent.click(view.getByRole('button', { name: 'Edit draft' }));
		expect(view.getByTestId('stub-agreement-draft-dialog')).toBeTruthy();
		expect(view.getByTestId('stub-draft-lease-management-id').textContent).toBe(String(leaseManagementId));
		expect(view.getByTestId('stub-draft-agreement-id').textContent).toBe(String(draftAgreementId));
		expect(view.getByTestId('stub-draft-source-id').textContent).toBe(String(governingAgreementId));
		expect(view.getByTestId('stub-draft-source-number').textContent).toBe('LEASE-101');
	});

	it('passes the selected agreement IDs to AgreementSignatureProgress', async () => {
		const view = await renderDetail();
		await fireEvent.click(view.getByRole('button', { name: 'View signing progress' }));
		expect(view.getByTestId('stub-agreement-signature-progress')).toBeTruthy();
		expect(view.getByTestId('stub-signature-lease-management-id').textContent).toBe(String(leaseManagementId));
		expect(view.getByTestId('stub-signature-agreement-id').textContent).toBe(String(governingAgreementId));
		expect(view.getByTestId('stub-signature-agreement-number').textContent).toBe('LEASE-101');
	});
});
