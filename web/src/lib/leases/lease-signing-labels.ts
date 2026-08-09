export function outsideEsignLabel(fullyExecutedAtUtc: string | null | undefined): string {
	return fullyExecutedAtUtc
		? 'Signed outside e-sign — no signing links to track.'
		: 'Signing handled outside e-sign — mark as signed when complete.';
}
