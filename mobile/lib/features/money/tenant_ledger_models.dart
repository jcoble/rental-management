import '../accounting/accounting_book_models.dart';

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

String _dateOnly(DateTime value) => value.toIso8601String().split('T').first;

String _string(Object? value) => value is String ? value : '';

String? _nullableString(Object? value) => value is String ? value : null;

List<T> _list<T>(Object? value, T Function(Map<String, dynamic>) fromJson) {
  if (value is! List) return <T>[];
  return value
      .whereType<Map<String, dynamic>>()
      .map(fromJson)
      .toList(growable: false);
}

enum TenantLedgerEntryType {
  openingBalance('OpeningBalance'),
  rentCharge('RentCharge'),
  addendumCharge('AddendumCharge'),
  lateFeeCharge('LateFeeCharge'),
  depositCharge('DepositCharge'),
  manualCharge('ManualCharge'),
  paymentReceipt('PaymentReceipt'),
  credit('Credit'),
  adjustment('Adjustment'),
  refund('Refund'),
  transferIn('TransferIn'),
  transferOut('TransferOut'),
  reversal('Reversal'),
  unknown(null);

  const TenantLedgerEntryType(this.wire);

  final String? wire;

  static TenantLedgerEntryType fromJson(Object? value) {
    final raw = value?.toString().trim().toLowerCase();
    return values.firstWhere(
      (item) => item.wire?.toLowerCase() == raw,
      orElse: () => TenantLedgerEntryType.unknown,
    );
  }
}

enum TenantLedgerDirection {
  debit('Debit'),
  credit('Credit'),
  unknown(null);

  const TenantLedgerDirection(this.wire);

  final String? wire;

  static TenantLedgerDirection fromJson(Object? value) {
    final raw = value?.toString().trim().toLowerCase();
    return values.firstWhere(
      (item) => item.wire?.toLowerCase() == raw,
      orElse: () => TenantLedgerDirection.unknown,
    );
  }
}

class TenantLedgerRow {
  const TenantLedgerRow({
    required this.tenantLedgerEntryId,
    required this.publicId,
    required this.sourceType,
    required this.sourceId,
    this.sourcePublicId,
    required this.effectiveOn,
    required this.postedAtUtc,
    required this.type,
    required this.description,
    required this.chargeAmount,
    required this.paymentAmount,
    required this.creditAmount,
    required this.runningAmountOwed,
    this.dueOn,
    required this.openAmount,
    required this.status,
    this.paymentMethod,
    this.reference,
    this.accountLabel,
    this.recurringScheduleContext,
    this.sourceDocumentContext,
    required this.allocations,
    this.reversesEntryId,
    this.replacedByEntryId,
    this.journalEntryPublicId,
    required this.currency,
    this.relatedTenantLedgerEntryId,
    this.relatedEntryDescription,
    this.categoryName,
    this.servicePeriodStartOn,
    this.servicePeriodEndOn,
  });

  final int tenantLedgerEntryId;
  final String publicId;
  final String sourceType;
  final int sourceId;
  final String? sourcePublicId;
  final DateTime effectiveOn;
  final DateTime postedAtUtc;
  final TenantLedgerEntryType type;
  final String description;
  final double chargeAmount;
  final double paymentAmount;
  final double creditAmount;
  final double runningAmountOwed;
  final DateTime? dueOn;
  final double openAmount;
  final String status;
  final String? paymentMethod;
  final String? reference;
  final String? accountLabel;
  final String? recurringScheduleContext;
  final String? sourceDocumentContext;
  final List<AllocationRef> allocations;
  final int? reversesEntryId;
  final int? replacedByEntryId;
  final String? journalEntryPublicId;
  final String currency;

  /// Optional linkage and service-period fields from the accounting plan's
  /// tenant-ledger read-model contract.
  final int? relatedTenantLedgerEntryId;
  final String? relatedEntryDescription;
  final String? categoryName;
  final DateTime? servicePeriodStartOn;
  final DateTime? servicePeriodEndOn;

