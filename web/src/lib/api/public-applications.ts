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
import type { DerivedUnitStatus } from '$lib/types';
import { readPublicError } from './public-error.ts';

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
	status: DerivedUnitStatus;
}

export interface PublicApplicationProperty {
	id: number;
	name: string;
	addressLine1: string | null;
	city: string | null;
	state: string | null;
	units: PublicApplicationUnit[];
}

export interface PublicApplicationContext {
	managementCompanyName: string;
	properties: PublicApplicationProperty[];
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

/** GET the public context (company name + properties/units) for an apply token. */
export async function getApplicationContext(token: string): Promise<PublicApplicationContext> {
	const response = await publicFetch(`/public/applications/${encodeURIComponent(token)}`, {
		method: 'GET',
		headers: { Accept: 'application/json' },
	});
	if (!response.ok) {
		throw new PublicApiError(response.status, await readPublicError(response));
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
		throw new PublicApiError(response.status, await readPublicError(response));
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
		throw new PublicApiError(response.status, await readPublicError(response));
	}
	return response.json() as Promise<SubmitApplicationResult>;
}
