const REPORT_TITLES: Record<string, string> = {
	'income-expense-statement': 'Income and expenses',
	'property-pnl-summary': 'Income and expenses by property',
	'general-ledger': 'Complete money history',
	'cash-flow': 'Cash flow by month',
	'schedule-e': 'Rental tax summary',
	'year-end-packet': 'Year-end accountant packet',
	'rent-roll': 'Rent roll',
	'rent-ledger': 'Rent charges and payments',
	delinquency: 'Who is behind',
	'aged-receivables': 'Aged receivables',
	'owner-statement': 'Owner statement',
	'owner-distributions': 'Payments to owners',
	occupancy: 'Occupied and available rentals',
	'lease-expirations': 'Leases ending soon',
	'security-deposit-register': 'Security deposits',
	'vendor-1099': 'Vendor tax forms and payments',
	'work-orders': 'Maintenance report',
};

const REPORT_DESCRIPTIONS: Record<string, string> = {
	'income-expense-statement': 'See money received, expenses, and what remained for each month.',
	'property-pnl-summary': 'Compare money received, expenses, and what remained for each property.',
	'general-ledger': 'Review every payment and expense in date order, including the balance after each entry.',
	'cash-flow': 'Compare money in and money out for each month.',
	'schedule-e': 'Estimate rental income and deductible expenses by property for your accountant.',
	'year-end-packet': 'Download one PDF with the main reports your accountant is likely to need.',
	'rent-roll': 'See every unit, who lives there, the lease terms, deposits, and current balance.',
	'rent-ledger': 'See rent charges, payments received, and the running balance for each rental.',
	delinquency: 'See who still owes money and how long each balance has been overdue.',
	'aged-receivables': 'Who owes what and for how long, grouped by property and age.',
	'owner-statement': 'Summarize income, expenses, fees, and payments for one owner.',
	'owner-distributions': 'See what each owner earned, what was paid, and what remains to be paid.',
	occupancy: 'Compare occupied and available rentals across properties.',
	'lease-expirations': 'Find leases ending within the number of days you choose.',
	'security-deposit-register': 'See money received, deductions, refunds, and the amount still held for each tenant.',
	'vendor-1099': 'See vendor payments and which vendors still need tax paperwork.',
	'work-orders': 'Review maintenance requests, progress, vendors, and costs for the selected dates.',
};

const CATEGORY_TITLES: Record<string, string> = {
	accounting: 'Income and expenses',
	'rent-payments': 'Rent and payments',
	owners: 'Owners',
	operations: 'Rentals and maintenance',
};

export function reportTitle(key: string, fallback: string): string {
	return REPORT_TITLES[key] ?? fallback;
}

export function reportDescription(key: string, fallback: string): string {
	return REPORT_DESCRIPTIONS[key] ?? fallback;
}

export function reportCategoryTitle(key: string, fallback: string): string {
	return CATEGORY_TITLES[key] ?? fallback;
}