  factory TenantLedgerRow.fromJson(
    Map<String, dynamic> json,
  ) => TenantLedgerRow(
    tenantLedgerEntryId: (json['tenantLedgerEntryId'] as num?)?.toInt() ?? 0,
    publicId: _string(json['publicId']),
    sourceType: _string(json['sourceType']),
    sourceId: (json['sourceId'] as num?)?.toInt() ?? 0,
    sourcePublicId: _nullableString(json['sourcePublicId']),
    effectiveOn: _dateTime(json['effectiveOn']),
    postedAtUtc: _dateTime(json['postedAtUtc']),
    type: TenantLedgerEntryType.fromJson(json['type']),
    description: _string(json['description']),
    chargeAmount: (json['chargeAmount'] as num?)?.toDouble() ?? 0,
    paymentAmount: (json['paymentAmount'] as num?)?.toDouble() ?? 0,
    creditAmount: (json['creditAmount'] as num?)?.toDouble() ?? 0,
    runningAmountOwed: (json['runningAmountOwed'] as num?)?.toDouble() ?? 0,
    dueOn: _nullableDateTime(json['dueOn']),
    openAmount: (json['openAmount'] as num?)?.toDouble() ?? 0,
    status: _string(json['status']),
    paymentMethod: _nullableString(json['paymentMethod']),
    reference: _nullableString(json['reference']),
    accountLabel: _nullableString(json['accountLabel']),
    recurringScheduleContext: _nullableString(json['recurringScheduleContext']),
    sourceDocumentContext: _nullableString(json['sourceDocumentContext']),
    allocations: _list(json['allocations'], AllocationRef.fromJson),
    reversesEntryId: (json['reversesEntryId'] as num?)?.toInt(),
    replacedByEntryId: (json['replacedByEntryId'] as num?)?.toInt(),
    journalEntryPublicId: _nullableString(json['journalEntryPublicId']),
    currency: _string(json['currency']),
    relatedTenantLedgerEntryId: (json['relatedTenantLedgerEntryId'] as num?)
        ?.toInt(),
    relatedEntryDescription: _nullableString(json['relatedEntryDescription']),
    categoryName: _nullableString(json['categoryName']),
    servicePeriodStartOn: _nullableDateTime(json['servicePeriodStartOn']),
    servicePeriodEndOn: _nullableDateTime(json['servicePeriodEndOn']),
  );
}

class TenantMonthSummary {
  const TenantMonthSummary({
    required this.year,
    required this.month,
    required this.currency,
    required this.openingBalance,
    required this.chargeAmount,
    required this.paymentAmount,
    required this.creditAmount,
    required this.closingBalance,
  });

  final int year;
  final int month;
  final String currency;
  final double openingBalance;
  final double chargeAmount;
  final double paymentAmount;
  final double creditAmount;
  final double closingBalance;

  factory TenantMonthSummary.fromJson(Map<String, dynamic> json) =>
      TenantMonthSummary(
        year: (json['year'] as num?)?.toInt() ?? 0,
        month: (json['month'] as num?)?.toInt() ?? 0,
        currency: _string(json['currency']),
        openingBalance: (json['openingBalance'] as num?)?.toDouble() ?? 0,
        chargeAmount: (json['chargeAmount'] as num?)?.toDouble() ?? 0,
        paymentAmount: (json['paymentAmount'] as num?)?.toDouble() ?? 0,
        creditAmount: (json['creditAmount'] as num?)?.toDouble() ?? 0,
        closingBalance: (json['closingBalance'] as num?)?.toDouble() ?? 0,
      );
}

class TenantLedgerPeriodSummary {
  const TenantLedgerPeriodSummary({
    required this.periodMonths,
    required this.currency,
    required this.chargeAmount,
    required this.paymentAmount,
    required this.creditAmount,
    required this.endingBalance,
    required this.agingCurrent,
    required this.aging1To30,
    required this.aging31To60,
    required this.aging61To90,
    required this.aging90Plus,
  });

