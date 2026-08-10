import { cleanup, fireEvent, render, waitFor, within } from '@testing-library/svelte';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import LeaseCanonicalDialogsHarness from './LeaseCanonicalDialogsHarness.svelte';

const mocks = vi.hoisted(() => ({
	currentParties: vi.fn(),
	templates: vi.fn(),
	addendumCreate: vi.fn(),
	successorCreate: vi.fn(),
	effectiveSeries: vi.fn(),
	draft: vi.fn(),
	prepare: vi.fn(),
	issue: vi.fn(),
	progress: vi.fn(),
	resend: vi.fn(),
	showError: vi.fn(),
	showSuccess: vi.fn()
}));

vi.mock('$lib/api/endpoints/lease-managements', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/lease-managements')>(
		'$lib/api/endpoints/lease-managements'
	);
	return {
		...actual,
		leaseManagements: {
			...actual.leaseManagements,
			getCurrentPartiesContext: mocks.currentParties,
			createAgreementSuccessorDraft: mocks.successorCreate,
			getEffectiveAddendumSeries: mocks.effectiveSeries,
			getAgreementDraft: mocks.draft,
			prepareAgreementIssuance: mocks.prepare,
			issueAgreement: mocks.issue,
			getAgreementSignatureProgress: mocks.progress,
			resendAgreementInvitation: mocks.resend
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
			createDraft: mocks.addendumCreate
		}
	};
});

vi.mock('$lib/api/endpoints/document-templates', async () => {
	const actual = await vi.importActual<typeof import('$lib/api/endpoints/document-templates')>(
		'$lib/api/endpoints/document-templates'
	);
	return {
		...actual,
		documentTemplates: {
			...actual.documentTemplates,
			listPage: mocks.templates
		}
	};
});

vi.mock('$lib/utils/toast', async () => {
	const actual = await vi.importActual<typeof import('$lib/utils/toast')>('$lib/utils/toast');
	return { ...actual, showError: mocks.showError, showSuccess: mocks.showSuccess };
});

afterEach(() => cleanup());

const leaseManagementId = 19;
const governingAgreementId = 101;

const sourceAgreement = {
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
	issuedArtifact: null,
	executedArtifact: null,
	issuedAtUtc: '2027-01-01T12:00:00Z',
	fullyExecutedAtUtc: null,
	voidedAtUtc: null,
	draftCanceledAtUtc: null,
	draftCanceledByUserId: null,
	draftCancellationReason: null,
	createdAtUtc: '2027-01-01T12:00:00Z',
	updatedAtUtc: '2027-01-01T12:00:00Z'
};

const primaryParty = {
	leaseManagementPartyId: 201,
	leaseManagementId,
	tenantId: 88,
	tenantName: 'Ada Lovelace',
	email: 'ada@example.com',
	phone: null,
	role: 'PrimaryTenant',
	effectiveFrom: '2027-01-01',
	effectiveThrough: null,
	guarantorLegalNoticeEligible: true,
	isCurrent: true,
	canGrantTenantPortalAccess: true
};

const draftDetail = {
	leaseManagementId,
	leaseAgreementId: 102,
	publicId: 'draft-102',
	versionNumber: 4,
	draftRevision: 7,
	agreementNumber: 'LEASE-102',
	changeType: 'Correction',
	correctionReason: 'Fix the amount',
	termType: 'FixedTerm',
	termStartOn: '2027-01-01',
	termEndOn: '2027-12-31',
	governingFromOn: '2027-02-01',
	baseRentAmount: 1800,
	rentDueDay: 1,
	securityDepositObligation: 1800,
	lateFeeAmount: 75,
	gracePeriodDays: 5,
	currency: 'USD',
	termsSchemaVersion: 1,
	termsPayload: { source: 'test' },
	documentSourceVersionId: governingAgreementId,
	documentTemplateId: null,
	documentTemplateVersion: null,
	signers: [{
		leaseManagementPartyId: 201,
		tenantId: 88,
		signerRole: 'PrimaryTenant',
		nameSnapshot: 'Ada Lovelace',
		emailSnapshot: 'ada@example.com',
		signingOrder: 1,
		isRequired: true
	}],
	createdAtUtc: '2027-01-01T12:00:00Z',
	updatedAtUtc: '2027-01-01T12:00:00Z'
};

