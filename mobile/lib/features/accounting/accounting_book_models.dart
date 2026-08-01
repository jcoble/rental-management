import '../money/expense_models.dart';

DateTime _dateTime(Object? value) {
  if (value is String) {
    return DateTime.tryParse(value) ??
        DateTime.fromMillisecondsSinceEpoch(0, isUtc: true);
  }
  return DateTime.fromMillisecondsSinceEpoch(0, isUtc: true);
}

DateTime? _nullableDateTime(Object? value) {
  if (value is! String || value.isEmpty) return null;
  return DateTime.tryParse(value);
}

DateTime _dateOnly(Object? value) => _dateTime(value);

String _string(Object? value) => value is String ? value : '';

String? _nullableString(Object? value) => value is String ? value : null;

List<T> _list<T>(Object? value, T Function(Map<String, dynamic>) fromJson) {
  if (value is! List) return <T>[];
  return value
      .whereType<Map<String, dynamic>>()
      .map(fromJson)
      .toList(growable: false);
}

List<String> _stringList(Object? value) {
  if (value is! List) return <String>[];
  return value.whereType<String>().toList(growable: false);
}

enum AccountType {
  asset('Asset'),
  liability('Liability'),
  equity('Equity'),
  income('Income'),
  expense('Expense'),
  unknown(null);

  const AccountType(this.wire);

  final String? wire;

  static AccountType fromJson(Object? value) {
    final raw = value?.toString().trim().toLowerCase();
    return values.firstWhere(
      (item) => item.wire?.toLowerCase() == raw,
      orElse: () => AccountType.unknown,
    );
  }
}

enum NormalBalance {
  debit('Debit'),
  credit('Credit'),
  unknown(null);

  const NormalBalance(this.wire);

  final String? wire;

  static NormalBalance fromJson(Object? value) {
    final raw = value?.toString().trim().toLowerCase();
    return values.firstWhere(
      (item) => item.wire?.toLowerCase() == raw,
      orElse: () => NormalBalance.unknown,
    );
  }
}

enum JournalSourceType {
  tenantCharge('TenantCharge'),
  tenantReceipt('TenantReceipt'),
  providerSettlement('ProviderSettlement'),
  tenantConcession('TenantConcession'),
  receivableWriteOff('ReceivableWriteOff'),
  securityDepositReceipt('SecurityDepositReceipt'),
  securityDepositRefund('SecurityDepositRefund'),
  securityDepositApplication('SecurityDepositApplication'),
  expensePayment('ExpensePayment'),
  billIncurred('BillIncurred'),
  billPayment('BillPayment'),
  bankTransfer('BankTransfer'),
  loanPayment('LoanPayment'),
  capitalPurchase('CapitalPurchase'),
  depreciation('Depreciation'),
  ownerContribution('OwnerContribution'),
  ownerDistribution('OwnerDistribution'),
  openingBalance('OpeningBalance'),
  unknown(null);

  const JournalSourceType(this.wire);

  final String? wire;

  static JournalSourceType fromJson(Object? value) {
    final raw = value?.toString().trim().toLowerCase();
    return values.firstWhere(
      (item) => item.wire?.toLowerCase() == raw,
      orElse: () => JournalSourceType.unknown,
    );
  }
}

