export interface OverlayCollisionPadding {
	top: number;
	right: number;
	bottom: number;
	left: number;
}

export interface DialogOverlayBoundary {
	content: Element | null;
	footerHeight: number;
}

export type OverlayBoundary = Element | null;

export interface OverlayPositioningOptions {
	/**
	 * Adjusts the default viewport padding for a specific overlay family.
	 * Dialog overlays keep using their measured content/footer boundary.
	 */
	viewportPadding?: Partial<OverlayCollisionPadding>;
}

// Keep the established viewport guard for ordinary consumers. Dialog consumers
// use their content boundary below, where the measured footer replaces this
// global bottom reservation.
const VIEWPORT_PADDING: OverlayCollisionPadding = { top: 12, right: 12, bottom: 96, left: 12 };

export function getOverlayPositioning(
	dialogBoundary?: DialogOverlayBoundary | null,
	explicitBoundary?: OverlayBoundary | OverlayBoundary[],
	explicitPadding?: Partial<OverlayCollisionPadding> | number,
	options?: OverlayPositioningOptions
): {
	collisionBoundary: OverlayBoundary | OverlayBoundary[] | undefined;
	collisionPadding: OverlayCollisionPadding | number;
} {
	const collisionBoundary = explicitBoundary ?? dialogBoundary?.content ?? undefined;
	const viewportPadding = { ...VIEWPORT_PADDING, ...options?.viewportPadding };
	if (typeof explicitPadding === 'number') return { collisionBoundary, collisionPadding: explicitPadding };
	if (explicitPadding != null) {
		return { collisionBoundary, collisionPadding: { ...viewportPadding, ...explicitPadding } };
	}
	if (!dialogBoundary?.content) return { collisionBoundary, collisionPadding: viewportPadding };
	return {
		collisionBoundary,
		collisionPadding: {
			top: 12,
			right: 12,
			bottom: Math.max(12, dialogBoundary.footerHeight + 20),
			left: 12,
		},
	};
}
