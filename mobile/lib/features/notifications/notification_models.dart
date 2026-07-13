/// A single landlord notification row, mirroring the API's `NotificationResponse`.
///
/// All string fields are defended with `?? ''` / nullable parsing so a partial
/// payload never throws (same posture as `payment.dart`). `type` and `severity`
/// are server-side string enums; we keep them as raw strings and map to
/// friendly labels / icons at the UI layer.
class AppNotification {
  const AppNotification({
    required this.id,
    required this.type,
    required this.title,
    required this.message,
    required this.severity,
    this.actionUrl,
    this.relatedEntityType,
    this.relatedEntityId,
    required this.isRead,
    required this.createdAt,
  });

  final int id;
  final String type;
  final String title;
  final String message;

  /// One of: Info, Success, Warning, Error (string enum on the wire). Unknown
  /// values fall back to neutral styling.
  final String severity;

  /// Server-emitted deep-link path, for example a canonical tenant-ledger
  /// entry route carrying both account and entry ids. May be absent.
  final String? actionUrl;
  final String? relatedEntityType;
  final int? relatedEntityId;

  final bool isRead;
  final DateTime createdAt;

  AppNotification copyWith({bool? isRead}) {
    return AppNotification(
      id: id,
      type: type,
      title: title,
      message: message,
      severity: severity,
      actionUrl: actionUrl,
      relatedEntityType: relatedEntityType,
      relatedEntityId: relatedEntityId,
      isRead: isRead ?? this.isRead,
      createdAt: createdAt,
    );
  }

  factory AppNotification.fromJson(Map<String, dynamic> json) {
    return AppNotification(
      id: (json['id'] as num?)?.toInt() ?? 0,
      type: json['type'] as String? ?? '',
      title: json['title'] as String? ?? '',
      message: json['message'] as String? ?? '',
      severity: json['severity'] as String? ?? 'Info',
      actionUrl: json['actionUrl'] as String?,
      relatedEntityType: json['relatedEntityType'] as String?,
      relatedEntityId: (json['relatedEntityId'] as num?)?.toInt(),
      isRead: json['isRead'] as bool? ?? false,
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '') ?? DateTime(0),
    );
  }
}
