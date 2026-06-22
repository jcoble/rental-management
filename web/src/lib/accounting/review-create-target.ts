export interface AccountingReviewCreateTargetInput {
	externalType: string;
	reason?: string | null;
}

export interface AccountingReviewCreateTarget {
	href: string;
	label: string;
}

export function accountingReviewCreateTarget(
	item: AccountingReviewCreateTargetInput,
): AccountingReviewCreateTarget | null {
	const externalType = item.externalType.trim();
	const reason = item.reason?.trim().toLowerCase() ?? '';

	if (externalType === 'Payment') {
		return { href: '/tenants?create=1', label: 'Add tenant' };
	}

	if ((externalType === 'Purchase' || externalType === 'Bill') && !reason.includes('depreciation')) {
		return { href: '/vendors?create=1', label: 'Add vendor' };
	}

	return null;
}
