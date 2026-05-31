/**
 * DataGrid column definition types.
 */

import type { Snippet } from 'svelte';

export type SortDirection = 'asc' | 'desc' | 'none';

export type MobileColumnRole = 'title' | 'subtitle' | 'badge' | 'metric' | 'meta' | 'hidden';

export interface ColumnDef<T> {
	/** Unique key for the column — also used as a property name on the data object if `accessor` is omitted. */
	key: string;
	/** Column header label. */
	title: string;
	/** Value getter. When omitted the `key` is used as a property name. */
	accessor?: (item: T) => unknown;
	/** Whether the column header is clickable for client-side (or server-side) sort. */
	sortable?: boolean;
	/** Text alignment for both the header and cells. Defaults to `left`; currency/number/date default to `right`. */
	align?: 'left' | 'center' | 'right';
	/**
	 * Built-in format applied when no `cell` snippet is given.
	 * - `currency`  → `Intl.NumberFormat` USD, right-align, font-mono tabular-nums
	 * - `date`      → locale date string, right-align, font-mono tabular-nums
	 * - `datetime`  → locale date+time string, right-align, font-mono tabular-nums
	 * - `number`    → locale number, right-align, font-mono tabular-nums
	 * - `text`      → plain string (default)
	 */
	format?: 'text' | 'date' | 'datetime' | 'currency' | 'number';
	/** Svelte snippet for rich cell rendering. Receives the full row item. */
	cell?: Snippet<[T]>;
	/** CSS width e.g. `"8rem"` — applied as inline style on the `<th>/<td>`. */
	width?: string;
	/** Extra class(es) appended to both the `<th>` and `<td>`. */
	class?: string;
	/** Role in the mobile card layout. First column defaults to `title`, rest to `meta`. */
	mobileRole?: MobileColumnRole;
	/** Override the label shown in the mobile card fields grid. Defaults to `title`. */
	mobileLabel?: string;
}
