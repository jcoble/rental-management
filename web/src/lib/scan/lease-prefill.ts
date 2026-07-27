import type { ScanFieldDto } from '$lib/api/scan';

/**
 * Flat, typed view of a lease draft's extracted fields, used to PRE-FILL the four guided
 * create-step forms (Property → Unit → Tenant → Lease). Field names mirror LeaseExtractionSchema.cs.
 * `confidence` per field drives the subtle "from your lease" badge (AC-3).
 */
export interface LeasePrefill {
	// Property
	propertyId: string;          // extracted in-portfolio id, "" if none
	propertyName: string;
	propertyAddress: string;     // street line only
	propertyCity: string;
	propertyState: string;       // 2-letter
	propertyPostalCode: string;
	// Unit
	unitId: string;
	unitNumber: string;
	unitBedrooms: string;
	unitBathrooms: string;
	unitSquareFeet: string;
	// Tenant (free text — no tenant_id in schema)
	tenantName: string;
	tenantEmail: string;
	tenantPhone: string;
	tenantEmergencyContact: string;
	// Lease terms
	leaseNumber: string;
	startDate: string;           // YYYY-MM-DD
	endDate: string;             // YYYY-MM-DD
	possessionGivenOn: string;   // YYYY-MM-DD
	monthlyRent: string;
	securityDeposit: string;
	lateFee: string;
	rentDueDay: string;
}

/** Field name → confidence (0..1), for the "from your lease" badge. Absent name => not auto-filled. */
export type PrefillConfidence = Record<string, number>;

const FIELD = (fs: Map<string, ScanFieldDto>, name: string) => (fs.get(name)?.value ?? '').trim();

/** Build the typed prefill + a confidence map from a draft's raw extracted fields. */
export function toLeasePrefill(fields: ScanFieldDto[]): { values: LeasePrefill; confidence: PrefillConfidence } {
	const fs = new Map(fields.map((f) => [f.name, f]));
	const confidence: PrefillConfidence = {};
	for (const f of fields) if ((f.value ?? '').trim()) confidence[f.name] = f.confidence;
	const values: LeasePrefill = {
		propertyId: FIELD(fs, 'property_id'),
		propertyName: FIELD(fs, 'property_name'),
		propertyAddress: FIELD(fs, 'property_address'),
		propertyCity: FIELD(fs, 'property_city'),
		propertyState: FIELD(fs, 'property_state'),
		propertyPostalCode: FIELD(fs, 'property_postal_code'),
		unitId: FIELD(fs, 'unit_id'),
		unitNumber: FIELD(fs, 'unit_number'),
		unitBedrooms: FIELD(fs, 'unit_bedrooms'),
		unitBathrooms: FIELD(fs, 'unit_bathrooms'),
		unitSquareFeet: FIELD(fs, 'unit_square_feet'),
		tenantName: FIELD(fs, 'tenant_name'),
		tenantEmail: FIELD(fs, 'tenant_email'),
		tenantPhone: FIELD(fs, 'tenant_phone'),
		tenantEmergencyContact: FIELD(fs, 'tenant_emergency_contact'),
		leaseNumber: FIELD(fs, 'lease_number'),
		startDate: FIELD(fs, 'start_date'),
		endDate: FIELD(fs, 'end_date'),
		possessionGivenOn:
			FIELD(fs, 'possession_given_at') ||
			FIELD(fs, 'possession_given_at_utc') ||
			FIELD(fs, 'possessionGivenAtUtc'),
		monthlyRent: FIELD(fs, 'monthly_rent'),
		securityDeposit: FIELD(fs, 'security_deposit'),
		lateFee: FIELD(fs, 'late_fee'),
		rentDueDay: FIELD(fs, 'rent_due_day')
	};
	return { values, confidence };
}

/** Map a field name in a step-form to the extraction key that fills it (for the "from your lease" badge). */
export const STEP_FIELD_TO_EXTRACTION: Record<string, string> = {
	// property form field -> extraction name
	name: 'property_name',
	addressLine1: 'property_address',
	city: 'property_city',
	state: 'property_state',
	postalCode: 'property_postal_code',
	// unit
	unitNumber: 'unit_number',
	bedrooms: 'unit_bedrooms',
	bathrooms: 'unit_bathrooms',
	// tenant
	firstName: 'tenant_name',
	lastName: 'tenant_name',
	email: 'tenant_email',
	phone: 'tenant_phone',
	emergencyContact: 'tenant_emergency_contact',
	// lease
	leaseNumber: 'lease_number',
	startDate: 'start_date',
	endDate: 'end_date',
	monthlyRent: 'monthly_rent',
	securityDeposit: 'security_deposit',
	lateFeeAmount: 'late_fee',
	rentDueDay: 'rent_due_day'
};
