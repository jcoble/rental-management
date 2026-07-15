import type {
	LeaseAgreementTermType,
	PrepareMoveInRequest
} from '$lib/api/endpoints/lease-managements';

export interface PrepareMoveInForm {
	applicationId: string;
	unitId: string;
	tenantId: string;
	plannedPossessionOn: string;
	partyEffectiveFrom: string;
	documentTemplateId: string;
	termType: LeaseAgreementTermType;
	termStartOn: string;
	termEndOn: string;
	baseRentAmount: string;
	rentDueDay: string;
	securityDepositObligation: string;
	lateFeeAmount: string;
	gracePeriodDays: string;
	createSecurityDepositAccount: boolean;
	openingBalanceAmount: string;
	openingBalanceEffectiveOn: string;
	openingBalanceNote: string;
}

export type PrepareMoveInFormErrors = Partial<Record<keyof PrepareMoveInForm, string>>;

export function createPrepareMoveInForm(
	prefill: {
		applicationId?: string;
		unitId?: string;
		tenantId?: string;
	} = {}
): PrepareMoveInForm {
	return {
		applicationId: prefill.applicationId ?? '',
		unitId: prefill.unitId ?? '',
		tenantId: prefill.tenantId ?? '',
		plannedPossessionOn: '',
		partyEffectiveFrom: '',
		documentTemplateId: '',
		termType: 'FixedTerm',
		termStartOn: '',
		termEndOn: '',
		baseRentAmount: '',
		rentDueDay: '',
		securityDepositObligation: '',
		lateFeeAmount: '',
		gracePeriodDays: '',
		createSecurityDepositAccount: false,
		openingBalanceAmount: '',
		openingBalanceEffectiveOn: '',
		openingBalanceNote: ''
	};
}

function positiveInteger(value: string): number | null {
	if (!/^\d+$/.test(value.trim())) return null;
	const parsed = Number(value);
	return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
}

function integerInRange(value: string, min: number, max: number): number | null {
	const parsed = positiveInteger(value);
	return parsed != null && parsed >= min && parsed <= max ? parsed : null;
}

function nonNegativeMoney(value: string): number | null {
	if (value.trim() === '') return null;
	const parsed = Number(value);
	return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

export function buildPrepareMoveInRequest(form: PrepareMoveInForm): {
	request: PrepareMoveInRequest | null;
	errors: PrepareMoveInFormErrors;
} {
	const errors: PrepareMoveInFormErrors = {};
	const applicationId = positiveInteger(form.applicationId);
	const unitId = positiveInteger(form.unitId);
	const tenantId = positiveInteger(form.tenantId);
	const documentTemplateId = positiveInteger(form.documentTemplateId);
	const rentDueDay = integerInRange(form.rentDueDay, 1, 31);
	const gracePeriodDays =
		form.gracePeriodDays.trim() === '0' ? 0 : integerInRange(form.gracePeriodDays, 1, 31);
	const baseRentAmount = nonNegativeMoney(form.baseRentAmount);
	const securityDepositObligation = nonNegativeMoney(form.securityDepositObligation);
	const lateFeeAmount = nonNegativeMoney(form.lateFeeAmount);

	if (!applicationId) errors.applicationId = 'Choose an approved application.';
	if (!unitId) errors.unitId = 'Choose the exact unit for this move-in.';
	if (!tenantId) errors.tenantId = 'The approved tenant could not be resolved.';
	if (!form.partyEffectiveFrom)
		errors.partyEffectiveFrom = 'Choose when this household relationship begins.';
	if (!form.termStartOn) errors.termStartOn = 'Choose the agreement start date.';
	if (form.termType === 'FixedTerm' && !form.termEndOn) {
		errors.termEndOn = 'Choose the fixed-term end date.';
	} else if (form.termType === 'FixedTerm' && form.termEndOn < form.termStartOn) {
		errors.termEndOn = 'The end date cannot be before the start date.';
	}
	if (baseRentAmount == null) errors.baseRentAmount = 'Enter rent as zero or a positive amount.';
	if (rentDueDay == null) errors.rentDueDay = 'Enter a due day from 1 through 31.';
	if (securityDepositObligation == null) {
		errors.securityDepositObligation = 'Enter the deposit obligation, including zero.';
	}
	if (lateFeeAmount == null) errors.lateFeeAmount = 'Enter the late fee, including zero.';
	if (gracePeriodDays == null) errors.gracePeriodDays = 'Enter grace days from 0 through 31.';
	const openingBalanceAmount =
		form.openingBalanceAmount.trim() === '' ? null : Number(form.openingBalanceAmount);
	if (
		openingBalanceAmount != null &&
		(!Number.isFinite(openingBalanceAmount) || openingBalanceAmount === 0)
	) {
		errors.openingBalanceAmount = 'Opening balance must be a non-zero amount.';
	}
	if ((openingBalanceAmount != null) !== Boolean(form.openingBalanceEffectiveOn)) {
		errors.openingBalanceEffectiveOn =
			'Supply the opening balance amount and effective date together.';
	}
	if (form.openingBalanceNote.trim() && openingBalanceAmount == null) {
		errors.openingBalanceNote = 'Enter an opening balance before adding its note.';
	}
	if (form.openingBalanceNote.trim().length > 500) {
		errors.openingBalanceNote = 'Opening balance note cannot exceed 500 characters.';
	}

	if (
		Object.keys(errors).length > 0 ||
		!applicationId ||
		!unitId ||
		!tenantId ||
		rentDueDay == null ||
		gracePeriodDays == null ||
		baseRentAmount == null ||
		securityDepositObligation == null ||
		lateFeeAmount == null
	) {
		return { request: null, errors };
	}

	return {
		request: {
			applicationId,
			unitId,
			plannedPossessionAtUtc: form.plannedPossessionOn
				? `${form.plannedPossessionOn}T00:00:00.000Z`
				: null,
			partyEffectiveFrom: form.partyEffectiveFrom,
			parties: [
				{
					tenantId,
					role: 'PrimaryTenant',
					guarantorLegalNoticeEligible: false,
					changeReason: 'Approved application move-in',
					isAgreementSigner: true,
					signingOrder: 1,
					isRequiredSigner: true
				}
			],
			documentTemplateId,
			termType: form.termType,
			termStartOn: form.termStartOn,
			termEndOn: form.termType === 'FixedTerm' ? form.termEndOn : null,
			baseRentAmount,
			rentDueDay,
			securityDepositObligation,
			lateFeeAmount,
			gracePeriodDays,
			termsSchemaVersion: 1,
			termsPayload: {},
			createSecurityDepositAccount: form.createSecurityDepositAccount,
			openingBalanceAmount,
			openingBalanceEffectiveOn:
				openingBalanceAmount == null ? null : form.openingBalanceEffectiveOn,
			openingBalanceNote: form.openingBalanceNote.trim() || null
		},
		errors: {}
	};
}
