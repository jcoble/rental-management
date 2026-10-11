import 'package:flutter/material.dart';

import '../../core/models/lease.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'leases_repository.dart';

class IssuedAgreementRecoveryInput {
  const IssuedAgreementRecoveryInput({
    required this.reissueReason,
    required this.operationKey,
    this.voidNote,
  });

  final String? voidNote;
  final String reissueReason;
  final String operationKey;
}

Future<IssuedAgreementRecoveryInput?> showIssuedAgreementRecoverySheet(
  BuildContext context, {
  required LeaseAgreementHistory source,
}) => showModalBottomSheet<IssuedAgreementRecoveryInput>(
  context: context,
  isScrollControlled: true,
  useSafeArea: true,
  builder: (_) => _IssuedAgreementRecoverySheet(source: source),
);

class _IssuedAgreementRecoverySheet extends StatefulWidget {
  const _IssuedAgreementRecoverySheet({required this.source});

  final LeaseAgreementHistory source;

  @override
  State<_IssuedAgreementRecoverySheet> createState() =>
      _IssuedAgreementRecoverySheetState();
}

class _IssuedAgreementRecoverySheetState
    extends State<_IssuedAgreementRecoverySheet> {
  late final TextEditingController _voidNote;
  late final TextEditingController _reissueReason;

  bool get _alreadyVoided => widget.source.voidedAt != null;

  @override
  void initState() {
    super.initState();
    _voidNote = TextEditingController();
    _reissueReason = TextEditingController();
  }

  @override
  void dispose() {
    _voidNote.dispose();
    _reissueReason.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => TabbedFormSheet(
    title: _alreadyVoided
        ? 'Create replacement draft'
        : 'Void and replace issued agreement',
    saveLabel: _alreadyVoided
        ? 'Create replacement'
        : 'Void and create replacement',
    saving: false,
    onSave: () async {
      final voidNote = _voidNote.text.trim();
      final reissueReason = _reissueReason.text.trim();
      Navigator.of(context).pop(
        IssuedAgreementRecoveryInput(
          voidNote: _alreadyVoided || voidNote.isEmpty ? null : voidNote,
          reissueReason: reissueReason,
          operationKey: LeaseManagementsRepository.newOperationKey(),
        ),
      );
    },
    tabs: [
      TabbedFormStepSpec(
        label: 'Recovery',
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'The issued PDF, content hash, signer snapshot, and audit history remain on ${widget.source.agreementNumber}. The replacement keeps the same agreement type and will not govern until fully executed.',
            ),
            const SizedBox(height: 16),
            if (!_alreadyVoided) ...[
              TextFormField(
                controller: _voidNote,
                maxLength: 2000,
                minLines: 2,
                maxLines: 4,
                decoration: const InputDecoration(labelText: 'Void note'),
                validator: (value) => (value?.trim().length ?? 0) > 2000
                    ? 'Void note cannot exceed 2,000 characters.'
                    : null,
              ),
              const SizedBox(height: 12),
            ],
            TextFormField(
              controller: _reissueReason,
              maxLength: 1000,
              minLines: 3,
              maxLines: 5,
              decoration: const InputDecoration(
                labelText: 'Why this document must be reissued',
                hintText: 'Explain why the issued document cannot be used',
              ),
              validator: (value) {
                final reason = value?.trim() ?? '';
                if (reason.isEmpty) return 'A reissue reason is required.';
                if (reason.length > 1000) {
                  return 'Reissue reason cannot exceed 1,000 characters.';
                }
                return null;
              },
            ),
          ],
        ),
      ),
    ],
  );
}
