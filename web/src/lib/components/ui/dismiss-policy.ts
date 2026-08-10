const DISMISSIBLE_SURFACE_SELECTOR = '[data-dismiss-policy="dismissible"]';

const DATA_ENTRY_CONTROL_SELECTOR = [
	'input:not([type="hidden"]):not([disabled]):not([readonly]):not([data-slot="command-input"]):not([data-command-input])',
	'textarea:not([disabled]):not([readonly])',
	'select:not([disabled]):not([readonly])',
	'[contenteditable="true"]',
	'[role="textbox"]:not([data-slot="command-input"]):not([data-command-input])',
	'[role="checkbox"]',
	'[role="radio"]',
	'[role="switch"]',
	'[role="spinbutton"]',
	'[data-slot="select-trigger"]',
	'[data-slot="combobox-trigger"]',
	'[aria-checked]:not([role="menuitemcheckbox"]):not([role="menuitemradio"])',
	'[aria-pressed]:not([role="tab"])'
].join(',');

/**
 * Data-entry dialogs stay open until the user explicitly dismisses them.
 * The check is performed against the rendered content so shared dialogs do
 * not need every call site to repeat outside-click and Escape handlers.
 *
 * Audited read-only and transient surfaces may opt out at their content root
 * with `data-dismiss-policy="dismissible"`. Descendant opt-outs are also
 * supported so a help/popover surface cannot make its parent data-entry
 * dialog dismissible.
 */
export function containsDataEntryControls(element: Element | null): boolean {
	if (!element || element.matches(DISMISSIBLE_SURFACE_SELECTOR)) return false;

	const controls = [
		...(element.matches(DATA_ENTRY_CONTROL_SELECTOR) ? [element] : []),
		...Array.from(element.querySelectorAll(DATA_ENTRY_CONTROL_SELECTOR))
	];

	return controls.some((control) => !control.closest(DISMISSIBLE_SURFACE_SELECTOR));
}

export function preventDataEntryDismissal(element: Element | null, event: Event): void {
	if (containsDataEntryControls(element)) {
		event.preventDefault();
	}
}
