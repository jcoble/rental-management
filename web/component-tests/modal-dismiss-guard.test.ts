import { cleanup, fireEvent, render, waitFor } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import ModalDismissGuardHarness from './ModalDismissGuardHarness.svelte';

afterEach(async () => {
	cleanup();
	await new Promise((resolve) => setTimeout(resolve, 40));
});

async function clickBackdrop(selector: string) {
	const backdrop = document.querySelector(selector);
	expect(backdrop).not.toBeNull();
	await new Promise((resolve) => setTimeout(resolve, 30));
	// jsdom's pointer-event fallback drops coordinates, so provide a real outside point.
	const pointerDown = new Event('pointerdown', { bubbles: true, cancelable: true });
	Object.defineProperties(pointerDown, {
		button: { value: 0 },
		clientX: { value: 10 },
		clientY: { value: 10 },
		pointerType: { value: 'mouse' },
	});
	document.body.dispatchEvent(pointerDown);
	await fireEvent.pointerUp(backdrop!);
	await fireEvent.mouseUp(backdrop!);
	await fireEvent.click(backdrop!);
}

describe('shared modal dismissal policy', () => {
	it('keeps a data-entry dialog open with its typed value and allows explicit Cancel', async () => {
		const view = render(ModalDismissGuardHarness);
		await fireEvent.click(view.getByTestId('open-dialog'));
		const input = view.getByTestId('dialog-input') as HTMLInputElement;
		await fireEvent.input(input, { target: { value: 'typed dialog value' } });

		await clickBackdrop('[data-slot="dialog-overlay"]');

		await waitFor(() => expect(view.getByTestId('data-entry-dialog')).toBeTruthy());
		expect(input.value).toBe('typed dialog value');
		await fireEvent.keyDown(document, { key: 'Escape' });
		await waitFor(() => expect(view.getByTestId('data-entry-dialog')).toBeTruthy());
		expect(input.value).toBe('typed dialog value');
		await fireEvent.click(view.getByTestId('dialog-cancel'));
		await waitFor(() => expect(view.queryByTestId('data-entry-dialog')).toBeNull());
	});

	it('keeps a data-entry sheet open with its typed value and allows explicit Cancel', async () => {
		const view = render(ModalDismissGuardHarness);
		await fireEvent.click(view.getByTestId('open-sheet'));
		const input = view.getByTestId('sheet-input') as HTMLInputElement;
		await fireEvent.input(input, { target: { value: 'typed sheet value' } });

		await clickBackdrop('[data-slot="drawer-overlay"]');

		await waitFor(() => expect(view.getByTestId('data-entry-sheet')).toBeTruthy());
		expect(input.value).toBe('typed sheet value');
		await fireEvent.click(view.getByTestId('sheet-cancel'));
		await waitFor(() => expect(view.queryByTestId('data-entry-sheet')).toBeNull());
	});

	it('keeps read-only dialogs dismissible from the backdrop, including the migrated one-off modal', async () => {
		const view = render(ModalDismissGuardHarness);
		await fireEvent.click(view.getByTestId('open-read-only'));
		await clickBackdrop('[data-slot="dialog-overlay"]');
		await waitFor(() => expect(view.queryByTestId('read-only-dialog')).toBeNull());

		await fireEvent.click(view.getByTestId('notification-help-trigger'));
		await waitFor(() => expect(view.getByTestId('notification-help-dialog')).toBeTruthy());
		await clickBackdrop('[data-slot="dialog-overlay"]');
		await waitFor(() => expect(view.queryByTestId('notification-help-dialog')).toBeNull());
	});
});