function resetDefaults() {
	mocks.currentParties.mockResolvedValue({ parties: [primaryParty], activeTenantUserAccesses: [] });
	mocks.templates.mockResolvedValue({
		items: [{ id: 301, name: 'Lease change template', version: 1, defaultForPortfolio: true }],
		totalCount: 1,
		skip: 0,
		take: 50
	});
	mocks.addendumCreate.mockResolvedValue({ leaseManagementId, leaseAddendumId: 401 });
	mocks.successorCreate.mockResolvedValue({ leaseManagementId, leaseAgreementId: 402 });
	mocks.effectiveSeries.mockResolvedValue({
		businessDate: '2027-01-01',
		decisionRequired: false,
		requiredDecisionCount: 0,
		series: []
	});
	mocks.draft.mockResolvedValue(draftDetail);
	mocks.prepare.mockResolvedValue({
		pendingUploadId: 'upload-7',
		draftRevision: 7,
		documentSourceVersionId: governingAgreementId,
		issuanceFingerprint: 'fingerprint-7',
		storageKey: 'storage/7',
		fileName: 'lease-102.pdf',
		fileSize: 1234,
		contentSha256: 'hash-7'
	});
	mocks.issue.mockResolvedValue({
		leaseManagementId,
		leaseAgreementId: 102,
		signatureRequestPublicId: 'signature-102',
		signatureRequestId: 9001,
		issuedArtifactId: 901,
		replayed: false
	});
	mocks.progress.mockResolvedValue({
		leaseManagementId,
		leaseAgreementId: governingAgreementId,
		subject: 'Lease 101',
		status: 'DeliveryFailed',
		totalSignerCount: 1,
		requiredSignerCount: 1,
		signedSignerCount: 0,
		declinedSignerCount: 0,
		issuedArtifactReady: true,
		executedArtifactReady: false,
		preparedAtUtc: '2027-01-01T12:00:00Z',
		providerAcceptedAtUtc: null,
		completedAtUtc: null,
		declinedAtUtc: null,
		voidedAtUtc: null,
		failureCode: 'delivery-failed',
		signers: [{
			leaseAgreementSignerId: 501,
			signerRole: 'PrimaryTenant',
			nameSnapshot: 'Ada Lovelace',
			emailSnapshot: 'ada@example.com',
			signingOrder: 1,
			isRequired: true,
			status: 'Pending',
			deliveryQueuedAtUtc: '2027-01-01T12:00:00Z',
			viewedAtUtc: null,
			consentGivenAtUtc: null,
			signedAtUtc: null,
			declinedAtUtc: null
		}]
	});
	mocks.resend.mockResolvedValue({ signatureRequestId: 9001, signatureSignerId: 501, replayed: false });
}

async function renderDialog(props: Record<string, unknown>) {
	const view = render(LeaseCanonicalDialogsHarness, { props: props as never });
	return view;
}

