/// Message model.
///
/// Matches the API contract:
///   { id, portfolioId, propertyId?, propertyName?, unitId?, unitLabel?,
///     userAccountId, senderName?, subject, body, status, reply?,
///     createdAt (ISO), updatedAt (ISO) }
///
/// status ∈ Open | InProgress | Resolved | Closed
class Message {
  final int id;
  final int portfolioId;
  final int? propertyId;
  final String? propertyName;
  final int? unitId;
  final String? unitLabel;
  final int userAccountId;
  final String? senderName;
  final String subject;
  final String body;
  final String status;
  final String? reply;
  final DateTime createdAt;
  final DateTime updatedAt;

  const Message({
    required this.id,
    required this.portfolioId,
    this.propertyId,
    this.propertyName,
    this.unitId,
    this.unitLabel,
    required this.userAccountId,
    this.senderName,
    required this.subject,
    required this.body,
    required this.status,
    this.reply,
    required this.createdAt,
    required this.updatedAt,
  });

  factory Message.fromJson(Map<String, dynamic> json) {
    return Message(
      id: (json['id'] as num).toInt(),
      portfolioId: (json['portfolioId'] as num).toInt(),
      propertyId: (json['propertyId'] as num?)?.toInt(),
      propertyName: json['propertyName'] as String?,
      unitId: (json['unitId'] as num?)?.toInt(),
      unitLabel: json['unitLabel'] as String?,
      userAccountId: (json['userAccountId'] as num).toInt(),
      senderName: json['senderName'] as String?,
      subject: json['subject'] as String? ?? '',
      body: json['body'] as String? ?? '',
      status: json['status'] as String? ?? 'Open',
      reply: json['reply'] as String?,
      createdAt: DateTime.parse(json['createdAt'] as String),
      updatedAt: DateTime.parse(json['updatedAt'] as String),
    );
  }
}
