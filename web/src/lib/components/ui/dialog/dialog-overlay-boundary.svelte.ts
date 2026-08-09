import { getContext, setContext } from 'svelte';

export interface DialogOverlayBoundaryContext {
	content: Element | null;
	footer: HTMLElement | null;
	footerHeight: number;
}

const DIALOG_OVERLAY_BOUNDARY = Symbol('dialog-overlay-boundary');

export function createDialogOverlayBoundary(): DialogOverlayBoundaryContext {
	const boundary = $state<DialogOverlayBoundaryContext>({
		content: null,
		footer: null,
		footerHeight: 0,
	});
	setContext(DIALOG_OVERLAY_BOUNDARY, boundary);
	return boundary;
}

export function getDialogOverlayBoundary(): DialogOverlayBoundaryContext | null {
	return getContext<DialogOverlayBoundaryContext | null>(DIALOG_OVERLAY_BOUNDARY) ?? null;
}
