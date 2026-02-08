export interface Shortcut {
	key: string;
	label: string;
	meta?: boolean;
	shift?: boolean;
}

export const shortcuts: Shortcut[] = [
	{ key: 'n', label: 'New task' },
	{ key: 'f', label: 'Focus search' },
	{ key: '/', label: 'Focus search' },
	{ key: 'j', label: 'Next item' },
	{ key: 'k', label: 'Previous item' },
	{ key: 'h', label: 'Move left' },
	{ key: 'l', label: 'Move right' },
	{ key: 'Enter', label: 'Open selected' },
	{ key: 'Escape', label: 'Close panel' },
	{ key: '?', label: 'Show shortcuts' },
	{ key: 'k', label: 'Command palette', meta: true },
];

export type ShortcutAction =
	| 'new-task'
	| 'focus-search'
	| 'next-item'
	| 'prev-item'
	| 'move-left'
	| 'move-right'
	| 'open-selected'
	| 'close-panel'
	| 'show-shortcuts'
	| 'command-palette';

export function matchShortcut(e: KeyboardEvent): ShortcutAction | null {
	if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return null;

	if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
		e.preventDefault();
		return 'command-palette';
	}

	switch (e.key) {
		case 'n': return 'new-task';
		case 'f':
		case '/': e.preventDefault(); return 'focus-search';
		case 'j': return 'next-item';
		case 'k': return 'prev-item';
		case 'h': return 'move-left';
		case 'l': return 'move-right';
		case 'Enter': return 'open-selected';
		case 'Escape': return 'close-panel';
		case '?': return 'show-shortcuts';
		default: return null;
	}
}
