/**
 * Unauthenticated client for the public, no-login rental application flow.
 *
 * These endpoints live under `/api/v1/public/applications/*` and must be called
 * WITHOUT the app's auth cookie or bearer token — they are reached by anonymous
 * prospective tenants via a shareable link. We use a plain `fetch` to the
 * same-origin `/api/v1` proxy path (forwarded to the API by the Vite dev proxy /
 * reverse proxy) and deliberately do NOT attach the runes auth store token.
 *
 * Note: we omit `credentials` (default `same-origin`) but never read or rely on
 * the auth cookie; the API authenticates these routes anonymously.
 */

import { API_BASE_URL } from '$lib/config';

const PUBLIC_FETCH_TIMEOUT_MS = 30_000;

/** Thrown for any non-2xx public response; `status` lets callers special-case 404. */
export class PublicApiError extends Error {
	constructor(
		public status: number,
		message: string
	) {
		super(message);
		this.name = 'PublicApiError';
	}
}

export interface PublicApplicationUnit {
	id: number;
	unitNumber: string;
}

export interface PublicApplicationProperty {
	id: number;
	name: string;
	addressLine1: string | null;
	city: string | null;
	state: string | null;
	units: PublicApplicationUnit[];
}

/** Allowed custom-question input kinds (matches the API contract). */
export type CustomFieldType = 'text' | 'number' | 'yesno' | 'select';

/** A landlord-authored custom question on the application form. */
export interface CustomFieldConfig {
	id: string;
	label: string;
	type: CustomFieldType;
	required: boolean;
	options: string[];
}

/** Normalized application-form configuration the public form renders from. Always complete. */
export interface ApplicationFormConfig {
	incomeSources: { enabled: boolean };
	pets: { enabled: boolean; askDeposit: boolean };
	customFields: CustomFieldConfig[];
	defaults: {
		propertyId: number | null;
		unitId: number | null;
		desiredMoveInDate: string | null;
	};
	/** Default keys ("propertyId" | "unitId" | "desiredMoveInDate") the applicant may NOT change. */
	locked: string[];
}

export interface PublicApplicationContext {
	managementCompanyName: string;
	properties: PublicApplicationProperty[];
	/** Tells the form which fields to render. Defaults (income on, pets off) when the landlord set nothing. */
	formConfig: ApplicationFormConfig;
}

/** A single scanned field with its extraction confidence (0–1). */
export interface ScannedField {
	value: string;
	confidence: number;
}

export interface ScanIdResult {
	fields: {
		firstName?: ScannedField;
		lastName?: ScannedField;
		dateOfBirth?: ScannedField;
		currentAddress?: ScannedField;
		employer?: ScannedField;
		monthlyIncome?: ScannedField;
	};
	extracted: boolean;
}

export interface SubmitApplicationBody {
	propertyId?: number | null;
	unitId?: number | null;
	firstName: string;
	lastName: string;
	email: string;
	phone: string;
	dateOfBirth?: string | null;
	currentAddressLine1?: string | null;
	currentAddressLine2?: string | null;
	currentCity?: string | null;
	currentState?: string | null;
	currentPostalCode?: string | null;
	employer?: string | null;
	monthlyIncome?: number | null;
	desiredMoveInDate?: string | null;
	notes?: string | null;
	// Server binds this to a `string?` (jsonb stored as text). Send the serialized JSON
	// string, NOT a raw object — an object fails System.Text.Json string binding with a 400.
	idExtractedFields?: string | null;
	// Configurable-form payloads, each a serialized JSON string (same string-binding rule as
	// idExtractedFields). incomeSourcesJson = JSON array of { employer, monthlyIncome };
	// petsJson = JSON object { hasPets, pets[] }; customFieldAnswersJson = JSON object keyed by field id.
	incomeSourcesJson?: string | null;
	petsJson?: string | null;
	customFieldAnswersJson?: string | null;
	consentGiven: boolean;
}

export interface SubmitApplicationResult {
	applicationId: number;
	status: string;
	message: string;
}

async function publicFetch(
	endpoint: string,
	init: RequestInit,
	timeoutMs = PUBLIC_FETCH_TIMEOUT_MS
): Promise<Response> {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), timeoutMs);
	try {
		// No Authorization header, no credentials include — anonymous by design.
		return await fetch(`${API_BASE_URL}${endpoint}`, { ...init, signal: controller.signal });
	} catch (error) {
		if (error instanceof DOMException && error.name === 'AbortError') {
			throw new PublicApiError(408, 'The request took too long. Please try again.');
		}
		throw error;
	} finally {
		clearTimeout(timeout);
	}
}

async function readError(response: Response): Promise<string> {
	try {
		const body = await response.json();
		if (body && typeof body === 'object') {
			const b = body as Record<string, unknown>;
			if (typeof b.error === 'string') return b.error;
			if (b.error && typeof b.error === 'object' && typeof (b.error as Record<string, unknown>).message === 'string') {
				return (b.error as Record<string, string>).message;
			}
			if (typeof b.message === 'string') return b.message;
			if (typeof b.title === 'string') return b.title;
		}
	} catch {
		// non-JSON body
	}
	if (response.status === 404) return 'This application link is invalid or has expired.';
	return `Request failed (${response.status}).`;
}

/** GET the public context (company name + properties/units) for an apply token. */
export async function getApplicationContext(token: string): Promise<PublicApplicationContext> {
	const response = await publicFetch(`/public/applications/${encodeURIComponent(token)}`, {
		method: 'GET',
		headers: { Accept: 'application/json' },
	});
	if (!response.ok) {
		throw new PublicApiError(response.status, await readError(response));
	}
	return response.json() as Promise<PublicApplicationContext>;
}

/** POST a single ID / pay-stub image for field extraction. */
export async function scanApplicationId(token: string, file: File): Promise<ScanIdResult> {
	const formData = new FormData();
	formData.append('file', file);
	const response = await publicFetch(
		`/public/applications/${encodeURIComponent(token)}/scan-id`,
		{ method: 'POST', body: formData }
	);
	if (!response.ok) {
		throw new PublicApiError(response.status, await readError(response));
	}
	return response.json() as Promise<ScanIdResult>;
}

/** POST the completed application. Returns the created application id + status. */
export async function submitApplication(
	token: string,
	body: SubmitApplicationBody
): Promise<SubmitApplicationResult> {
	const response = await publicFetch(`/public/applications/${encodeURIComponent(token)}`, {
		method: 'POST',
		headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
		body: JSON.stringify(body),
	});
	if (!response.ok) {
		throw new PublicApiError(response.status, await readError(response));
	}
	return response.json() as Promise<SubmitApplicationResult>;
}
