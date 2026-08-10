export function outsideEsignLabel(fullyExecutedAtUtc: string | null | undefined): string {
	return fullyExecutedAtUtc
		? 'Signed outside Rental Command — no signing links to track.'
		: 'Signing handled outside Rental Command — mark as signed when complete.';
}
