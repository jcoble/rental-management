const DATA_ENTRY_CONTROL_SELECTOR = [
	'form',
	'input:not([type="hidden"]):not([disabled]):not([readonly])',
	'textarea:not([disabled]):not([readonly])',
	'select:not([disabled]):not([readonly])',
	'[contenteditable="true"]',
	'[role="textbox"]',
	'[role="combobox"]',
	'[role="checkbox"]',
	'[role="radio"]',
	'[role="switch"]',
	'[role="spinbutton"]',
	'button[data-state]:not([data-dialog-close]):not([data-slot="dialog-close"]):not([data-drawer-close]):not([data-slot="drawer-close"])',
	'button[aria-pressed]',
	'button[aria-checked]',
	'button[aria-expanded]',
	'button[aria-haspopup]'
].join(',');

/**
 * Data-entry dialogs stay open until the user explicitly dismisses them.
 * The check is performed against the rendered content so shared dialogs do
 * not need every call site to repeat outside-click and Escape handlers.
 */
export function containsDataEntryControls(element: Element | null): boolean {
	return Boolean(element?.matches(DATA_ENTRY_CONTROL_SELECTOR) || element?.querySelector(DATA_ENTRY_CONTROL_SELECTOR));
}

export function preventDataEntryDismissal(element: Element | null, event: Event): void {
	if (containsDataEntryControls(element)) {
		event.preventDefault();
	}
}
