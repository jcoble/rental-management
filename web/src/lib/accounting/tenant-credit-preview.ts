export function projectedRemainingCharge(openAmount: number, rawCreditAmount: string): number {
	const creditAmount = Number(rawCreditAmount);
	if (!Number.isFinite(creditAmount) || creditAmount <= 0) {
		return Math.max(0, openAmount);
	}

	return Math.max(0, openAmount - creditAmount);
}
