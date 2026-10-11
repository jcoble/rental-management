/**
 * Shared list/pagination query parameters accepted by the API's `ListQuery`
 * binder (`?skip&take&search&sort`). {@link buildListQuery} serializes these
 * (plus any extra entity-specific filters) into a `URLSearchParams`, omitting
 * empty values so the backend applies its defaults.
 */

export interface ListParams {
	/** Number of records to skip (offset). */
	skip?: number;
	/** Page size; the API clamps to (0, 200]. */
	take?: number;
	/** Free-text search applied to entity-specific fields. */
	search?: string;
	/** Sort field; prefix with '-' for descending (e.g. `-createdAt`). */
	sort?: string;
	/** Grid date-range start (ISO `yyyy-MM-dd`). Applied to the entity's designated date column, DB-side. */
	from?: string;
	/** Grid date-range end DAY (ISO `yyyy-MM-dd`), inclusive (the API treats it as `< to + 1 day`). */
	to?: string;
}

export function buildListQuery(
	params: ListParams = {},
	extra: Record<string, string | number | readonly (string | number)[] | null | undefined> = {}
): string {
	const query = new URLSearchParams();
	if (params.skip != null && params.skip > 0) query.set('skip', String(params.skip));
	if (params.take != null) query.set('take', String(params.take));
	if (params.search != null && params.search.trim().length > 0) query.set('search', params.search.trim());
	if (params.sort != null && params.sort.length > 0) query.set('sort', params.sort);
	if (params.from != null && params.from.length > 0) query.set('from', params.from);
	if (params.to != null && params.to.length > 0) query.set('to', params.to);
	for (const [key, value] of Object.entries(extra)) {
		if (Array.isArray(value)) {
			for (const item of value) {
				if (String(item).length > 0) query.append(key, String(item));
			}
			continue;
		}
		if (value != null && String(value).length > 0) query.set(key, String(value));
	}
	const qs = query.toString();
	return qs ? `?${qs}` : '';
}
