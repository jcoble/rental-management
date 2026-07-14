import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:image_picker/image_picker.dart';
import 'package:material_symbols_icons/symbols.dart';

import '../../core/api/api_exception.dart';
import 'technician_repository.dart';

class TechnicianAssignmentDetailScreen extends ConsumerStatefulWidget {
  const TechnicianAssignmentDetailScreen({
    super.key,
    required this.workOrderId,
  });
  final int workOrderId;

  @override
  ConsumerState<TechnicianAssignmentDetailScreen> createState() =>
      _TechnicianAssignmentDetailScreenState();
}

class _TechnicianAssignmentDetailScreenState
    extends ConsumerState<TechnicianAssignmentDetailScreen> {
  late Future<TechnicianAssignmentDetail> _future;
  final _statusNoteController = TextEditingController();
  final _entryNoteController = TextEditingController();
  final _quantityController = TextEditingController();
  final _unitController = TextEditingController(text: 'hours');
  final _messageController = TextEditingController();
  String _entryKind = 'Note';
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    _reload();
  }

  void _reload() {
    final repository = ref.read(technicianRepositoryProvider);
    _future = repository.detail(widget.workOrderId).then((detail) {
      if (detail.conversationId != null) {
        unawaited(
          repository
              .markConversationRead(widget.workOrderId)
              .onError((_, _) {}),
        );
      }
      return detail;
    });
  }

  Future<void> _refresh() async {
    setState(_reload);
    await _future;
  }

  @override
  void dispose() {
    _statusNoteController.dispose();
    _entryNoteController.dispose();
    _quantityController.dispose();
    _unitController.dispose();
    _messageController.dispose();
    super.dispose();
  }

  String _error(Object error) =>
      error is ApiException ? error.message : 'Something went wrong.';
  void _notice(String text) =>
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text)));

  Future<void> _update(TechnicianAssignmentDetail work, String status) async {
    setState(() => _saving = true);
    try {
      await ref
          .read(technicianRepositoryProvider)
          .update(
            work.id,
            work.updatedAtUtc,
            status: status,
            note: _statusNoteController.text.trim(),
          );
      _statusNoteController.clear();
      _notice('Assignment updated.');
      await _refresh();
    } catch (error) {
      _notice(_error(error));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _addEntry() async {
    setState(() => _saving = true);
    try {
      final repository = ref.read(technicianRepositoryProvider);
      if (_entryKind == 'Photo') {
        final image = await ImagePicker().pickImage(
          source: ImageSource.camera,
          imageQuality: 85,
        );
        if (image == null) return;
        final fileId = await repository.uploadPhoto(
          widget.workOrderId,
          File(image.path),
        );
        await repository.addEntry(
          widget.workOrderId,
          kind: 'Photo',
          photoFileId: fileId,
          note: _entryNoteController.text.trim(),
        );
      } else {
        final quantity = double.tryParse(_quantityController.text);
        await repository.addEntry(
          widget.workOrderId,
          kind: _entryKind,
          note: _entryNoteController.text.trim(),
          quantity: _entryKind == 'Time' || _entryKind == 'Material'
              ? quantity
              : null,
          unit: _entryKind == 'Time' || _entryKind == 'Material'
              ? _unitController.text.trim()
              : null,
        );
      }
      _entryNoteController.clear();
      _quantityController.clear();
      _notice('Field entry added.');
      await _refresh();
    } catch (error) {
      _notice(_error(error));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _sendMessage() async {
    final body = _messageController.text.trim();
    if (body.isEmpty) return;
    setState(() => _saving = true);
    try {
      await ref
          .read(technicianRepositoryProvider)
          .sendMessage(widget.workOrderId, body);
      _messageController.clear();
      await _refresh();
    } catch (error) {
      _notice(_error(error));
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Assignment')),
    body: FutureBuilder<TechnicianAssignmentDetail>(
      future: _future,
      builder: (context, snapshot) {
        if (snapshot.connectionState != ConnectionState.done)
          return const Center(child: CircularProgressIndicator());
        if (snapshot.hasError || !snapshot.hasData)
          return Center(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    snapshot.hasError
                        ? _error(snapshot.error!)
                        : 'Assignment unavailable.',
                  ),
                  const SizedBox(height: 12),
                  FilledButton(
                    onPressed: () => setState(_reload),
                    child: const Text('Try again'),
                  ),
                ],
              ),
            ),
          );
        final work = snapshot.data!;
        return RefreshIndicator(
          onRefresh: _refresh,
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 100),
            children: [
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(18),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        work.category,
                        style: Theme.of(context).textTheme.labelMedium,
                      ),
                      const SizedBox(height: 4),
                      Text(
                        work.title,
                        style: Theme.of(context).textTheme.headlineSmall
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                      const SizedBox(height: 10),
                      Text(work.description),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 12),
              _InfoCard(
                title: 'Visit details',
                children: [
                  _InfoRow(
                    icon: Symbols.location_on_rounded,
                    text:
                        '${work.address}${work.unit == null ? '' : ' · Unit ${work.unit}'}',
                  ),
                  _InfoRow(
                    icon: Symbols.schedule_rounded,
                    text: work.scheduledForUtc == null
                        ? 'Not scheduled'
                        : work.scheduledForUtc!.toLocal().toString(),
                  ),
                  if (work.accessInstructions?.isNotEmpty ?? false)
                    _InfoRow(
                      icon: Symbols.key_rounded,
                      text: work.accessInstructions!,
                    ),
                ],
              ),
              const SizedBox(height: 12),
              _InfoCard(
                title: 'Permitted contact',
                children: [
                  if (work.contactName == null)
                    const Text(
                      'No resident contact is shared for this assignment.',
                    ),
                  if (work.contactName != null)
                    _InfoRow(
                      icon: Symbols.person_rounded,
                      text: work.contactName!,
                    ),
                  if (work.contactPhone != null)
                    _InfoRow(
                      icon: Symbols.call_rounded,
                      text: work.contactPhone!,
                    ),
                  if (work.contactEmail != null)
                    _InfoRow(
                      icon: Symbols.mail_rounded,
                      text: work.contactEmail!,
                    ),
                ],
              ),
              const SizedBox(height: 12),
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Update progress',
                        style: Theme.of(context).textTheme.titleMedium
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                      const SizedBox(height: 12),
                      TextField(
                        controller: _statusNoteController,
                        decoration: const InputDecoration(
                          labelText: 'Optional progress note',
                        ),
                      ),
                      const SizedBox(height: 12),
                      Wrap(
                        spacing: 8,
                        runSpacing: 8,
                        children:
                            [
                                  'Scheduled',
                                  'InProgress',
                                  'WaitingParts',
                                  'OnHold',
                                  'Completed',
                                ]
                                .map(
                                  (status) => FilledButton.tonal(
                                    onPressed: _saving || status == work.status
                                        ? null
                                        : () => _update(work, status),
                                    child: Text(status),
                                  ),
                                )
                                .toList(),
                      ),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 12),
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Field log',
                        style: Theme.of(context).textTheme.titleMedium
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                      const SizedBox(height: 12),
                      SegmentedButton<String>(
                        segments: const [
                          ButtonSegment(value: 'Note', label: Text('Note')),
                          ButtonSegment(value: 'Time', label: Text('Time')),
                          ButtonSegment(
                            value: 'Material',
                            label: Text('Material'),
                          ),
                          ButtonSegment(
                            value: 'Photo',
                            icon: Icon(Symbols.photo_camera_rounded),
                          ),
                        ],
                        selected: {_entryKind},
                        onSelectionChanged: (value) =>
                            setState(() => _entryKind = value.first),
                        showSelectedIcon: false,
                      ),
                      const SizedBox(height: 12),
                      if (_entryKind != 'Photo')
                        TextField(
                          controller: _entryNoteController,
                          decoration: InputDecoration(
                            labelText: _entryKind == 'Note'
                                ? 'Note'
                                : 'Optional note',
                          ),
                        ),
                      if (_entryKind == 'Time' || _entryKind == 'Material') ...[
                        const SizedBox(height: 10),
                        Row(
                          children: [
                            Expanded(
                              child: TextField(
                                controller: _quantityController,
                                keyboardType: TextInputType.number,
                                decoration: const InputDecoration(
                                  labelText: 'Quantity',
                                ),
                              ),
                            ),
                            const SizedBox(width: 10),
                            Expanded(
                              child: TextField(
                                controller: _unitController,
                                decoration: const InputDecoration(
                                  labelText: 'Unit',
                                ),
                              ),
                            ),
                          ],
                        ),
                      ],
                      const SizedBox(height: 12),
                      FilledButton.icon(
                        onPressed: _saving ? null : _addEntry,
                        icon: Icon(
                          _entryKind == 'Photo'
                              ? Symbols.photo_camera_rounded
                              : Symbols.add_rounded,
                        ),
                        label: Text(
                          _entryKind == 'Photo' ? 'Take photo' : 'Add entry',
                        ),
                      ),
                      const Divider(height: 32),
                      for (final entry in work.entries) ...[
                        ListTile(
                          contentPadding: EdgeInsets.zero,
                          leading: Icon(
                            entry.kind == 'Photo'
                                ? Symbols.photo_rounded
                                : entry.kind == 'Time'
                                ? Symbols.timer_rounded
                                : entry.kind == 'Material'
                                ? Symbols.inventory_2_rounded
                                : Symbols.notes_rounded,
                          ),
                          title: Text(
                            entry.note?.isNotEmpty == true
                                ? entry.note!
                                : entry.kind,
                          ),
                          subtitle: entry.quantity == null
                              ? null
                              : Text('${entry.quantity} ${entry.unit ?? ''}'),
                        ),
                        if (entry.photoFileId != null)
                          FutureBuilder<Uint8List>(
                            future: ref
                                .read(technicianRepositoryProvider)
                                .downloadPhoto(entry.photoFileId!),
                            builder: (context, snapshot) =>
                                snapshot.hasData && snapshot.data!.isNotEmpty
                                ? ClipRRect(
                                    borderRadius: BorderRadius.circular(12),
                                    child: Image.memory(
                                      snapshot.data!,
                                      height: 220,
                                      width: double.infinity,
                                      fit: BoxFit.cover,
                                    ),
                                  )
                                : const SizedBox.shrink(),
                          ),
                      ],
                      if (work.entries.isEmpty)
                        const Text('No field entries yet.'),
                    ],
                  ),
                ),
              ),
              const SizedBox(height: 12),
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        'Assignment conversation',
                        style: Theme.of(context).textTheme.titleMedium
                            ?.copyWith(fontWeight: FontWeight.w700),
                      ),
                      const SizedBox(height: 12),
                      for (final message in work.messages)
                        Align(
                          alignment: message.sender == 'You'
                              ? Alignment.centerRight
                              : Alignment.centerLeft,
                          child: Container(
                            margin: const EdgeInsets.only(bottom: 8),
                            padding: const EdgeInsets.all(12),
                            constraints: const BoxConstraints(maxWidth: 300),
                            decoration: BoxDecoration(
                              color: message.sender == 'You'
                                  ? Theme.of(
                                      context,
                                    ).colorScheme.primaryContainer
                                  : Theme.of(
                                      context,
                                    ).colorScheme.surfaceContainerHighest,
                              borderRadius: BorderRadius.circular(16),
                            ),
                            child: Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Text(
                                  message.sender,
                                  style: Theme.of(context).textTheme.labelSmall,
                                ),
                                const SizedBox(height: 4),
                                Text(message.body),
                              ],
                            ),
                          ),
                        ),
                      if (work.messages.isEmpty)
                        const Padding(
                          padding: EdgeInsets.symmetric(vertical: 16),
                          child: Center(child: Text('No messages yet.')),
                        ),
                      const SizedBox(height: 8),
                      Row(
                        children: [
                          Expanded(
                            child: TextField(
                              controller: _messageController,
                              decoration: const InputDecoration(
                                hintText: 'Message the office or resident',
                              ),
                              onSubmitted: (_) => _sendMessage(),
                            ),
                          ),
                          IconButton.filled(
                            onPressed: _saving ? null : _sendMessage,
                            icon: const Icon(Symbols.send_rounded),
                          ),
                        ],
                      ),
                    ],
                  ),
                ),
              ),
            ],
          ),
        );
      },
    ),
  );
}

class _InfoCard extends StatelessWidget {
  const _InfoCard({required this.title, required this.children});
  final String title;
  final List<Widget> children;
  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            title,
            style: Theme.of(
              context,
            ).textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w700),
          ),
          const SizedBox(height: 12),
          ...children,
        ],
      ),
    ),
  );
}

class _InfoRow extends StatelessWidget {
  const _InfoRow({required this.icon, required this.text});
  final IconData icon;
  final String text;
  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 8),
    child: Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 19),
        const SizedBox(width: 8),
        Expanded(child: Text(text)),
      ],
    ),
  );
}