/// The server-calculated page envelope used by accounting read models.
class AccountingPage<T> {
  const AccountingPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<T> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory AccountingPage.fromJson(
    Map<String, dynamic> json,
    T Function(Map<String, dynamic>) fromJson,
  ) {
    final items = _list(json['items'], fromJson);
    return AccountingPage(
      items: items,
      totalCount: (json['totalCount'] as num?)?.toInt() ?? items.length,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class ChartOfAccountsRow {
  const ChartOfAccountsRow({
    required this.id,
    required this.publicId,
    required this.code,
    required this.name,
    required this.accountType,
    required this.normalBalance,
    this.parentAccountId,
    this.systemKey,
    this.scheduleECategory,
    required this.isSystem,
    required this.isActive,
    required this.hasPostedLines,
  });

  final int id;
  final String publicId;
  final String code;
  final String name;
  final AccountType accountType;
  final NormalBalance normalBalance;
  final int? parentAccountId;
  final String? systemKey;
  final ScheduleECategory? scheduleECategory;
  final bool isSystem;
  final bool isActive;
  final bool hasPostedLines;

  factory ChartOfAccountsRow.fromJson(Map<String, dynamic> json) {
    final category = json['scheduleECategory'];
    return ChartOfAccountsRow(
      id: (json['id'] as num?)?.toInt() ?? 0,
      publicId: _string(json['publicId']),
      code: _string(json['code']),
      name: _string(json['name']),
      accountType: AccountType.fromJson(json['accountType']),
      normalBalance: NormalBalance.fromJson(json['normalBalance']),
      parentAccountId: (json['parentAccountId'] as num?)?.toInt(),
      systemKey: _nullableString(json['systemKey']),
      scheduleECategory: category is String
          ? ScheduleECategory.fromWire(category)
          : null,
      isSystem: json['isSystem'] as bool? ?? false,
      isActive: json['isActive'] as bool? ?? false,
      hasPostedLines: json['hasPostedLines'] as bool? ?? false,
    );
  }
}

class GeneralLedgerRow {
  const GeneralLedgerRow({
    required this.journalEntryPublicId,
    required this.lineId,
    required this.effectiveOn,
    required this.postedAtUtc,
    required this.sourceType,
    required this.sourceId,
    required this.sourceBusinessKey,
    required this.description,
    required this.accountId,
    required this.accountCode,
    required this.accountName,
    required this.accountType,
    required this.normalBalance,
    required this.debitAmount,
    required this.creditAmount,
    required this.currency,
    this.propertyId,
    this.unitId,
    this.tenantAccountId,
    this.ownerEntityId,
    this.runningBalance,
  });

  final String journalEntryPublicId;
  final int lineId;
  final DateTime effectiveOn;
  final DateTime postedAtUtc;
  final JournalSourceType sourceType;
  final int sourceId;
  final String sourceBusinessKey;
  final String description;
  final int accountId;
  final String accountCode;
  final String accountName;
  final AccountType accountType;
  final NormalBalance normalBalance;
  final double debitAmount;
  final double creditAmount;
  final String currency;
  final int? propertyId;
  final int? unitId;
  final int? tenantAccountId;
  final int? ownerEntityId;
  final double? runningBalance;

  factory GeneralLedgerRow.fromJson(Map<String, dynamic> json) =>
      GeneralLedgerRow(
        journalEntryPublicId: _string(json['journalEntryPublicId']),
        lineId: (json['lineId'] as num?)?.toInt() ?? 0,
        effectiveOn: _dateOnly(json['effectiveOn']),
        postedAtUtc: _dateTime(json['postedAtUtc']),
        sourceType: JournalSourceType.fromJson(json['sourceType']),
        sourceId: (json['sourceId'] as num?)?.toInt() ?? 0,
        sourceBusinessKey: _string(json['sourceBusinessKey']),
        description: _string(json['description']),
        accountId: (json['accountId'] as num?)?.toInt() ?? 0,
        accountCode: _string(json['accountCode']),
        accountName: _string(json['accountName']),
        accountType: AccountType.fromJson(json['accountType']),
        normalBalance: NormalBalance.fromJson(json['normalBalance']),
        debitAmount: (json['debitAmount'] as num?)?.toDouble() ?? 0,
        creditAmount: (json['creditAmount'] as num?)?.toDouble() ?? 0,
        currency: _string(json['currency']),
        propertyId: (json['propertyId'] as num?)?.toInt(),
        unitId: (json['unitId'] as num?)?.toInt(),
        tenantAccountId: (json['tenantAccountId'] as num?)?.toInt(),
        ownerEntityId: (json['ownerEntityId'] as num?)?.toInt(),
        runningBalance: (json['runningBalance'] as num?)?.toDouble(),
      );
}

class AllocationRef {
  const AllocationRef({
    required this.targetSourceId,
    required this.targetPublicId,
    required this.targetDescription,
    required this.amount,
    required this.effectiveOn,
  });

  final int targetSourceId;
  final String targetPublicId;
  final String targetDescription;
  final double amount;
  final DateTime effectiveOn;

  factory AllocationRef.fromJson(Map<String, dynamic> json) => AllocationRef(
    targetSourceId: (json['targetSourceId'] as num?)?.toInt() ?? 0,
    targetPublicId: _string(json['targetPublicId']),
    targetDescription: _string(json['targetDescription']),
    amount: (json['amount'] as num?)?.toDouble() ?? 0,
    effectiveOn: _dateOnly(json['effectiveOn']),
  );
}

class JournalDetail {
  const JournalDetail({
    required this.publicId,
    required this.description,
    required this.effectiveOn,
    required this.postedAtUtc,
    required this.sourceType,
    required this.sourceId,
    required this.sourceBusinessKey,
    this.actor,
    required this.attemptId,
    required this.atomicReceiptId,
    required this.idempotencyDigest,
    required this.currency,
    required this.lines,
    required this.totalDebits,
    required this.totalCredits,
    required this.isBalanced,
    this.reversesJournalEntryPublicId,
    required this.reversalPublicIds,
    this.auditLink,
    required this.documentIds,
    this.bankReconciliationEvidence,
  });

  final String publicId;
  final String description;
  final DateTime effectiveOn;
  final DateTime postedAtUtc;
  final JournalSourceType sourceType;
  final int sourceId;
  final String sourceBusinessKey;
  final String? actor;
  final String attemptId;
  final String atomicReceiptId;
  final String idempotencyDigest;
  final String currency;
  final List<JournalDetailLine> lines;
  final double totalDebits;
  final double totalCredits;
  final bool isBalanced;
  final String? reversesJournalEntryPublicId;
  final List<String> reversalPublicIds;
  final String? auditLink;
  final List<int> documentIds;
  final BankReconciliationEvidence? bankReconciliationEvidence;

  factory JournalDetail.fromJson(Map<String, dynamic> json) => JournalDetail(
    publicId: _string(json['publicId']),
    description: _string(json['description']),
    effectiveOn: _dateOnly(json['effectiveOn']),
    postedAtUtc: _dateTime(json['postedAtUtc']),
    sourceType: JournalSourceType.fromJson(json['sourceType']),
    sourceId: (json['sourceId'] as num?)?.toInt() ?? 0,
    sourceBusinessKey: _string(json['sourceBusinessKey']),
    actor: _nullableString(json['actor']),
    attemptId: _string(json['attemptId']),
    atomicReceiptId: _string(json['atomicReceiptId']),
    idempotencyDigest: _string(json['idempotencyDigest']),
    currency: _string(json['currency']),
    lines: _list(json['lines'], JournalDetailLine.fromJson),
    totalDebits: (json['totalDebits'] as num?)?.toDouble() ?? 0,
    totalCredits: (json['totalCredits'] as num?)?.toDouble() ?? 0,
    isBalanced: json['isBalanced'] as bool? ?? false,
    reversesJournalEntryPublicId: _nullableString(
      json['reversesJournalEntryPublicId'],
    ),
    reversalPublicIds: _stringList(json['reversalPublicIds']),
    auditLink: _nullableString(json['auditLink']),
    documentIds: (json['documentIds'] as List? ?? const [])
        .whereType<num>()
        .map((value) => value.toInt())
        .toList(growable: false),
    bankReconciliationEvidence:
        json['bankReconciliationEvidence'] is Map<String, dynamic>
        ? BankReconciliationEvidence.fromJson(
            json['bankReconciliationEvidence'] as Map<String, dynamic>,
          )
        : null,
  );
}

class JournalDetailLine {
  const JournalDetailLine({
    required this.id,
    required this.accountId,
    required this.accountCode,
    required this.accountName,
    required this.debitAmount,
    required this.creditAmount,
    this.memo,
    this.propertyId,
    this.unitId,
    this.tenantAccountId,
    this.ownerEntityId,
  });

  final int id;
  final int accountId;
  final String accountCode;
  final String accountName;
  final double debitAmount;
  final double creditAmount;
  final String? memo;
  final int? propertyId;
  final int? unitId;
  final int? tenantAccountId;
  final int? ownerEntityId;

  factory JournalDetailLine.fromJson(Map<String, dynamic> json) =>
      JournalDetailLine(
        id: (json['id'] as num?)?.toInt() ?? 0,
        accountId: (json['accountId'] as num?)?.toInt() ?? 0,
        accountCode: _string(json['accountCode']),
        accountName: _string(json['accountName']),
        debitAmount: (json['debitAmount'] as num?)?.toDouble() ?? 0,
        creditAmount: (json['creditAmount'] as num?)?.toDouble() ?? 0,
        memo: _nullableString(json['memo']),
        propertyId: (json['propertyId'] as num?)?.toInt(),
        unitId: (json['unitId'] as num?)?.toInt(),
        tenantAccountId: (json['tenantAccountId'] as num?)?.toInt(),
        ownerEntityId: (json['ownerEntityId'] as num?)?.toInt(),
      );
}

class SourceJournalSummary {
  const SourceJournalSummary({
    required this.publicId,
    required this.effectiveOn,
    required this.postedAtUtc,
    required this.sourceType,
    required this.description,
    required this.totalDebits,
    required this.totalCredits,
    required this.isReversal,
    this.reversesPublicId,
  });

  final String publicId;
  final DateTime effectiveOn;
  final DateTime postedAtUtc;
  final JournalSourceType sourceType;
  final String description;
  final double totalDebits;
  final double totalCredits;
  final bool isReversal;
  final String? reversesPublicId;

  factory SourceJournalSummary.fromJson(Map<String, dynamic> json) =>
      SourceJournalSummary(
        publicId: _string(json['publicId']),
        effectiveOn: _dateOnly(json['effectiveOn']),
        postedAtUtc: _dateTime(json['postedAtUtc']),
        sourceType: JournalSourceType.fromJson(json['sourceType']),
        description: _string(json['description']),
        totalDebits: (json['totalDebits'] as num?)?.toDouble() ?? 0,
        totalCredits: (json['totalCredits'] as num?)?.toDouble() ?? 0,
        isReversal: json['isReversal'] as bool? ?? false,
        reversesPublicId: _nullableString(json['reversesPublicId']),
      );
}

class BankReconciliationEvidence {
  const BankReconciliationEvidence({
    this.bankTransactionId,
    this.bankAccountLabel,
    this.matchedOn,
    this.status,
  });

  final int? bankTransactionId;
  final String? bankAccountLabel;
  final DateTime? matchedOn;
  final String? status;

  factory BankReconciliationEvidence.fromJson(Map<String, dynamic> json) =>
      BankReconciliationEvidence(
        bankTransactionId: (json['bankTransactionId'] as num?)?.toInt(),
        bankAccountLabel: _nullableString(json['bankAccountLabel']),
        matchedOn: _nullableDateTime(json['matchedOn']),
        status: _nullableString(json['status']),
      );
}

class TrialBalanceRow {
  const TrialBalanceRow({
    required this.accountId,
    required this.accountCode,
    required this.accountName,
    required this.accountType,
    required this.debitBalance,
    required this.creditBalance,
    required this.currency,
  });

  final int accountId;
  final String accountCode;
  final String accountName;
  final AccountType accountType;
  final double debitBalance;
  final double creditBalance;
  final String currency;

  factory TrialBalanceRow.fromJson(Map<String, dynamic> json) =>
      TrialBalanceRow(
        accountId: (json['accountId'] as num?)?.toInt() ?? 0,
        accountCode: _string(json['accountCode']),
        accountName: _string(json['accountName']),
        accountType: AccountType.fromJson(json['accountType']),
        debitBalance: (json['debitBalance'] as num?)?.toDouble() ?? 0,
        creditBalance: (json['creditBalance'] as num?)?.toDouble() ?? 0,
        currency: _string(json['currency']),
      );
}

class TrialBalanceResponse {
  const TrialBalanceResponse({
    required this.rows,
    required this.totalDebits,
    required this.totalCredits,
    required this.isBalanced,
  });

  final List<TrialBalanceRow> rows;
  final double totalDebits;
  final double totalCredits;
  final bool isBalanced;

  factory TrialBalanceResponse.fromJson(Map<String, dynamic> json) =>
      TrialBalanceResponse(
        rows: _list(json['rows'], TrialBalanceRow.fromJson),
        totalDebits: (json['totalDebits'] as num?)?.toDouble() ?? 0,
        totalCredits: (json['totalCredits'] as num?)?.toDouble() ?? 0,
        isBalanced: json['isBalanced'] as bool? ?? false,
      );
}

class FinancialStatementRow {
  const FinancialStatementRow({
    required this.accountId,
    required this.accountCode,
    required this.accountName,
    required this.amount,
    required this.currency,
  });

  final int accountId;
  final String accountCode;
  final String accountName;
  final double amount;
  final String currency;

  factory FinancialStatementRow.fromJson(Map<String, dynamic> json) =>
      FinancialStatementRow(
        accountId: (json['accountId'] as num?)?.toInt() ?? 0,
        accountCode: _string(json['accountCode']),
        accountName: _string(json['accountName']),
        amount: (json['amount'] as num?)?.toDouble() ?? 0,
        currency: _string(json['currency']),
      );
}

class StatementSection {
  const StatementSection({
    required this.label,
    required this.rows,
    required this.subtotal,
  });

  final String label;
  final List<FinancialStatementRow> rows;
  final double subtotal;

  factory StatementSection.fromJson(Map<String, dynamic> json) =>
      StatementSection(
        label: _string(json['label']),
        rows: _list(json['rows'], FinancialStatementRow.fromJson),
        subtotal: (json['subtotal'] as num?)?.toDouble() ?? 0,
      );
}

class StatementTotals {
  const StatementTotals({
    required this.total,
    this.netIncome,
    this.assets,
    this.liabilitiesAndEquity,
    this.currentEarnings,
    this.isBalanced,
  });

  final double total;
  final double? netIncome;
  final double? assets;
  final double? liabilitiesAndEquity;
  final double? currentEarnings;
  final bool? isBalanced;

  factory StatementTotals.fromJson(Map<String, dynamic> json) =>
      StatementTotals(
        total: (json['total'] as num?)?.toDouble() ?? 0,
        netIncome: (json['netIncome'] as num?)?.toDouble(),
        assets: (json['assets'] as num?)?.toDouble(),
        liabilitiesAndEquity: (json['liabilitiesAndEquity'] as num?)
            ?.toDouble(),
        currentEarnings: (json['currentEarnings'] as num?)?.toDouble(),
        isBalanced: json['isBalanced'] as bool?,
      );
}

class FinancialStatementResponse {
  const FinancialStatementResponse({
    required this.sections,
    required this.totals,
  });

  final List<StatementSection> sections;
  final StatementTotals totals;

  factory FinancialStatementResponse.fromJson(Map<String, dynamic> json) =>
      FinancialStatementResponse(
        sections: _list(json['sections'], StatementSection.fromJson),
        totals: StatementTotals.fromJson(
          json['totals'] as Map<String, dynamic>? ?? const {},
        ),
      );
}

class MoneyPositionResponse {
  const MoneyPositionResponse({
    required this.asOfUtc,
    required this.fromUtc,
    required this.toUtc,
    required this.totalCashOnHand,
    required this.tenantDepositsHeld,
    required this.cashAfterTenantDeposits,
    required this.rentStillOwed,
    required this.loanBalance,
    required this.bookEquity,
    required this.cashReceived,
    required this.cashPaid,
    required this.netCashMovement,
    required this.profitOrLoss,
  });

  final DateTime asOfUtc;
  final DateTime fromUtc;
  final DateTime toUtc;
  final double totalCashOnHand;
  final double tenantDepositsHeld;
  final double cashAfterTenantDeposits;
  final double rentStillOwed;
  final double loanBalance;
  final double bookEquity;
  final double cashReceived;
  final double cashPaid;
  final double netCashMovement;
  final double profitOrLoss;

  factory MoneyPositionResponse.fromJson(Map<String, dynamic> json) =>
      MoneyPositionResponse(
        asOfUtc: _dateTime(json['asOfUtc']),
        fromUtc: _dateTime(json['fromUtc']),
        toUtc: _dateTime(json['toUtc']),
        totalCashOnHand: (json['totalCashOnHand'] as num?)?.toDouble() ?? 0,
        tenantDepositsHeld:
            (json['tenantDepositsHeld'] as num?)?.toDouble() ?? 0,
        cashAfterTenantDeposits:
            (json['cashAfterTenantDeposits'] as num?)?.toDouble() ?? 0,
        rentStillOwed: (json['rentStillOwed'] as num?)?.toDouble() ?? 0,
        loanBalance: (json['loanBalance'] as num?)?.toDouble() ?? 0,
        bookEquity: (json['bookEquity'] as num?)?.toDouble() ?? 0,
        cashReceived: (json['cashReceived'] as num?)?.toDouble() ?? 0,
        cashPaid: (json['cashPaid'] as num?)?.toDouble() ?? 0,
        netCashMovement: (json['netCashMovement'] as num?)?.toDouble() ?? 0,
        profitOrLoss: (json['profitOrLoss'] as num?)?.toDouble() ?? 0,
      );
}

class CashFlowSummaryResponse {
  const CashFlowSummaryResponse({
    required this.from,
    required this.to,
    required this.properties,
    required this.totalIncome,
    required this.totalOperatingExpenses,
    required this.totalNoi,
    required this.totalDebtService,
    required this.totalCashFlow,
  });

  final DateTime from;
  final DateTime to;
  final List<PropertyCashFlow> properties;
  final double totalIncome;
  final double totalOperatingExpenses;
  final double totalNoi;
  final double totalDebtService;
  final double totalCashFlow;

  factory CashFlowSummaryResponse.fromJson(Map<String, dynamic> json) =>
      CashFlowSummaryResponse(
        from: _dateTime(json['from']),
        to: _dateTime(json['to']),
        properties: _list(json['properties'], PropertyCashFlow.fromJson),
        totalIncome: (json['totalIncome'] as num?)?.toDouble() ?? 0,
        totalOperatingExpenses:
            (json['totalOperatingExpenses'] as num?)?.toDouble() ?? 0,
        totalNoi: (json['totalNoi'] as num?)?.toDouble() ?? 0,
        totalDebtService: (json['totalDebtService'] as num?)?.toDouble() ?? 0,
        totalCashFlow: (json['totalCashFlow'] as num?)?.toDouble() ?? 0,
      );
}

class PropertyCashFlow {
  const PropertyCashFlow({
    required this.propertyId,
    required this.propertyName,
    required this.income,
    required this.operatingExpenses,
    required this.noi,
    required this.debtService,
    required this.cashFlow,
  });

  final int propertyId;
  final String propertyName;
  final double income;
  final double operatingExpenses;
  final double noi;
  final double debtService;
  final double cashFlow;

  factory PropertyCashFlow.fromJson(Map<String, dynamic> json) =>
      PropertyCashFlow(
        propertyId: (json['propertyId'] as num?)?.toInt() ?? 0,
        propertyName: _string(json['propertyName']),
        income: (json['income'] as num?)?.toDouble() ?? 0,
        operatingExpenses: (json['operatingExpenses'] as num?)?.toDouble() ?? 0,
        noi: (json['noi'] as num?)?.toDouble() ?? 0,
        debtService: (json['debtService'] as num?)?.toDouble() ?? 0,
        cashFlow: (json['cashFlow'] as num?)?.toDouble() ?? 0,
      );
}
