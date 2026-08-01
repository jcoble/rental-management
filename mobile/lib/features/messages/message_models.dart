// Conversation (threaded messenger) models.
//
// Matches the API contract:
//   GET  /conversations              -> Conversation[] (summary)
//   GET  /conversations/{id}         -> Conversation (summary + messages[])
//   POST /conversations              -> Conversation
//   POST /conversations/{id}/messages -> Conversation
//
// A conversation is a thread between the landlord and a single tenant about a
// subject. Each message has a senderRole of 'Landlord' or 'Tenant' and the
// channels it was delivered on ('Portal' | 'Email' | 'Sms').

/// A single message (chat bubble) inside a conversation thread.
class ConversationMessage {
  final int id;

  /// 'Landlord' | 'Tenant' — see [isFromLandlord].
  final String senderRole;
  final String body;

  /// Delivery channels for this message, e.g. ['Portal', 'Email'].
  /// Only meaningful for landlord-sent messages.
  final List<String> channels;
  final DateTime createdAt;

  const ConversationMessage({
    required this.id,
    required this.senderRole,
    required this.body,
    this.channels = const [],
    required this.createdAt,
  });

  /// True when the landlord sent this message (right-aligned bubble).
  bool get isFromLandlord => senderRole == 'Landlord';

  factory ConversationMessage.fromJson(Map<String, dynamic> json) {
    final rawChannels = json['channels'];
    return ConversationMessage(
      id: (json['id'] as num).toInt(),
      senderRole: json['senderRole'] as String? ?? 'Tenant',
      body: json['body'] as String? ?? '',
      channels: rawChannels is List
          ? rawChannels.whereType<String>().toList(growable: false)
          : const [],
      createdAt: DateTime.parse(json['createdAt'] as String),
    );
  }
}

/// A conversation thread.
///
/// Always carries the summary fields used by the thread list. When loaded via
/// `GET /conversations/{id}` (or returned from a start/send mutation) the
/// [messages] list is populated with the full chronological history; the
/// thread-list endpoint leaves it empty.
class Conversation {
  final int id;
  final int tenantId;
  final String tenantName;
  final String counterpartyName;
  final String subject;
  final String? propertyName;
  final String? lastMessagePreview;
  final DateTime lastMessageAt;
  final int unreadCount;
  final int messageCount;

  /// Chronological (ascending) message history. Empty for thread-list rows.
  final List<ConversationMessage> messages;

  const Conversation({
    required this.id,
    required this.tenantId,
    required this.tenantName,
    String? counterpartyName,
    required this.subject,
    this.propertyName,
    this.lastMessagePreview,
    required this.lastMessageAt,
    required this.unreadCount,
    required this.messageCount,
    this.messages = const [],
  }) : counterpartyName = counterpartyName ?? tenantName;

  bool get hasUnread => unreadCount > 0;
  String get displayName {
    final counterparty = counterpartyName.trim();
    if (counterparty.isNotEmpty) return counterparty;
    final tenant = tenantName.trim();
    return tenant.isEmpty ? 'Conversation' : tenant;
  }

  factory Conversation.fromJson(Map<String, dynamic> json) {
    final rawMessages = json['messages'];
    return Conversation(
      id: (json['id'] as num).toInt(),
      tenantId: (json['tenantId'] as num).toInt(),
      tenantName: json['tenantName'] as String? ?? 'Tenant',
      counterpartyName: json['counterpartyName'] as String?,
      subject: json['subject'] as String? ?? '',
      propertyName: json['propertyName'] as String?,
      lastMessagePreview: json['lastMessagePreview'] as String?,
      lastMessageAt: DateTime.parse(json['lastMessageAt'] as String),
      unreadCount: (json['unreadCount'] as num?)?.toInt() ?? 0,
      messageCount: (json['messageCount'] as num?)?.toInt() ?? 0,
      messages: rawMessages is List
          ? rawMessages
                .whereType<Map<String, dynamic>>()
                .map(ConversationMessage.fromJson)
                .toList()
          : const [],
    );
  }
}

class ConversationListQuery {
  const ConversationListQuery({
    this.skip = 0,
    this.take = 20,
    this.search,
    this.sort = '-lastMessageAt',
    this.unreadOnly = false,
  });

  final int skip;
  final int take;
  final String? search;
  final String sort;
  final bool unreadOnly;

  @override
  bool operator ==(Object other) {
    return other is ConversationListQuery &&
        other.skip == skip &&
        other.take == take &&
        other.search == search &&
        other.sort == sort &&
        other.unreadOnly == unreadOnly;
  }

  @override
  int get hashCode => Object.hash(skip, take, search, sort, unreadOnly);
}

class ConversationListPage {
  const ConversationListPage({
    required this.items,
    required this.totalCount,
    required this.skip,
    required this.take,
  });

  final List<Conversation> items;
  final int totalCount;
  final int skip;
  final int take;

  bool get hasPrevious => skip > 0;
  bool get hasNext => skip + items.length < totalCount;

  factory ConversationListPage.fromJson(Map<String, dynamic> json) {
    final rawItems = json['items'];
    return ConversationListPage(
      items: (rawItems is List ? rawItems : const [])
          .whereType<Map<String, dynamic>>()
          .map(Conversation.fromJson)
          .toList(growable: false),
      totalCount: (json['totalCount'] as num?)?.toInt() ?? 0,
      skip: (json['skip'] as num?)?.toInt() ?? 0,
      take: (json['take'] as num?)?.toInt() ?? 20,
    );
  }
}
