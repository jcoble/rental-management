export function shouldSeedLeaseReviewState(status: string | null | undefined, fieldCount: number): boolean {
	return (status === 'Reviewing' || status === 'Confirmed') && fieldCount > 0;
}
