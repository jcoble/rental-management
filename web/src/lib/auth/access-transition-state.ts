let activeAccessTransitions = 0;

export function beginAccessTransition(): void {
	activeAccessTransitions++;
}

export function endAccessTransition(): void {
	activeAccessTransitions = Math.max(0, activeAccessTransitions - 1);
}

export function isAccessTransitionInProgress(): boolean {
	return activeAccessTransitions > 0;
}
