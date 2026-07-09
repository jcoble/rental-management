class NoticeDraft {
  const NoticeDraft({
    required this.id,
    required this.leaseId,
    this.paymentId,
    required this.tenantId,
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
  final int leaseId;
  final int? paymentId;
  final int tenantId;
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
      leaseId: (json['leaseId'] as num?)?.toInt() ?? 0,
      paymentId: (json['paymentId'] as num?)?.toInt(),
      tenantId: (json['tenantId'] as num?)?.toInt() ?? 0,
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
