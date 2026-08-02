import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../../accounting/accounting_repository.dart';
import 'accounting_impact_block.dart';

class JournalEntryScreen extends ConsumerWidget {
  const JournalEntryScreen({super.key, required this.publicId});
  final String publicId;
  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final journal = ref.watch(journalEntryProvider(publicId));
    return Scaffold(
      appBar: AppBar(title: const Text('Journal entry')),
      body: journal.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (error, _) =>
            Center(child: Text("Couldn't load journal entry: $error")),
        data: (value) => ListView(
          padding: const EdgeInsets.all(16),
          children: [
            Text(
              value.description,
              style: Theme.of(context).textTheme.headlineSmall,
            ),
            const SizedBox(height: 8),
            Text('Effective ${value.effectiveOn.toLocal()}'),
            Text('Entered ${value.postedAtUtc.toLocal()}'),
            Text('Source ${value.sourceType}'),
            if (value.actor != null) Text('Entered by ${value.actor}'),
            const SizedBox(height: 16),
            AccountingImpactBlock(journal: value),
          ],
        ),
      ),
    );
  }
}