  final int periodMonths;
  final String currency;
  final double chargeAmount;
  final double paymentAmount;
  final double creditAmount;
  final double endingBalance;
  final double agingCurrent;
  final double aging1To30;
  final double aging31To60;
  final double aging61To90;
  final double aging90Plus;

  factory TenantLedgerPeriodSummary.fromJson(Map<String, dynamic> json) =>
      TenantLedgerPeriodSummary(
        periodMonths: (json['periodMonths'] as num?)?.toInt() ?? 0,
        currency: _string(json['currency']),
        chargeAmount: (json['chargeAmount'] as num?)?.toDouble() ?? 0,
        paymentAmount: (json['paymentAmount'] as num?)?.toDouble() ?? 0,
        creditAmount: (json['creditAmount'] as num?)?.toDouble() ?? 0,
        endingBalance: (json['endingBalance'] as num?)?.toDouble() ?? 0,
        agingCurrent: (json['agingCurrent'] as num?)?.toDouble() ?? 0,
        aging1To30: (json['aging1To30'] as num?)?.toDouble() ?? 0,
        aging31To60: (json['aging31To60'] as num?)?.toDouble() ?? 0,
        aging61To90: (json['aging61To90'] as num?)?.toDouble() ?? 0,
        aging90Plus: (json['aging90Plus'] as num?)?.toDouble() ?? 0,
      );
}

class RecurringTenantChargeRow {
  const RecurringTenantChargeRow({
    required this.id,
    required this.publicId,
    required this.tenantAccountId,
    this.leaseAgreementId,
    required this.displayName,
    required this.amount,
    required this.currency,
    required this.ledgerAccountId,
    required this.effectiveStartOn,
    this.effectiveEndOn,
    required this.monthlyDueDay,
    required this.nextRunDate,
    required this.isActive,
    this.propertyId,
    this.unitId,
  });

  final int id;
  final String publicId;
  final int tenantAccountId;
  final int? leaseAgreementId;
  final String displayName;
  final double amount;
  final String currency;
  final int ledgerAccountId;
  final DateTime effectiveStartOn;
  final DateTime? effectiveEndOn;
  final int monthlyDueDay;
  final DateTime nextRunDate;
  final bool isActive;
  final int? propertyId;
  final int? unitId;

  factory RecurringTenantChargeRow.fromJson(Map<String, dynamic> json) =>
      RecurringTenantChargeRow(
        id: (json['id'] as num?)?.toInt() ?? 0,
        publicId: _string(json['publicId']),
        tenantAccountId: (json['tenantAccountId'] as num?)?.toInt() ?? 0,
        leaseAgreementId: (json['leaseAgreementId'] as num?)?.toInt(),
        displayName: _string(json['displayName']),
        amount: (json['amount'] as num?)?.toDouble() ?? 0,
        currency: _string(json['currency']),
        ledgerAccountId: (json['ledgerAccountId'] as num?)?.toInt() ?? 0,
        effectiveStartOn: _dateTime(json['effectiveStartOn']),
        effectiveEndOn: _nullableDateTime(json['effectiveEndOn']),
        monthlyDueDay: (json['monthlyDueDay'] as num?)?.toInt() ?? 0,
        nextRunDate: _dateTime(json['nextRunDate']),
        isActive: json['isActive'] as bool? ?? false,
        propertyId: (json['propertyId'] as num?)?.toInt(),
        unitId: (json['unitId'] as num?)?.toInt(),
      );
}

class RecordTenantReceiptInput {
  const RecordTenantReceiptInput({
    required this.amount,
    required this.effectiveOn,
    required this.description,
    required this.paymentMethodSummary,
    this.externalReference,
    this.payerName,
    this.checkNumber,
    this.bankName,
    this.sourceStoredFileId,
    this.targetChargeEntryId,
    this.allocateOldestCharges = true,
  });