describe('rendered canonical lease action payloads', () => {
	beforeEach(() => {
		vi.clearAllMocks();
		resetDefaults();
	});

	it('creates an addendum with the selected governing agreement and relationship IDs', async () => {
		const view = await renderDialog({
			mode: 'addendum',
			leaseManagementId,
			propertyId: 7,
			source: sourceAgreement
		});
		await waitFor(() => expect(view.getByTestId('addendum-create-dialog')).toBeTruthy());
		await fireEvent.input(view.getByTestId('addendum-create-number'), { target: { value: 'ADD-101' } });
		const effectiveDate = view.baseElement.querySelectorAll('input.date-picker-input')[0];
		await fireEvent.input(effectiveDate, { target: { value: '01/02/2027' } });
		const templateTrigger = view.getByRole('button', { name: 'Document template' });
		await fireEvent.keyDown(templateTrigger, { key: 'ArrowDown' });
		expect(templateTrigger.getAttribute('aria-expanded')).toBe('true');
		const templateOption = await waitFor(() => view.getByRole('option', { name: 'Lease change template · version 1' }));
		await fireEvent.pointerUp(templateOption, { pointerId: 1, pointerType: 'mouse' });
		await waitFor(() => expect(templateTrigger.textContent).toContain('Lease change template · version 1'));
		await waitFor(() => expect(view.getByDisplayValue('Ada Lovelace')).toBeTruthy());
		await fireEvent.click(view.getByRole('button', { name: 'Create draft' }));
		await waitFor(() => expect(mocks.addendumCreate).toHaveBeenCalledOnce());
		expect(mocks.templates).toHaveBeenCalledWith(expect.objectContaining({ propertyId: 7, kind: 'Lease', status: 'Active' }));
		expect(mocks.addendumCreate).toHaveBeenCalledWith(
			leaseManagementId,
			expect.objectContaining({
				baseAgreementId: governingAgreementId,
				addendumNumber: 'ADD-101',
				effectiveFromOn: '2027-01-02',
				documentTemplateId: 301
			}),
			expect.any(String)
		);
	});

	it.each([
		['Correction', { correctionReason: 'Fix the amount', termStartOn: '2027-01-01', termEndOn: '2027-12-31', governingFromOn: '2027-02-01' }],
		['Restatement', { termStartOn: '2027-01-01', termEndOn: '2027-12-31', governingFromOn: '2027-02-01' }],
		['Renewal', { termStartOn: '2028-01-01', termEndOn: '2028-12-31', governingFromOn: '2028-01-01' }],
		['MonthToMonth', { termStartOn: '2028-01-01', termEndOn: null, governingFromOn: '2028-01-01' }]
	] as const)('creates the %s successor from the selected source agreement', async (changeType, expected) => {
		const view = await renderDialog({ mode: 'successor', leaseManagementId, source: sourceAgreement, changeType, businessDate: '2027-02-01' });
		await waitFor(() => expect(view.getByTestId('agreement-successor-dialog')).toBeTruthy());
		if (changeType === 'Correction') {
			await fireEvent.input(view.getByTestId('agreement-successor-correction-reason'), { target: { value: expected.correctionReason } });
		}
		if (changeType === 'Renewal' || changeType === 'MonthToMonth') {
			await fireEvent.input(view.getByTestId('agreement-successor-term-start'), { target: { value: '01/01/2028' } });
			if (changeType === 'Renewal') {
				await fireEvent.input(view.getByTestId('agreement-successor-term-end'), { target: { value: '12/31/2028' } });
			}
			await fireEvent.input(view.getByTestId('agreement-successor-governing-from'), { target: { value: '01/01/2028' } });
		}
		await fireEvent.click(view.getByRole('button', { name: 'Create draft' }));
		await waitFor(() => expect(mocks.successorCreate).toHaveBeenCalledOnce());
		expect(mocks.successorCreate).toHaveBeenCalledWith(
			leaseManagementId,
			governingAgreementId,
			expect.objectContaining({ changeType, ...expected }),
			expect.any(String)
		);
	});

	it('prepares and sends the exact returned draft revision with separate operation keys', async () => {
		const randomUUID = vi.spyOn(globalThis.crypto, 'randomUUID')
			.mockReturnValueOnce('prepare-operation-key')
			.mockReturnValueOnce('issue-operation-key');
		const view = await renderDialog({
			mode: 'draft',
			leaseManagementId,
			leaseAgreementId: draftDetail.leaseAgreementId,
			source: sourceAgreement
		});
		await waitFor(() => expect(view.getByTestId('agreement-draft-dialog')).toBeTruthy());
		await waitFor(() => expect(view.getByRole('button', { name: 'Prepare and send' })).toBeTruthy());
		await fireEvent.click(view.getByRole('button', { name: 'Prepare and send' }));
		await fireEvent.click(within(view.getByTestId('agreement-issue-confirmation')).getByRole('button', { name: 'Prepare and send' }));
		await waitFor(() => expect(mocks.issue).toHaveBeenCalledOnce());
		expect(mocks.prepare).toHaveBeenCalledWith(leaseManagementId, draftDetail.leaseAgreementId, draftDetail.draftRevision, 'prepare-operation-key');
		expect(mocks.issue).toHaveBeenCalledWith(
			leaseManagementId,
			draftDetail.leaseAgreementId,
			{
				pendingUploadId: 'upload-7',
				draftRevision: 7,
				documentSourceVersionId: governingAgreementId,
				issuanceFingerprint: 'fingerprint-7',
				storageKey: 'storage/7',
				fileName: 'lease-102.pdf',
				fileSize: 1234,
				contentSha256: 'hash-7',
				subject: 'Lease LEASE-102'
			},
			'issue-operation-key'
		);
		randomUUID.mockRestore();
	});

	it('resends the selected signer invitation with the exact relationship, agreement, signer, and operation key', async () => {
		const randomUUID = vi.spyOn(globalThis.crypto, 'randomUUID').mockReturnValue('resend-operation-key');
		const confirm = vi.spyOn(globalThis, 'confirm').mockReturnValue(true);
		const view = await renderDialog({ mode: 'signature', leaseManagementId, leaseAgreementId: governingAgreementId, agreementNumber: 'LEASE-101' });
		await waitFor(() => expect(view.getByTestId('agreement-signature-progress')).toBeTruthy());
		await waitFor(() => expect(view.getByRole('button', { name: 'Resend invitation' })).toBeTruthy());
		await fireEvent.click(view.getByRole('button', { name: 'Resend invitation' }));
		await waitFor(() => expect(mocks.resend).toHaveBeenCalledOnce());
		expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Ada Lovelace'));
		expect(mocks.resend).toHaveBeenCalledWith(leaseManagementId, governingAgreementId, 501, 'resend-operation-key');
		randomUUID.mockRestore();
		confirm.mockRestore();
	});
});
