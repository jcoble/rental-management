export type LedgerTypeTone =
	| 'charge'
	| 'payment'
	| 'credit'
	| 'expense'
	| 'bank'
	| 'loan'
	| 'owner'
	| 'reversal';

const labels: Record<string, string> = {
	charge: 'Charge',
	tenantcharge: 'Charge',
	rentcharge: 'Charge',
	addendumcharge: 'Charge',
	latefeecharge: 'Charge',
	depositcharge: 'Charge',
	manualcharge: 'Charge',
	payment: 'Payment received',
	paymentreceived: 'Payment received',
	paymentreceipt: 'Payment received',
	tenantreceipt: 'Payment received',
	securitydepositreceipt: 'Payment received',
	credit: 'Credit',
	adjustment: 'Credit',
	tenantconcession: 'Credit',
	receivablewriteoff: 'Credit',
	refund: 'Credit',
	securitydepositrefund: 'Credit',
	securitydepositapplication: 'Credit',
	expense: 'Expense',
	expensepayment: 'Expense',
	capitalpurchase: 'Expense',
	depreciation: 'Expense',
	bill: 'Bill',
	billincurred: 'Bill',
	billpayment: 'Bill',
	banktransfer: 'Bank transfer',
	bankmovement: 'Bank transfer',
	providersettlement: 'Bank transfer',
	transferin: 'Bank transfer',
	transferout: 'Bank transfer',
	loanpayment: 'Loan payment',
	owneractivity: 'Owner activity',
	ownercontribution: 'Owner activity',
	ownerdistribution: 'Owner activity',
	openingbalance: 'Owner activity',
	reversal: 'Reversal'
};

const tones: Record<string, LedgerTypeTone> = {
	charge: 'charge',
	tenantcharge: 'charge',
	rentcharge: 'charge',
	addendumcharge: 'charge',
	latefeecharge: 'charge',
	depositcharge: 'charge',
	manualcharge: 'charge',
	payment: 'payment',
	paymentreceived: 'payment',
	paymentreceipt: 'payment',
	tenantreceipt: 'payment',
	securitydepositreceipt: 'payment',
	credit: 'credit',
	adjustment: 'credit',
	tenantconcession: 'credit',
	receivablewriteoff: 'credit',
	refund: 'credit',
	securitydepositrefund: 'credit',
	securitydepositapplication: 'credit',
	expense: 'expense',
	expensepayment: 'expense',
	capitalpurchase: 'expense',
	depreciation: 'expense',
	bill: 'expense',
	billincurred: 'expense',
	billpayment: 'expense',
	banktransfer: 'bank',
	bankmovement: 'bank',
	providersettlement: 'bank',
	transferin: 'bank',
	transferout: 'bank',
	loanpayment: 'loan',
	owneractivity: 'owner',
	ownercontribution: 'owner',
	ownerdistribution: 'owner',
	openingbalance: 'owner',
	reversal: 'reversal'
};

function key(type: string): string {
	return type.replace(/[\s_-]/g, '').toLowerCase();
}

export function ledgerTypeLabel(type: string): string {
	return labels[key(type)] ?? 'Activity';
}

export function ledgerTypeTone(type: string): LedgerTypeTone {
	return tones[key(type)] ?? 'owner';
}