  final double amount;
  final DateTime effectiveOn;
  final String description;
  final String paymentMethodSummary;
  final String? externalReference;
  final String? payerName;
  final String? checkNumber;
  final String? bankName;
  final int? sourceStoredFileId;
  final int? targetChargeEntryId;
  final bool allocateOldestCharges;

  Map<String, dynamic> toJson() => {
    'amount': amount,
    'effectiveOn': _dateOnly(effectiveOn),
    'description': description,
    'paymentMethodSummary': paymentMethodSummary,
    if (externalReference != null) 'externalReference': externalReference,
    if (payerName != null) 'payerName': payerName,
    if (checkNumber != null) 'checkNumber': checkNumber,
    if (bankName != null) 'bankName': bankName,
    if (sourceStoredFileId != null) 'sourceStoredFileId': sourceStoredFileId,
    if (targetChargeEntryId != null) 'targetChargeEntryId': targetChargeEntryId,
    'allocateOldestCharges': allocateOldestCharges,
  };
}

class PostTenantChargeInput {
  const PostTenantChargeInput({
    required this.amount,
    required this.effectiveOn,
    required this.dueOn,
    required this.description,
    this.sourceStoredFileId,
    this.incomeLedgerAccountId,
    this.servicePeriodStartOn,
    this.servicePeriodEndOn,
  });

  final double amount;
  final DateTime effectiveOn;
  final DateTime dueOn;
  final String description;
  final int? sourceStoredFileId;
  final int? incomeLedgerAccountId;
  final DateTime? servicePeriodStartOn;
  final DateTime? servicePeriodEndOn;

  Map<String, dynamic> toJson() => {
    'amount': amount,
    'effectiveOn': _dateOnly(effectiveOn),
    'dueOn': _dateOnly(dueOn),
    'description': description,
    if (sourceStoredFileId != null) 'sourceStoredFileId': sourceStoredFileId,
    if (incomeLedgerAccountId != null)
      'incomeLedgerAccountId': incomeLedgerAccountId,
    if (servicePeriodStartOn != null)
      'servicePeriodStartOn': _dateOnly(servicePeriodStartOn!),
    if (servicePeriodEndOn != null)
      'servicePeriodEndOn': _dateOnly(servicePeriodEndOn!),
  };
}

class PostTenantCreditInput {
  const PostTenantCreditInput({
    required this.amount,
    required this.effectiveOn,
    required this.description,
    this.sourceStoredFileId,
    this.allocateOldestCharges = true,
    this.targetChargeEntryId,
    this.incomeLedgerAccountId,
  });

  final double amount;
  final DateTime effectiveOn;
  final String description;
  final int? sourceStoredFileId;
  final bool allocateOldestCharges;
  final int? targetChargeEntryId;
  final int? incomeLedgerAccountId;

  Map<String, dynamic> toJson() => {
    'amount': amount,
    'effectiveOn': _dateOnly(effectiveOn),
    'description': description,
    if (sourceStoredFileId != null) 'sourceStoredFileId': sourceStoredFileId,
    'allocateOldestCharges': allocateOldestCharges,
    if (targetChargeEntryId != null) 'targetChargeEntryId': targetChargeEntryId,
    if (incomeLedgerAccountId != null)
      'incomeLedgerAccountId': incomeLedgerAccountId,
  };
}

class ReverseTenantChargeInput {
  const ReverseTenantChargeInput({
    required this.effectiveOn,
    required this.reason,
    this.sourceStoredFileId,
  });

  final DateTime effectiveOn;
  final String reason;
  final int? sourceStoredFileId;

  Map<String, dynamic> toJson() => {
    'effectiveOn': _dateOnly(effectiveOn),
    'reason': reason,
    if (sourceStoredFileId != null) 'sourceStoredFileId': sourceStoredFileId,
  };
}

