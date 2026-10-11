export type AccountingHelpEntry = {
	title: string;
	summary: string;
	href: `/docs/${string}`;
};

export const ACCOUNTING_HELP = {
	detailMode: {
		title: 'Simple or Advanced?',
		summary: 'Both modes use the same books. Simple uses everyday words; Advanced adds account codes, debit and credit columns, and posting details.',
		href: '/docs/simple-vs-advanced-accounting'
	},
	cashPosition: {
		title: 'Cash and tenant deposits',
		summary: 'Cash available includes deposit cash. Cash after tenant deposits subtracts money still held for tenants, but it is not a promise that the rest is free to spend.',
		href: '/docs/cash-flow-basics'
	},
	cashFlow: {
		title: 'How cash flow works',
		summary: 'Net operating income is rent and other operating cash minus operating costs. Net cash flow also subtracts full loan payments.',
		href: '/docs/cash-flow-basics'
	},
	generalLedger: {
		title: 'What is the general ledger?',
		summary: 'This is the complete accounting history behind your cash, income, expenses, debts, and tenant balances.',
		href: '/docs/general-ledger'
	},
	journalDetail: {
		title: 'Why every record has two sides',
		summary: 'Each accounting record explains what increased and what decreased, so the books stay balanced and traceable.',
		href: '/docs/general-ledger'
	},
	tenantLedger: {
		title: 'How charges and payments work',
		summary: 'Charges add what is owed. Payments and credits reduce it. Security deposits stay separate because they are held for the tenant.',
		href: '/docs/lease-and-tenant-ledgers'
	},
	oneTimeCharge: {
		title: 'One-time charges',
		summary: 'Use a one-time charge for a specific amount such as a utility reimbursement, replacement key, or documented damage charge.',
		href: '/docs/lease-and-tenant-ledgers'
	},
	tenantCredit: {
		title: 'Credits keep the history clear',
		summary: 'A credit reduces what the tenant owes without pretending cash was received or deleting the original charge.',
		href: '/docs/lease-and-tenant-ledgers'
	},
	recurringCharge: {
		title: 'Recurring charges',
		summary: 'A recurring charge creates future dated charges for rent, parking, storage, or another repeating amount.',
		href: '/docs/lease-and-tenant-ledgers'
	},
	financialStatements: {
		title: 'Where report totals come from',
		summary: 'Financial statements summarize the dated accounting records in your general ledger for the period or date you choose.',
		href: '/docs/audit-ready-books'
	},
	reports: {
		title: 'Reports you can trace',
		summary: 'Use reports for the answer, then follow the underlying records when you or your accountant need the detail.',
		href: '/docs/audit-ready-books'
	},
	accountingImpact: {
		title: 'Accounting impact',
		summary: 'This shows how the payment, charge, expense, or deposit changed your books without replacing the original record.',
		href: '/docs/general-ledger'
	}
} as const satisfies Record<string, AccountingHelpEntry>;
