/**
 * resolveStateCode — commit a typed-but-uncommitted StateSelect entry.
 *
 * The combobox keeps the live search text in `inputValue` and only sets a value
 * when the user explicitly picks an item (click / Enter). If the user types a
 * prefix like "Oh" for Ohio and then presses Tab (or blurs / clicks outside),
 * the dropdown closes WITHOUT a selection — and the wrapper would otherwise reset
 * the input to the selected label, which is empty, silently blanking the field.
 *
 * This helper resolves the typed text into a 2-letter code so the close handler
 * can commit it. Resolution order (case-insensitive, whitespace-trimmed):
 *   1. Empty text                -> keep `currentValue` (see below)
 *   2. Exact label  ("Ohio (OH)")-> that item
 *   3. Exact 2-letter code ("OH")-> that item
 *   4. Exact full name ("Ohio")  -> that item
 *   5. Unique prefix of name/label/code ("Oh", "New J") -> that item
 *
 * If nothing resolves (gibberish) or a prefix is ambiguous (e.g. "New" matches
 * four states), we return `currentValue` unchanged: an already-set value is
 * NEVER blanked because the user typed something unmatchable and tabbed away.
 *
 * Empty text is treated the same way (keep `currentValue`): the combobox clears
 * the search box on open, so just focusing a populated field and tabbing through
 * must not wipe the existing selection. The field has no inline clear affordance,
 * so an accidental blank is the only thing an empty box could mean here.
 */
export type ComboboxItem = { value: string; label: string };
export type CommittedStateInput = { value: string; label: string };

export function resolveStateCode({
	typed,
	items,
	currentValue
}: {
	typed: string;
	items: readonly ComboboxItem[];
	currentValue: string;
}): string {
	const text = typed.trim();

	// Empty box -> keep whatever was already selected. Never blank a set value just
	// because the search box happens to be empty (it is cleared on open).
	if (text === '') return currentValue;

	const lower = text.toLowerCase();

	// The full state name is the label with its trailing " (XX)" code stripped.
	const nameOf = (item: ComboboxItem) =>
		item.label.replace(/\s*\([A-Za-z]{2}\)\s*$/, '').trim();

	// 1. Exact label match (e.g. "Ohio (OH)").
	const byLabel = items.find((i) => i.label.toLowerCase() === lower);
	if (byLabel) return byLabel.value.toUpperCase();

	// 2. Exact 2-letter code match (e.g. "OH").
	const byCode = items.find((i) => i.value.toLowerCase() === lower);
	if (byCode) return byCode.value.toUpperCase();

	// 3. Exact full-name match (e.g. "Ohio" / "New York").
	const byName = items.find((i) => nameOf(i).toLowerCase() === lower);
	if (byName) return byName.value.toUpperCase();

	// 4. Unique prefix match against name, label, or code.
	const prefixMatches = items.filter((i) => {
		const name = nameOf(i).toLowerCase();
		const label = i.label.toLowerCase();
		const code = i.value.toLowerCase();
		return name.startsWith(lower) || label.startsWith(lower) || code.startsWith(lower);
	});
	if (prefixMatches.length === 1) return prefixMatches[0].value.toUpperCase();

	// No confident match: keep the prior selection (never blank a set value).
	return currentValue;
}

export function commitStateInput({
	typed,
	items,
	currentValue
}: {
	typed: string;
	items: readonly ComboboxItem[];
	currentValue: string;
}): CommittedStateInput {
	const value = resolveStateCode({ typed, items, currentValue });
	return {
		value,
		label: items.find((i) => i.value.toUpperCase() === value.toUpperCase())?.label ?? ''
	};
}