class ReverseTenantLedgerEntryInput {
  const ReverseTenantLedgerEntryInput({
    required this.reversesEntryId,
    required this.effectiveOn,
    required this.reason,
    this.sourceStoredFileId,
  });

  final int reversesEntryId;
  final DateTime effectiveOn;
  final String reason;
  final int? sourceStoredFileId;

  Map<String, dynamic> toJson() => {
    'reversesEntryId': reversesEntryId,
    'effectiveOn': _dateOnly(effectiveOn),
    'reason': reason,
    if (sourceStoredFileId != null) 'sourceStoredFileId': sourceStoredFileId,
  };
}

class CreateRecurringTenantChargeInput {
  const CreateRecurringTenantChargeInput({
    required this.displayName,
    required this.amount,
    required this.ledgerAccountId,
    this.leaseAgreementId,
    required this.effectiveStartOn,
    this.effectiveEndOn,
    required this.monthlyDueDay,
    this.nextRunDate,
    this.propertyId,
    this.unitId,
  });

  final String displayName;
  final double amount;
  final int ledgerAccountId;
  final int? leaseAgreementId;
  final DateTime effectiveStartOn;
  final DateTime? effectiveEndOn;
  final int monthlyDueDay;
  final DateTime? nextRunDate;
  final int? propertyId;
  final int? unitId;

  Map<String, dynamic> toJson() => {
    'displayName': displayName,
    'amount': amount,
    'ledgerAccountId': ledgerAccountId,
    if (leaseAgreementId != null) 'leaseAgreementId': leaseAgreementId,
    'effectiveStartOn': _dateOnly(effectiveStartOn),
    if (effectiveEndOn != null) 'effectiveEndOn': _dateOnly(effectiveEndOn!),
    'monthlyDueDay': monthlyDueDay,
    if (nextRunDate != null) 'nextRunDate': _dateOnly(nextRunDate!),
    if (propertyId != null) 'propertyId': propertyId,
    if (unitId != null) 'unitId': unitId,
  };
}

class UpdateRecurringTenantChargeInput {
  const UpdateRecurringTenantChargeInput({
    this.displayName,
    this.amount,
    this.ledgerAccountId,
    this.effectiveStartOn,
    this.effectiveEndOn,
    this.monthlyDueDay,
    this.nextRunDate,
    this.isActive,
    this.propertyId,
    this.unitId,
  });

  final String? displayName;
  final double? amount;
  final int? ledgerAccountId;
  final DateTime? effectiveStartOn;
  final DateTime? effectiveEndOn;
  final int? monthlyDueDay;
  final DateTime? nextRunDate;
  final bool? isActive;
  final int? propertyId;
  final int? unitId;

  Map<String, dynamic> toJson() => {
    if (displayName != null) 'displayName': displayName,
    if (amount != null) 'amount': amount,
    if (ledgerAccountId != null) 'ledgerAccountId': ledgerAccountId,
    if (effectiveStartOn != null)
      'effectiveStartOn': _dateOnly(effectiveStartOn!),
    if (effectiveEndOn != null) 'effectiveEndOn': _dateOnly(effectiveEndOn!),
    if (monthlyDueDay != null) 'monthlyDueDay': monthlyDueDay,
    if (nextRunDate != null) 'nextRunDate': _dateOnly(nextRunDate!),
    if (isActive != null) 'isActive': isActive,
    if (propertyId != null) 'propertyId': propertyId,
    if (unitId != null) 'unitId': unitId,
  };
}

class RecordTenantReceiptResult {
  const RecordTenantReceiptResult({
    required this.found,
    required this.tenantAccountId,
    required this.ledgerEntryId,
    required this.paymentAttemptId,
    required this.amount,
    required this.allocatedAmount,
    required this.allocationCount,
    required this.replayed,
  });

  final bool found;
  final int tenantAccountId;
  final int ledgerEntryId;
  final int paymentAttemptId;
  final double amount;
  final double allocatedAmount;
  final int allocationCount;
  final bool replayed;

