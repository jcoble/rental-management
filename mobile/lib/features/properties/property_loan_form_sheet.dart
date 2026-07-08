import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/widgets/tabbed_form_sheet.dart';
import 'property_loans_repository.dart';

Future<bool> showPropertyLoanFormSheet(
  BuildContext context, {
  required int propertyId,
  PropertyLoan? loan,
}) async {
  final saved = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    useRootNavigator: true,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
    ),
    builder: (_) => _PropertyLoanFormSheet(propertyId: propertyId, loan: loan),
  );
  return saved == true;
}

class _PropertyLoanFormSheet extends ConsumerStatefulWidget {
  const _PropertyLoanFormSheet({required this.propertyId, this.loan});

  final int propertyId;
  final PropertyLoan? loan;

  @override
  ConsumerState<_PropertyLoanFormSheet> createState() =>
      _PropertyLoanFormSheetState();
}

class _PropertyLoanFormSheetState
    extends ConsumerState<_PropertyLoanFormSheet> {
  final _lenderCtrl = TextEditingController();
  final _originalCtrl = TextEditingController();
  final _balanceCtrl = TextEditingController();
  final _rateCtrl = TextEditingController();
  final _termCtrl = TextEditingController(text: '360');
  final _startDateCtrl = TextEditingController();
  final _dayDueCtrl = TextEditingController(text: '1');
  final _principalInterestCtrl = TextEditingController();
  final _escrowCtrl = TextEditingController(text: '0');
  final _notesCtrl = TextEditingController();

  bool _escrowCoversTaxes = false;
  bool _escrowCoversInsurance = false;
  bool _saving = false;
  String _status = 'Active';
  String? _error;

  static const _statuses = ['Active', 'PaidOff', 'Closed'];

  @override
  void initState() {
    super.initState();
    final loan = widget.loan;
    if (loan == null) {
      _startDateCtrl.text = _dateInput(DateTime.now());
      return;
    }

    _lenderCtrl.text = loan.lender;
    _originalCtrl.text = _moneyInput(loan.originalAmount);
    _balanceCtrl.text = _moneyInput(loan.currentBalance);
    _rateCtrl.text = _decimalInput(loan.annualInterestRatePct);
    _termCtrl.text = loan.termMonths.toString();
    _startDateCtrl.text = _dateInput(loan.startDate);
    _dayDueCtrl.text = loan.dayOfMonthDue.toString();
    _principalInterestCtrl.text = _moneyInput(loan.monthlyPrincipalInterest);
    _escrowCtrl.text = _moneyInput(loan.monthlyEscrow);
    _escrowCoversTaxes = loan.escrowCoversTaxes;
    _escrowCoversInsurance = loan.escrowCoversInsurance;
    _status = _statuses.contains(loan.status) ? loan.status : 'Active';
    _notesCtrl.text = loan.notes ?? '';
  }

  @override
  void dispose() {
    _lenderCtrl.dispose();
    _originalCtrl.dispose();
    _balanceCtrl.dispose();
    _rateCtrl.dispose();
    _termCtrl.dispose();
    _startDateCtrl.dispose();
    _dayDueCtrl.dispose();
    _principalInterestCtrl.dispose();
    _escrowCtrl.dispose();
    _notesCtrl.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() {
      _saving = true;
      _error = null;
    });

    final repo = ref.read(propertyLoansRepositoryProvider);
    final payload = _payload();

    try {
      final loan = widget.loan;
      if (loan == null) {
        await repo.createLoan(propertyId: widget.propertyId, data: payload);
      } else {
        await repo.updateLoan(loan.id, payload);
      }
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.message);
      rethrow;
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Map<String, dynamic> _payload() {
    final balance = _optionalDouble(_balanceCtrl.text);
    final notes = _notesCtrl.text.trim();
    return {
      'lender': _lenderCtrl.text.trim(),
      'originalAmount': _requiredDouble(_originalCtrl.text),
      'currentBalance': ?balance,
      'annualInterestRatePct': _requiredDouble(_rateCtrl.text),
      'termMonths': int.tryParse(_termCtrl.text.trim()) ?? 360,
      'startDate': _startDateCtrl.text.trim(),
      'dayOfMonthDue': int.tryParse(_dayDueCtrl.text.trim()) ?? 1,
      'monthlyPrincipalInterest': _requiredDouble(_principalInterestCtrl.text),
      'monthlyEscrow': _requiredDouble(_escrowCtrl.text),
      'escrowCoversTaxes': _escrowCoversTaxes,
      'escrowCoversInsurance': _escrowCoversInsurance,
      'status': _status,
      if (notes.isNotEmpty) 'notes': notes,
    };
  }

  @override
  Widget build(BuildContext context) {
    const gap = SizedBox(height: 12);
    return TabbedFormSheet(
      title: widget.loan == null ? 'Add Loan' : 'Edit Loan',
      saveLabel: widget.loan == null ? 'Add Loan' : 'Save Loan',
      saving: _saving,
      error: _error,
      onSave: _save,
      tabs: [
        TabbedFormStepSpec(
          label: 'Basics',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                key: const Key('loan-lender-field'),
                controller: _lenderCtrl,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Lender'),
                validator: (value) => (value == null || value.trim().isEmpty)
                    ? 'Lender is required'
                    : null,
              ),
              gap,
              _MoneyField(
                keyName: 'loan-original-amount-field',
                controller: _originalCtrl,
                label: 'Original amount',
                required: true,
              ),
              gap,
              _MoneyField(
                keyName: 'loan-current-balance-field',
                controller: _balanceCtrl,
                label: 'Current balance',
                helperText: 'Defaults to original amount when blank',
              ),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Terms',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextFormField(
                key: const Key('loan-rate-field'),
                controller: _rateCtrl,
                keyboardType: const TextInputType.numberWithOptions(
                  decimal: true,
                ),
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Interest rate %'),
                validator: (value) =>
                    _numberInRange(value, min: 0, max: 100, label: 'Rate'),
              ),
              gap,
              TextFormField(
                key: const Key('loan-term-field'),
                controller: _termCtrl,
                keyboardType: TextInputType.number,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(labelText: 'Term (months)'),
                validator: (value) =>
                    _intInRange(value, min: 1, max: 1200, label: 'Term'),
              ),
              gap,
              TextFormField(
                key: const Key('loan-start-date-field'),
                controller: _startDateCtrl,
                keyboardType: TextInputType.datetime,
                textInputAction: TextInputAction.next,
                decoration: const InputDecoration(
                  labelText: 'Start date',
                  helperText: 'YYYY-MM-DD',
                ),
                validator: (value) =>
                    _dateValid(value) ? null : 'Enter YYYY-MM-DD',
              ),
              gap,
              TextFormField(
                key: const Key('loan-day-due-field'),
                controller: _dayDueCtrl,
                keyboardType: TextInputType.number,
                decoration: const InputDecoration(labelText: 'Day due'),
                validator: (value) =>
                    _intInRange(value, min: 1, max: 31, label: 'Day due'),
              ),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Payment',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _MoneyField(
                keyName: 'loan-pi-field',
                controller: _principalInterestCtrl,
                label: 'Monthly P&I',
                required: true,
              ),
              gap,
              _MoneyField(
                keyName: 'loan-escrow-field',
                controller: _escrowCtrl,
                label: 'Monthly escrow',
                required: true,
              ),
              gap,
              CheckboxListTile(
                key: const Key('loan-escrow-taxes-field'),
                value: _escrowCoversTaxes,
                onChanged: (value) =>
                    setState(() => _escrowCoversTaxes = value ?? false),
                contentPadding: EdgeInsets.zero,
                title: const Text('Escrow covers taxes'),
              ),
              CheckboxListTile(
                key: const Key('loan-escrow-insurance-field'),
                value: _escrowCoversInsurance,
                onChanged: (value) =>
                    setState(() => _escrowCoversInsurance = value ?? false),
                contentPadding: EdgeInsets.zero,
                title: const Text('Escrow covers insurance'),
              ),
            ],
          ),
        ),
        TabbedFormStepSpec(
          label: 'Status',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              DropdownButtonFormField<String>(
                key: const Key('loan-status-field'),
                initialValue: _status,
                decoration: const InputDecoration(labelText: 'Status'),
                items: _statuses
                    .map(
                      (status) => DropdownMenuItem(
                        value: status,
                        child: Text(_statusLabel(status)),
                      ),
                    )
                    .toList(),
                onChanged: (value) {
                  if (value != null) setState(() => _status = value);
                },
              ),
              gap,
              TextFormField(
                key: const Key('loan-notes-field'),
                controller: _notesCtrl,
                maxLines: 4,
                decoration: const InputDecoration(labelText: 'Notes'),
                validator: (value) => (value != null && value.length > 2000)
                    ? 'Notes must be 2000 characters or fewer'
                    : null,
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _MoneyField extends StatelessWidget {
  const _MoneyField({
    required this.keyName,
    required this.controller,
    required this.label,
    this.required = false,
    this.helperText,
  });

  final String keyName;
  final TextEditingController controller;
  final String label;
  final bool required;
  final String? helperText;

  @override
  Widget build(BuildContext context) {
    return TextFormField(
      key: Key(keyName),
      controller: controller,
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      textInputAction: TextInputAction.next,
      decoration: InputDecoration(labelText: label, helperText: helperText),
      validator: (value) {
        if (!required && (value == null || value.trim().isEmpty)) return null;
        return _numberInRange(value, min: 0, max: 999999999, label: label);
      },
    );
  }
}

double _requiredDouble(String value) => double.tryParse(value.trim()) ?? 0;

double? _optionalDouble(String value) {
  final trimmed = value.trim();
  if (trimmed.isEmpty) return null;
  return double.tryParse(trimmed);
}

String? _numberInRange(
  String? value, {
  required double min,
  required double max,
  required String label,
}) {
  final parsed = double.tryParse(value?.trim() ?? '');
  if (parsed == null) return 'Enter a number';
  if (parsed < min || parsed > max) {
    return '$label must be between ${_rangeLabel(min)} and ${_rangeLabel(max)}';
  }
  return null;
}

String? _intInRange(
  String? value, {
  required int min,
  required int max,
  required String label,
}) {
  final parsed = int.tryParse(value?.trim() ?? '');
  if (parsed == null) return 'Enter a number';
  if (parsed < min || parsed > max) {
    return '$label must be between $min and $max';
  }
  return null;
}

bool _dateValid(String? value) {
  final trimmed = value?.trim() ?? '';
  if (!RegExp(r'^\d{4}-\d{2}-\d{2}$').hasMatch(trimmed)) return false;
  return DateTime.tryParse(trimmed) != null;
}

String _rangeLabel(double value) =>
    value == value.roundToDouble() ? value.toStringAsFixed(0) : '$value';

String _dateInput(DateTime value) {
  final local = value.toLocal();
  return '${local.year.toString().padLeft(4, '0')}-'
      '${local.month.toString().padLeft(2, '0')}-'
      '${local.day.toString().padLeft(2, '0')}';
}

String _moneyInput(double value) => value == value.roundToDouble()
    ? value.toStringAsFixed(0)
    : value.toStringAsFixed(2);

String _decimalInput(double value) => value == value.roundToDouble()
    ? value.toStringAsFixed(0)
    : value.toString();

String _statusLabel(String status) => switch (status) {
  'PaidOff' => 'Paid off',
  _ => status,
};
