enum AccountingHelpTopic {
  detailMode,
  cashPosition,
  cashFlow,
  generalLedger,
  journalDetail,
  tenantLedger,
  oneTimeCharge,
  tenantCredit,
  recurringCharge,
  accountingImpact,
}

class AccountingHelpEntry {
  const AccountingHelpEntry({
    required this.title,
    required this.tip,
    required this.slug,
  });

  final String title;
  final String tip;
  final String slug;

  Uri get uri => Uri.parse('https://rentalcommand.net/docs/$slug');
}

const knownAccountingHelpSlugs = <String>{
  'simple-vs-advanced-accounting',
  'lease-and-tenant-ledgers',
  'general-ledger',
  'audit-ready-books',
  'cash-flow-basics',
};

const accountingHelp = <AccountingHelpTopic, AccountingHelpEntry>{
  AccountingHelpTopic.detailMode: AccountingHelpEntry(
    title: 'Simple or Advanced?',
    tip:
        'Both modes use the same books. Simple uses everyday words; Advanced also shows account codes, debits, credits, and posting details.',
    slug: 'simple-vs-advanced-accounting',
  ),
  AccountingHelpTopic.cashPosition: AccountingHelpEntry(
    title: 'Cash and tenant deposits',
    tip:
        'Cash on hand includes deposit money. Cash after tenant deposits subtracts money still held for tenants, but the rest is not automatically free to spend.',
    slug: 'cash-flow-basics',
  ),
  AccountingHelpTopic.cashFlow: AccountingHelpEntry(
    title: 'How cash flow works',
    tip:
        'Cash received minus cash paid shows how cash moved. Profit or loss follows accounting records, so it can differ from cash movement.',
    slug: 'cash-flow-basics',
  ),
  AccountingHelpTopic.generalLedger: AccountingHelpEntry(
    title: 'What is the general ledger?',
    tip:
        'This is the complete accounting history behind your cash, income, expenses, debts, and tenant balances.',
    slug: 'general-ledger',
  ),
  AccountingHelpTopic.journalDetail: AccountingHelpEntry(
    title: 'Why every record has two sides',
    tip:
        'Each accounting record explains what increased and what decreased, so the books stay balanced and traceable.',
    slug: 'general-ledger',
  ),
  AccountingHelpTopic.tenantLedger: AccountingHelpEntry(
    title: 'How the tenant ledger works',
    tip:
        'Charges add what is owed. Payments and credits reduce it, while security deposits stay separate because they are held for the tenant.',
    slug: 'lease-and-tenant-ledgers',
  ),
  AccountingHelpTopic.oneTimeCharge: AccountingHelpEntry(
    title: 'One-time charges',
    tip:
        'Use a one-time charge for a specific amount, such as a utility reimbursement, replacement key, or documented damage charge.',
    slug: 'lease-and-tenant-ledgers',
  ),
  AccountingHelpTopic.tenantCredit: AccountingHelpEntry(
    title: 'Credits keep the history clear',
    tip:
        'A credit reduces what the tenant owes without pretending cash was received or deleting the original charge.',
    slug: 'lease-and-tenant-ledgers',
  ),
  AccountingHelpTopic.recurringCharge: AccountingHelpEntry(
    title: 'Recurring charges',
    tip:
        'A recurring charge creates future dated charges for rent, parking, storage, or another repeating amount.',
    slug: 'lease-and-tenant-ledgers',
  ),
  AccountingHelpTopic.accountingImpact: AccountingHelpEntry(
    title: 'Accounting impact',
    tip:
        'This shows how the payment, charge, expense, or deposit changed your books without replacing the original record.',
    slug: 'general-ledger',
  ),
};

AccountingHelpEntry accountingHelpFor(AccountingHelpTopic topic) =>
    accountingHelp[topic]!;
