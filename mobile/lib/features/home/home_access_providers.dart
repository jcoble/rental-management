import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/models/models.dart';
import '../ai/ai_models.dart';
import '../ai/ai_repository.dart';
import '../maintenance/work_orders_repository.dart';
import '../messages/message_models.dart';
import '../messages/messages_repository.dart';

final homeBriefingProvider = FutureProvider.autoDispose<BriefingResponse>((
  ref,
) {
  return ref.watch(aiRepositoryProvider).briefing();
});

final homeLatestMessagesProvider =
    FutureProvider.autoDispose<List<Conversation>>((ref) async {
      final conversations = await ref
          .watch(messagesRepositoryProvider)
          .listRecentConversations(take: 5);
      return conversations;
    });

final homeFieldQueueProvider = FutureProvider.autoDispose<List<WorkOrder>>((
  ref,
) async {
  final page = await ref
      .watch(workOrdersRepositoryProvider)
      .listWorkOrdersPage(
        const WorkOrderListQuery(openOnly: true, take: 5, sort: 'fieldQueue'),
      );
  return page.items;
});
