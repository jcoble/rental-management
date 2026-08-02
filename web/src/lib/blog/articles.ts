export type BlogSection = {
	heading: string;
	paragraphs: string[];
	bullets?: string[];
};

export type BlogPost = {
	slug: string;
	title: string;
	seoTitle: string;
	description: string;
	summary: string;
	publishedOn: string;
	readingTime: string;
	intro: string[];
	sections: BlogSection[];
	cta: { heading: string; body: string; href: '/register'; label: string };
};

const signup = (body: string): BlogPost['cta'] => ({
	heading: 'Put the record behind the number',
	body,
	href: '/register',
	label: 'Start free'
});

export const BLOG_POSTS: readonly BlogPost[] = [
	{
		slug: 'why-your-rental-spreadsheet-will-fail-an-audit',
		title: 'Why your rental spreadsheet will fail an audit',
		seoTitle: 'Why Your Rental Spreadsheet Will Fail an Audit | Rental Command',
		description: 'Learn where rental bookkeeping spreadsheets break down during an audit and how landlords can keep clearer, traceable records.',
		summary: 'A spreadsheet can total your rentals. It usually cannot prove who changed a number, why it changed, or which document supports it.',
		publishedOn: '2026-08-02',
		readingTime: '6 minute read',
		intro: [
			'Your spreadsheet may be accurate today. The problem appears months later, when someone asks where one number came from.',
			'An audit is not only a math test. It is a records test. You need to connect a reported amount to dated transactions, properties, people, bank activity, and supporting documents.'
		],
		sections: [
			{
				heading: 'A total is not an explanation',
				paragraphs: ['A cell can show $18,420 of repairs. It does not automatically show which invoices make up that total, whether one was entered twice, or whether a personal cost slipped into the column.'],
				bullets: ['The original receipt or invoice', 'The property and unit involved', 'The date the cost took effect', 'The payee and payment record', 'Any later correction and its reason']
			},
			{
				heading: 'Spreadsheets quietly rewrite history',
				paragraphs: ['When an amount is wrong, the natural spreadsheet fix is to replace it. That makes the total right but erases what happened. A reviewer cannot tell whether the original was a typing error, a duplicate, a refund, or a real charge later reversed.', 'Stronger books preserve the original record and add a dated correction. The history remains understandable without keeping five versions of the same workbook.']
			},
			{
				heading: 'Transfers and deposits create convincing mistakes',
				paragraphs: ['Moving $5,000 from one bank account to another is not $5,000 of new income. A tenant security deposit is cash in the bank, but it is not rent you earned. Flat rows in a spreadsheet make both mistakes easy because each row has only one label and one amount.', 'Balanced accounting records show both sides: where money went and what obligation, income, expense, or asset changed.']
			},
			{
				heading: 'Build records you can trace',
				paragraphs: ['Record money events promptly, attach the supporting document, reconcile them to the bank, and correct mistakes without deleting the original. Review unusual balances before tax season.', 'No software can guarantee an audit result or decide the correct tax treatment for your situation. It can give you a far better starting point: books that are organized enough to inspect and explain.']
			}
		],
		cta: signup('Rental Command connects daily rental activity to dated accounting records, so a report total is not the end of the trail.')
	},
	{
		slug: 'lease-ledgers-what-every-landlord-should-track',
		title: 'Lease ledgers: what every landlord should track',
		seoTitle: 'Lease Ledgers: What Every Landlord Should Track | Rental Command',
		description: 'A practical guide to rental lease ledgers, including charges, payments, credits, balances, recurring rent, and tenant-visible history.',
		summary: 'A useful lease ledger explains every change to what a tenant owes. It should do more than list payments.',
		publishedOn: '2026-08-02',
		readingTime: '5 minute read',
		intro: [
			'A bank deposit tells you that money arrived. It does not tell you which rent charge it paid, whether part remains due, or why the tenant received a credit.',
			'That is the job of a lease or tenant ledger: one dated history of amounts charged, paid, credited, refunded, corrected, and still owed.'
		],
		sections: [
			{
				heading: 'Start with each charge',
				paragraphs: ['Post rent and other agreed charges as separate dated items. Include the effective date, due date, description, amount, and the rental account involved. Parking, pet rent, storage, and utility reimbursements should not disappear inside one vague rent line.']
			},
			{
				heading: 'Connect payments to what they paid',
				paragraphs: ['A payment needs the received date, amount, method, payer, and check or confirmation number when available. It should also show which open charges it reduced.', 'If a payment is not yet applied, keep it visible as a credit instead of forcing it against the wrong month.']
			},
			{
				heading: 'Use credits for reductions, not imaginary payments',
				paragraphs: ['A credit can correct an overcharge, recognize a concession, or reduce a specific charge. It lowers what the tenant owes without claiming that cash was received.', 'Keep the original charge in the history. The credit explains the change and avoids a dispute over a row that vanished.']
			},
			{
				heading: 'Show the balance after every entry',
				paragraphs: ['A running balance lets both sides see how the current amount was reached. Monthly opening balance, charges, payments or credits, and closing balance make a long lease easier to read.'],
				bullets: ['Current balance and past-due amount', 'Next charge and due date', 'Unapplied credit', 'Recurring-charge schedule', 'Links to receipts and supporting documents']
			},
			{
				heading: 'Keep the tenant view focused',
				paragraphs: ['Tenants need their own charges, payments, credits, deposit held, and balance. They do not need internal account codes, debit and credit columns, management notes, or another rental account.', 'A clear shared history reduces “what is this charge?” conversations because the answer is attached to the dated entry.']
			}
		],
		cta: signup('Rental Command keeps charges, payments, credits, and the tenant balance in one explainable history.')
	},
	{
		slug: 'security-deposits-bookkeeping-mistake-that-costs-landlords',
		title: 'Security deposits: the bookkeeping mistake that costs landlords',
		seoTitle: 'Security Deposit Bookkeeping Mistakes Landlords Make | Rental Command',
		description: 'See why tenant security deposits should stay separate from rental income and what landlords should track from receipt through refund.',
		summary: 'The costly mistake is treating a tenant deposit like earned rent. The cash may be in your account, but you may still owe it back.',
		publishedOn: '2026-08-02',
		readingTime: '5 minute read',
		intro: [
			'A security deposit increases your bank balance. That does not make it rental income. Until a lawful, documented deduction applies, you are holding money that may need to go back to the tenant.',
			'Mixing deposits with rent can overstate income, hide how much you owe tenants, and leave you short when move-out refunds are due.'
		],
		sections: [
			{
				heading: 'Track cash and the obligation together',
				paragraphs: ['When you receive a $1,500 deposit, record both facts: deposit cash increased by $1,500, and the amount held for the tenant increased by $1,500. Those two sides explain why the bank balance rose without calling the money income.']
			},
			{
				heading: 'Do not use the rent balance as the deposit balance',
				paragraphs: ['Rent owed and deposit held answer opposite questions. The rent ledger shows what the tenant owes you. The deposit record shows what you hold for the tenant.', 'Show the deposit beside the tenant account for convenience, but keep it out of rent charges, payments, credits, and the rent running balance.']
			},
			{
				heading: 'Keep a complete deposit history',
				paragraphs: ['For each tenant, keep the amount and date received, payment method, current held amount, deductions with reasons and documents, refunds with dates and references, and the final move-out statement.'],
				bullets: ['Receipt of funds', 'Any additional deposit amount', 'Documented deduction', 'Partial or final refund', 'Photos, invoices, and notices that support the record']
			},
			{
				heading: 'Know the difference between a deduction and an expense',
				paragraphs: ['A repair cost and a deposit deduction are related, but they are not the same record. The repair invoice records the cost to your business. The deduction records the amount retained from the tenant under the lease and applicable rules.', 'Keep both records and connect the supporting evidence rather than entering one vague net amount.']
			},
			{
				heading: 'Local rules still control',
				paragraphs: ['Deposit account, notice, deadline, interest, and deduction rules vary by location. Organized bookkeeping helps you follow the money, but it does not replace the lease, local law, or legal advice.', 'Review your local requirements and keep enough cash available to meet refund obligations when they come due.']
			}
		],
		cta: signup('Rental Command keeps deposit money, deductions, refunds, and supporting records separate from rent income.')
	}
] as const;

export function getBlogPost(slug: string): BlogPost | undefined {
	return BLOG_POSTS.find((post) => post.slug === slug);
}
