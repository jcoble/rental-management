import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
	adjustFieldRectSize,
	clampDesignerZoom,
	clampFieldRect,
	moveFieldRect,
	parseFieldDimensionPercent,
	resizeFieldRect,
	type FieldRect
} from './lease-template-designer-utils.ts';

test('clampFieldRect keeps a placed field inside the PDF page', () => {
	const rect = clampFieldRect({
		xPct: 0.94,
		yPct: 0.97,
		widthPct: 0.12,
		heightPct: 0.08
	});

	assert.deepEqual(rect, {
		xPct: 0.88,
		yPct: 0.92,
		widthPct: 0.12,
		heightPct: 0.08
	});
});

test('moveFieldRect preserves size while clamping position to the page', () => {
	const rect: FieldRect = { xPct: 0.1, yPct: 0.2, widthPct: 0.25, heightPct: 0.06 };

	assert.deepEqual(moveFieldRect(rect, 0.9, -0.3), {
		xPct: 0.75,
		yPct: 0,
		widthPct: 0.25,
		heightPct: 0.06
	});
});

test('resizeFieldRect grows from the lower-right handle and respects page bounds', () => {
	const rect: FieldRect = { xPct: 0.82, yPct: 0.91, widthPct: 0.12, heightPct: 0.06 };

	assert.deepEqual(resizeFieldRect(rect, 0.2, 0.2, 'southEast'), {
		xPct: 0.82,
		yPct: 0.91,
		widthPct: 0.18,
		heightPct: 0.09
	});
});

test('adjustFieldRectSize supports wider, narrower, taller, and shorter button controls', () => {
	const rect: FieldRect = { xPct: 0.4, yPct: 0.4, widthPct: 0.12, heightPct: 0.05 };

	assert.deepEqual(adjustFieldRectSize(rect, 0.04, -0.02), {
		xPct: 0.4,
		yPct: 0.4,
		widthPct: 0.16,
		heightPct: 0.03
	});
});

test('clampDesignerZoom keeps manual PDF zoom usable', () => {
	assert.equal(clampDesignerZoom(20), 50);
	assert.equal(clampDesignerZoom(175), 175);
	assert.equal(clampDesignerZoom(260), 220);
});

test('parseFieldDimensionPercent accepts only displayed whole-number percentages', () => {
	assert.deepEqual(parseFieldDimensionPercent('3'), { valuePct: 0.03, error: null });
	assert.deepEqual(parseFieldDimensionPercent('100'), { valuePct: 1, error: null });

	for (const invalid of ['', '2', '101', '3.5', 'not-a-number']) {
		assert.deepEqual(parseFieldDimensionPercent(invalid), {
			valuePct: null,
			error: 'Enter a whole number from 3 to 100.'
		});
	}
});
