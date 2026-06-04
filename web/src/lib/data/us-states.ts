/**
 * US states + DC reference data.
 *
 * Each entry is `{ code, name }` where `code` is the 2-letter USPS abbreviation
 * (uppercase) and `name` is the full display name. The list is the source of
 * truth for the shared `StateSelect.svelte` picker.
 *
 * `US_STATES` = 50 states + DC (the common case). `US_TERRITORIES` are exported
 * separately so callers can opt in (e.g. `[...US_STATES, ...US_TERRITORIES]`)
 * without polluting the default list.
 */

export type UsState = { code: string; name: string };

/** 50 states + District of Columbia, ordered alphabetically by name. */
export const US_STATES: readonly UsState[] = [
	{ code: 'AL', name: 'Alabama' },
	{ code: 'AK', name: 'Alaska' },
	{ code: 'AZ', name: 'Arizona' },
	{ code: 'AR', name: 'Arkansas' },
	{ code: 'CA', name: 'California' },
	{ code: 'CO', name: 'Colorado' },
	{ code: 'CT', name: 'Connecticut' },
	{ code: 'DE', name: 'Delaware' },
	{ code: 'DC', name: 'District of Columbia' },
	{ code: 'FL', name: 'Florida' },
	{ code: 'GA', name: 'Georgia' },
	{ code: 'HI', name: 'Hawaii' },
	{ code: 'ID', name: 'Idaho' },
	{ code: 'IL', name: 'Illinois' },
	{ code: 'IN', name: 'Indiana' },
	{ code: 'IA', name: 'Iowa' },
	{ code: 'KS', name: 'Kansas' },
	{ code: 'KY', name: 'Kentucky' },
	{ code: 'LA', name: 'Louisiana' },
	{ code: 'ME', name: 'Maine' },
	{ code: 'MD', name: 'Maryland' },
	{ code: 'MA', name: 'Massachusetts' },
	{ code: 'MI', name: 'Michigan' },
	{ code: 'MN', name: 'Minnesota' },
	{ code: 'MS', name: 'Mississippi' },
	{ code: 'MO', name: 'Missouri' },
	{ code: 'MT', name: 'Montana' },
	{ code: 'NE', name: 'Nebraska' },
	{ code: 'NV', name: 'Nevada' },
	{ code: 'NH', name: 'New Hampshire' },
	{ code: 'NJ', name: 'New Jersey' },
	{ code: 'NM', name: 'New Mexico' },
	{ code: 'NY', name: 'New York' },
	{ code: 'NC', name: 'North Carolina' },
	{ code: 'ND', name: 'North Dakota' },
	{ code: 'OH', name: 'Ohio' },
	{ code: 'OK', name: 'Oklahoma' },
	{ code: 'OR', name: 'Oregon' },
	{ code: 'PA', name: 'Pennsylvania' },
	{ code: 'RI', name: 'Rhode Island' },
	{ code: 'SC', name: 'South Carolina' },
	{ code: 'SD', name: 'South Dakota' },
	{ code: 'TN', name: 'Tennessee' },
	{ code: 'TX', name: 'Texas' },
	{ code: 'UT', name: 'Utah' },
	{ code: 'VT', name: 'Vermont' },
	{ code: 'VA', name: 'Virginia' },
	{ code: 'WA', name: 'Washington' },
	{ code: 'WV', name: 'West Virginia' },
	{ code: 'WI', name: 'Wisconsin' },
	{ code: 'WY', name: 'Wyoming' }
];

/** US territories + freely-associated areas. Opt-in; not in `US_STATES`. */
export const US_TERRITORIES: readonly UsState[] = [
	{ code: 'AS', name: 'American Samoa' },
	{ code: 'GU', name: 'Guam' },
	{ code: 'MP', name: 'Northern Mariana Islands' },
	{ code: 'PR', name: 'Puerto Rico' },
	{ code: 'VI', name: 'U.S. Virgin Islands' }
];

/** Look up a full state/territory name from its 2-letter code (case-insensitive). */
export function stateName(code: string | null | undefined): string {
	if (!code) return '';
	const upper = code.toUpperCase();
	return (
		US_STATES.find((s) => s.code === upper)?.name ??
		US_TERRITORIES.find((s) => s.code === upper)?.name ??
		''
	);
}
