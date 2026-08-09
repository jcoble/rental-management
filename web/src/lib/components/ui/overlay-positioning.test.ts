import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { getOverlayPositioning } from './overlay-positioning.ts';

describe('dialog-aware overlay positioning', () => {
	it('keeps ordinary page overlays on the viewport with the established padding', () => {
		assert.deepEqual(getOverlayPositioning(), {
			collisionBoundary: undefined,
			collisionPadding: { top: 12, right: 12, bottom: 96, left: 12 },
		});
	});

	it('reserves the measured dialog footer while retaining the dialog content boundary', () => {
		const boundary = {} as HTMLElement;
		assert.deepEqual(getOverlayPositioning({ content: boundary, footerHeight: 72 }), {
			collisionBoundary: boundary,
			collisionPadding: { top: 12, right: 12, bottom: 92, left: 12 },
		});
	});

	it('honors an explicit consumer collision contract', () => {
		const boundary = {} as HTMLElement;
		assert.deepEqual(
			getOverlayPositioning({ content: boundary, footerHeight: 72 }, undefined, { top: 4, right: 5, bottom: 6, left: 7 }),
			{ collisionBoundary: boundary, collisionPadding: { top: 4, right: 5, bottom: 6, left: 7 } }
		);
	});
});
