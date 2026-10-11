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
	/**
	 * CSS min-width e.g. `"8rem"` — applied as inline style on the `<th>/<td>`. Keeps a column
	 * readable when the grid is dense: once the sum of min-widths exceeds the container the desktop
	 * table scrolls horizontally instead of squishing columns.
	 */
	minWidth?: string;
	/**
	 * CSS max-width e.g. `"20rem"` — applied as inline style on the `<th>/<td>`. The cell content
	 * truncates with an ellipsis at this width so a long free-text column can't blow out the table.
	 */
	maxWidth?: string;
	/** Extra class(es) appended to both the `<th>` and `<td>`. */
	class?: string;
	/** Pin a column to the left or right edge while the desktop table scrolls. */
	pinned?: 'left' | 'right';
	/** CSS inset used by a right-pinned column when another pinned column follows it. */
	pinnedOffset?: string;
	/** Role in the mobile card layout. First column defaults to `title`, rest to `meta`. */
	mobileRole?: MobileColumnRole;
	/** Override the label shown in the mobile card fields grid. Defaults to `title`. */
	mobileLabel?: string;
	/**
	 * Marks this as an action column (Mark Paid / Edit / Delete buttons). Action columns are
	 * pinned sticky to the LEFT edge of the desktop table and rendered before the data columns,
	 * so the row's controls stay visible even when the grid overflows horizontally. They never
	 * become the implicit mobile `title` and default to `mobileRole: 'hidden'`.
	 */
	isAction?: boolean;
}