  factory RecordTenantReceiptResult.fromJson(Map<String, dynamic> json) {
    final value = _valueJson(json);
    return RecordTenantReceiptResult(
      found: value['found'] as bool? ?? false,
      tenantAccountId: (value['tenantAccountId'] as num?)?.toInt() ?? 0,
      ledgerEntryId: (value['ledgerEntryId'] as num?)?.toInt() ?? 0,
      paymentAttemptId: (value['paymentAttemptId'] as num?)?.toInt() ?? 0,
      amount: (value['amount'] as num?)?.toDouble() ?? 0,
      allocatedAmount: (value['allocatedAmount'] as num?)?.toDouble() ?? 0,
      allocationCount: (value['allocationCount'] as num?)?.toInt() ?? 0,
      replayed: json['replayed'] as bool? ?? false,
    );
  }
}

class TenantChargeMutationResult {
  const TenantChargeMutationResult({
    required this.found,
    required this.applied,
    required this.tenantAccountId,
    required this.ledgerEntryId,
    this.reversesEntryId,
    required this.amount,
    this.error,
    required this.replayed,
  });

  final bool found;
  final bool applied;
  final int tenantAccountId;
  final int ledgerEntryId;
  final int? reversesEntryId;
  final double amount;
  final String? error;
  final bool replayed;

  factory TenantChargeMutationResult.fromJson(Map<String, dynamic> json) {
    final value = _valueJson(json);
    return TenantChargeMutationResult(
      found: value['found'] as bool? ?? false,
      applied: value['applied'] as bool? ?? false,
      tenantAccountId: (value['tenantAccountId'] as num?)?.toInt() ?? 0,
      ledgerEntryId: (value['ledgerEntryId'] as num?)?.toInt() ?? 0,
      reversesEntryId: (value['reversesEntryId'] as num?)?.toInt(),
      amount: (value['amount'] as num?)?.toDouble() ?? 0,
      error: _nullableString(value['error']),
      replayed: json['replayed'] as bool? ?? false,
    );
  }
}

class TenantLedgerMutationResult {
  const TenantLedgerMutationResult({
    required this.found,
    required this.applied,
    required this.tenantAccountId,
    required this.ledgerEntryId,
    this.reversesEntryId,
    required this.entryType,
    required this.direction,
    required this.amount,
    required this.allocatedAmount,
    required this.allocationCount,
    this.error,
    required this.replayed,
  });

  final bool found;
  final bool applied;
  final int tenantAccountId;
  final int ledgerEntryId;
  final int? reversesEntryId;
  final TenantLedgerEntryType entryType;
  final TenantLedgerDirection direction;
  final double amount;
  final double allocatedAmount;
  final int allocationCount;
  final String? error;
  final bool replayed;

  factory TenantLedgerMutationResult.fromJson(Map<String, dynamic> json) {
    final value = _valueJson(json);
    return TenantLedgerMutationResult(
      found: value['found'] as bool? ?? false,
      applied: value['applied'] as bool? ?? false,
      tenantAccountId: (value['tenantAccountId'] as num?)?.toInt() ?? 0,
      ledgerEntryId: (value['ledgerEntryId'] as num?)?.toInt() ?? 0,
      reversesEntryId: (value['reversesEntryId'] as num?)?.toInt(),
      entryType: TenantLedgerEntryType.fromJson(value['entryType']),
      direction: TenantLedgerDirection.fromJson(value['direction']),
      amount: (value['amount'] as num?)?.toDouble() ?? 0,
      allocatedAmount: (value['allocatedAmount'] as num?)?.toDouble() ?? 0,
      allocationCount: (value['allocationCount'] as num?)?.toInt() ?? 0,
      error: _nullableString(value['error']),
      replayed: json['replayed'] as bool? ?? false,
    );
  }
}

Map<String, dynamic> _valueJson(Map<String, dynamic> json) {
  final value = json['value'];
  return value is Map<String, dynamic> ? value : json;
}
