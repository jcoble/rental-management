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
}

export function buildListQuery(
	params: ListParams = {},
	extra: Record<string, string | number | null | undefined> = {}
): string {
	const query = new URLSearchParams();
	if (params.skip != null && params.skip > 0) query.set('skip', String(params.skip));
	if (params.take != null) query.set('take', String(params.take));
	if (params.search != null && params.search.trim().length > 0) query.set('search', params.search.trim());
	if (params.sort != null && params.sort.length > 0) query.set('sort', params.sort);
	for (const [key, value] of Object.entries(extra)) {
		if (value != null && String(value).length > 0) query.set(key, String(value));
	}
	const qs = query.toString();
	return qs ? `?${qs}` : '';
}
