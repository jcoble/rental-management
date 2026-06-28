export type FieldRect = {
	xPct: number;
	yPct: number;
	widthPct: number;
	heightPct: number;
};

export type FieldResizeHandle = 'east' | 'south' | 'southEast';

const DEFAULT_LIMITS = {
	minWidthPct: 0.03,
	minHeightPct: 0.025,
	maxWidthPct: 1,
	maxHeightPct: 1
};

export function clampDesignerZoom(zoomPct: number) {
	return Math.min(220, Math.max(50, Math.round(zoomPct)));
}

export function clampFieldRect(rect: FieldRect): FieldRect {
	const widthPct = roundPct(
		clamp(rect.widthPct, DEFAULT_LIMITS.minWidthPct, DEFAULT_LIMITS.maxWidthPct)
	);
	const heightPct = roundPct(
		clamp(rect.heightPct, DEFAULT_LIMITS.minHeightPct, DEFAULT_LIMITS.maxHeightPct)
	);
	const xPct = roundPct(clamp(rect.xPct, 0, 1 - widthPct));
	const yPct = roundPct(clamp(rect.yPct, 0, 1 - heightPct));

	return { xPct, yPct, widthPct, heightPct };
}

export function moveFieldRect(rect: FieldRect, deltaXPct: number, deltaYPct: number): FieldRect {
	return clampFieldRect({
		...rect,
		xPct: rect.xPct + deltaXPct,
		yPct: rect.yPct + deltaYPct
	});
}

export function resizeFieldRect(
	rect: FieldRect,
	deltaXPct: number,
	deltaYPct: number,
	handle: FieldResizeHandle
): FieldRect {
	const maxWidthPct = Math.max(DEFAULT_LIMITS.minWidthPct, 1 - rect.xPct);
	const maxHeightPct = Math.max(DEFAULT_LIMITS.minHeightPct, 1 - rect.yPct);

	return clampFieldRect({
		...rect,
		widthPct: clamp(
			rect.widthPct + (handle === 'east' || handle === 'southEast' ? deltaXPct : 0),
			DEFAULT_LIMITS.minWidthPct,
			maxWidthPct
		),
		heightPct: clamp(
			rect.heightPct + (handle === 'south' || handle === 'southEast' ? deltaYPct : 0),
			DEFAULT_LIMITS.minHeightPct,
			maxHeightPct
		)
	});
}

export function adjustFieldRectSize(
	rect: FieldRect,
	widthDeltaPct: number,
	heightDeltaPct: number
): FieldRect {
	return clampFieldRect({
		...rect,
		widthPct: rect.widthPct + widthDeltaPct,
		heightPct: rect.heightPct + heightDeltaPct
	});
}

export function roundPct(value: number) {
	return Math.round(value * 10_000) / 10_000;
}

function clamp(value: number, min: number, max: number) {
	return Math.min(max, Math.max(min, value));
}
