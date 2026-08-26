import type {
	LeaseAgreementTermType,
	PrepareMoveInRequest
} from '$lib/api/endpoints/lease-managements';
import { formatStatusLabel } from '../utils/status-labels.ts';

export interface PrepareMoveInUnitChoice {
	id: number;
	propertyName: string;
	unitNumber: string;
	status?: string | null;
}

export interface PrepareMoveInUnitOption {
	value: string;
	label: string;
	disabled: boolean;
}

/**
 * Choices for the "which unit" picker. A unit that already has a move-in prepared cannot take a
 * second one, so it is still listed - with a reason - but cannot be picked.
 */
export function buildPrepareMoveInUnitOptions(
	units: PrepareMoveInUnitChoice[]
): PrepareMoveInUnitOption[] {
	return units.map((unit) => {
		const alreadyPrepared = unit.status === 'Reserved';
		const state = alreadyPrepared ? 'Move-in already prepared' : formatStatusLabel(unit.status);
		return {
			value: String(unit.id),
			label: `${unit.propertyName} · Unit ${unit.unitNumber} · ${state}`,
			disabled: alreadyPrepared
		};
	});
}

export interface PrepareMoveInForm {
	applicationId: string;
	unitId: string;
	tenantId: string;
	createNewTenant: boolean;
	newTenantFirstName: string;
	newTenantLastName: string;
	newTenantEmail: string;
	newTenantPhone: string;
	newTenantEmergencyContact: string;
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
	rentTrackingStartMode: 'BackfillFromLeaseStart' | 'ForwardOnly' | 'CustomCutoffDate';
	rentTrackingStartOn: string;
	createSecurityDepositAccount: boolean;
	openingBalanceAmount: string;
	openingBalanceEffectiveOn: string;
	openingBalanceNote: string;
}

export type PrepareMoveInFormErrors = Partial<Record<keyof PrepareMoveInForm, string>>;

export interface BuildPrepareMoveInRequestOptions {
	requireApplication?: boolean;
}

/** Today in the browser's own calendar day, as the yyyy-MM-dd the date inputs use. */
function todayIsoDate(): string {
	const now = new Date();
	const month = String(now.getMonth() + 1).padStart(2, '0');
	const day = String(now.getDate()).padStart(2, '0');
	return `${now.getFullYear()}-${month}-${day}`;
}

export function createPrepareMoveInForm(
	prefill: {
		applicationId?: string;
		unitId?: string;
		tenantId?: string;
	} = {}
): PrepareMoveInForm {
	const today = todayIsoDate();
	return {
		applicationId: prefill.applicationId ?? '',
		unitId: prefill.unitId ?? '',
		tenantId: prefill.tenantId ?? '',
		createNewTenant: false,
		newTenantFirstName: '',
		newTenantLastName: '',
		newTenantEmail: '',
		newTenantPhone: '',
		newTenantEmergencyContact: '',
		plannedPossessionOn: '',
		partyEffectiveFrom: today,
		documentTemplateId: '',
		termType: 'FixedTerm',
		termStartOn: today,
		termEndOn: '',
		baseRentAmount: '',
		rentDueDay: '',
		securityDepositObligation: '',
		lateFeeAmount: '',
		gracePeriodDays: '',
		rentTrackingStartMode: 'ForwardOnly',
		rentTrackingStartOn: '',
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

export function buildPrepareMoveInRequest(
	form: PrepareMoveInForm,
	options: BuildPrepareMoveInRequestOptions = {}
): {
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

	if (!unitId) errors.unitId = 'Choose the exact unit for this move-in.';
	if (options.requireApplication && !applicationId) {
		errors.applicationId = 'Choose an approved application.';
	}
	if (!applicationId && !form.createNewTenant && !tenantId) {
		errors.tenantId = 'Choose an existing tenant or create a new one.';
	}
	if (applicationId && !tenantId) errors.tenantId = 'The approved tenant could not be resolved.';
	if (form.createNewTenant) {
		if (!form.newTenantFirstName.trim()) errors.newTenantFirstName = 'Enter the tenant first name.';
		if (!form.newTenantLastName.trim()) errors.newTenantLastName = 'Enter the tenant last name.';
		if (!form.newTenantEmail.trim()) errors.newTenantEmail = 'Enter the tenant email for signing.';
	}
	if (!form.partyEffectiveFrom)
		errors.partyEffectiveFrom = 'Choose the move-in date.';
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
	if (form.rentTrackingStartMode === 'CustomCutoffDate' && !form.rentTrackingStartOn) {
		errors.rentTrackingStartOn = 'Choose the custom rent tracking start date.';
	}
	if (
		form.rentTrackingStartMode === 'CustomCutoffDate' &&
		form.rentTrackingStartOn &&
		form.termStartOn &&
		form.rentTrackingStartOn < form.termStartOn
	) {
		errors.rentTrackingStartOn = 'Rent tracking cannot start before the agreement.';
	}
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
		(options.requireApplication && !applicationId) ||
		!unitId ||
		(!form.createNewTenant && !tenantId) ||
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
					tenantId: form.createNewTenant ? null : tenantId,
					newTenant: form.createNewTenant
						? {
								firstName: form.newTenantFirstName.trim(),
								lastName: form.newTenantLastName.trim(),
								email: form.newTenantEmail.trim() || null,
								phone: form.newTenantPhone.trim() || null,
								emergencyContact: form.newTenantEmergencyContact.trim() || null
							}
						: null,
					role: 'PrimaryTenant',
					guarantorLegalNoticeEligible: false,
					changeReason: applicationId ? 'Approved application move-in' : 'Manual lease creation',
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
			rentTrackingStartMode: form.rentTrackingStartMode,
			rentTrackingStartOn:
				form.rentTrackingStartMode === 'CustomCutoffDate' ? form.rentTrackingStartOn : null,
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
