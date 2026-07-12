class NoticeDraft {
  const NoticeDraft({
    required this.id,
    required this.leaseManagementId,
    required this.tenantAccountId,
    this.tenantLedgerEntryId,
    required this.recipientTenantId,
    required this.tenantName,
    this.propertyName,
    this.unitNumber,
    required this.noticeType,
    required this.status,
    required this.subject,
    required this.body,
    required this.reason,
    required this.triggerDate,
    this.conversationId,
  });

  final int id;
  final int leaseManagementId;
  final int tenantAccountId;
  final int? tenantLedgerEntryId;
  final int recipientTenantId;
  final String tenantName;
  final String? propertyName;
  final String? unitNumber;
  final String noticeType;
  final String status;
  final String subject;
  final String body;
  final String reason;
  final DateTime triggerDate;
  final int? conversationId;

  factory NoticeDraft.fromJson(Map<String, dynamic> json) {
    return NoticeDraft(
      id: (json['id'] as num).toInt(),
      leaseManagementId: (json['leaseManagementId'] as num?)?.toInt() ?? 0,
      tenantAccountId: (json['tenantAccountId'] as num?)?.toInt() ?? 0,
      tenantLedgerEntryId: (json['tenantLedgerEntryId'] as num?)?.toInt(),
      recipientTenantId: (json['recipientTenantId'] as num?)?.toInt() ?? 0,
      tenantName: json['tenantName'] as String? ?? '',
      propertyName: json['propertyName'] as String?,
      unitNumber: json['unitNumber'] as String?,
      noticeType: json['noticeType'] as String? ?? '',
      status: json['status'] as String? ?? 'Draft',
      subject: json['subject'] as String? ?? '',
      body: json['body'] as String? ?? '',
      reason: json['reason'] as String? ?? '',
      triggerDate:
          DateTime.tryParse(json['triggerDate'] as String? ?? '') ??
          DateTime(0),
      conversationId: (json['conversationId'] as num?)?.toInt(),
    );
  }
}
