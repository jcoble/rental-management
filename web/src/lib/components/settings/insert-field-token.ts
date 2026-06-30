/**
 * Insert a `{{field}}` merge token into `text` at the given caret position and return the new
 * text plus the caret position just after the inserted token. The caret is clamped to the valid
 * range so callers can pass a raw selectionStart without bounds-checking.
 */
export function insertFieldToken(
	text: string,
	caret: number,
	field: string
): { text: string; caret: number } {
	const token = `{{${field}}}`;
	const position = Math.max(0, Math.min(caret, text.length));
	const next = text.slice(0, position) + token + text.slice(position);
	return { text: next, caret: position + token.length };
}
