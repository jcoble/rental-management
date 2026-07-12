import 'dart:convert';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'lease_prefill.dart';
import 'scan_models.dart';
import 'scan_repository.dart';
import '../places/address_autocomplete_field.dart';

/// Guided, explicitly-stepped, PRE-FILLED "New rental from your lease" flow (mobile mirror of the
/// web /scan/new-rental). One entity per step with a visible "Step N of 4" indicator (AC-1/AC-5),
/// reuses the real create fields pre-filled (AC-2), shows a "from your lease" badge on auto-filled
/// fields (AC-3), and ends on a review screen that creates everything in ONE confirm (AC-4).
class GuidedRentalFlow extends ConsumerStatefulWidget {
  const GuidedRentalFlow({super.key, required this.draftId});
  final int draftId;

  static Future<void> open(BuildContext context, int draftId) {
    return Navigator.of(context).push(
      MaterialPageRoute(builder: (_) => GuidedRentalFlow(draftId: draftId)),
    );
  }

  @override
  ConsumerState<GuidedRentalFlow> createState() => _GuidedRentalFlowState();
}

class _GuidedRentalFlowState extends ConsumerState<GuidedRentalFlow> {
  static const _labels = ['Property', 'Unit', 'Tenant', 'Lease'];
  static const _total = 4;

  int _step = 0;
  bool _loading = true;
  String? _error;
  bool _confirming = false;
  Set<String> _filled = {};

  // Property
  final _propName = TextEditingController();
  final _propAddress = TextEditingController();
  final _propCity = TextEditingController();
  final _propState = TextEditingController();
  final _propZip = TextEditingController();
  // Unit
  final _unitNumber = TextEditingController();
  final _unitBeds = TextEditingController();
  final _unitBaths = TextEditingController();
  // Tenant
  final _tenantFirst = TextEditingController();
  final _tenantLast = TextEditingController();
  // Lease
  final _leaseNumber = TextEditingController();
  final _rent = TextEditingController();
  final _deposit = TextEditingController();
  final _lateFee = TextEditingController();
  final _dueDay = TextEditingController(text: '1');
  DateTime? _start;
  DateTime? _end;

  @override
  void initState() {
    super.initState();
    _poll();
  }

  @override
  void dispose() {
    _propName.dispose();
    _propAddress.dispose();
    _propCity.dispose();
    _propState.dispose();
    _propZip.dispose();
    _unitNumber.dispose();
    _unitBeds.dispose();
    _unitBaths.dispose();
    _tenantFirst.dispose();
    _tenantLast.dispose();
    _leaseNumber.dispose();
    _rent.dispose();
    _deposit.dispose();
    _lateFee.dispose();
    _dueDay.dispose();
    super.dispose();
  }

  // Extraction runs a real LLM (Claude/Sonnet) server-side, which on a slow run
  // can take well over a minute. Poll against an elapsed-time budget rather than
  // a fixed iteration count so a slow-but-valid draft is never abandoned, with a
  // gentle backoff (1.5s ramping to 3s) so we don't hammer the API early while
  // still staying responsive once the draft flips to Reviewing.
  static const _pollBudget = Duration(seconds: 180);
  static const _pollDelayInitial = Duration(milliseconds: 1500);
  static const _pollDelayMax = Duration(seconds: 3);

  Future<void> _poll() async {
    final repo = ref.read(scanRepositoryProvider);
    final deadline = DateTime.now().add(_pollBudget);
    var delay = _pollDelayInitial;
    while (DateTime.now().isBefore(deadline)) {
      late final ScanDraft draft;
      try {
        draft = await repo.getDraft(widget.draftId);
      } catch (_) {
        if (!mounted) return;
        setState(() {
          _loading = false;
          _error = 'Could not load the lease draft. Please try again.';
        });
        return;
      }
      if (!mounted) return;
      if (draft.isReviewing) {
        _seed(draft.fields);
        return;
      }
      if (draft.status == 'Failed' || draft.status == 'Rejected') {
        setState(() {
          _loading = false;
          _error =
              "We couldn't read that lease. Go back and try clearer photos or a PDF.";
        });
        return;
      }
      await Future<void>.delayed(delay);
      // Gentle backoff: each idle pass lengthens the wait by 250ms up to the cap.
      if (delay < _pollDelayMax) {
        final next = delay + const Duration(milliseconds: 250);
        delay = next > _pollDelayMax ? _pollDelayMax : next;
      }
    }
    if (!mounted) return;
    setState(() {
      _loading = false;
      _error = 'Reading the lease took too long. Please try again.';
    });
  }

  void _seed(List<ScanField> fields) {
    final r = LeasePrefill.fromFields(
      fields
          .map(
            (f) => <String, dynamic>{
              'name': f.name,
              'value': f.value,
              'confidence': f.confidence,
            },
          )
          .toList(),
    );
    final v = r.values;
    _filled = r.filled;
    _propName.text = v.propertyName;
    _propAddress.text = v.propertyAddress;
    _propCity.text = v.propertyCity;
    _propState.text = v.propertyState;
    _propZip.text = v.propertyPostalCode;
    _unitNumber.text = v.unitNumber.isEmpty ? '1' : v.unitNumber;
    _unitBeds.text = v.unitBedrooms;
    _unitBaths.text = v.unitBathrooms;
    if (v.tenantName.isNotEmpty) {
      final parts = v.tenantName.trim().split(RegExp(r'\s+'));
      _tenantFirst.text = parts.length > 1
          ? parts.sublist(0, parts.length - 1).join(' ')
          : parts.first;
      _tenantLast.text = parts.length > 1 ? parts.last : '';
    }
    _leaseNumber.text = v.leaseNumber;
    _rent.text = v.monthlyRent;
    _deposit.text = v.securityDeposit;
    _lateFee.text = v.lateFee;
    _dueDay.text = v.rentDueDay.isEmpty ? '1' : v.rentDueDay;
    _start = DateTime.tryParse(v.startDate);
    _end = DateTime.tryParse(v.endDate);
    setState(() => _loading = false);
  }

  bool _badge(String field) => _filled.contains(field);

  Widget _fromLeaseBadge() => Container(
    padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
    decoration: BoxDecoration(
      color: Theme.of(context).colorScheme.secondaryContainer,
      borderRadius: BorderRadius.circular(10),
    ),
    child: Text(
      'from your lease',
      style: Theme.of(context).textTheme.labelSmall,
    ),
  );

  Widget _field(
    String label,
    TextEditingController c, {
    String? extractionKey,
    TextInputType? keyboard,
  }) {
    final filled = extractionKey != null && _badge(extractionKey);
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Text(label, style: Theme.of(context).textTheme.bodySmall),
              if (filled) ...[const SizedBox(width: 6), _fromLeaseBadge()],
            ],
          ),
          const SizedBox(height: 4),
          TextField(
            controller: c,
            keyboardType: keyboard,
            decoration: const InputDecoration(border: OutlineInputBorder()),
          ),
        ],
      ),
    );
  }

  String _overridesJson() {
    final o = <String, dynamic>{};
    // This flow always creates from the document (mobile v1: no existing-link picker — parity follow-up).
    o['propertyId'] = null;
    if (_propName.text.trim().isNotEmpty)
      o['propertyName'] = _propName.text.trim();
    if (_propAddress.text.trim().isNotEmpty)
      o['propertyAddress'] = _propAddress.text.trim();
    if (_propCity.text.trim().isNotEmpty)
      o['propertyCity'] = _propCity.text.trim();
    if (_propState.text.trim().isNotEmpty)
      o['propertyState'] = _propState.text.trim();
    if (_propZip.text.trim().isNotEmpty)
      o['propertyPostalCode'] = _propZip.text.trim();
    o['unitId'] = null;
    if (_unitNumber.text.trim().isNotEmpty)
      o['unitNumber'] = _unitNumber.text.trim();
    if (_unitBeds.text.trim().isNotEmpty)
      o['unitBedrooms'] = num.tryParse(_unitBeds.text.trim());
    if (_unitBaths.text.trim().isNotEmpty)
      o['unitBathrooms'] = num.tryParse(_unitBaths.text.trim());
    final name = '${_tenantFirst.text.trim()} ${_tenantLast.text.trim()}'
        .trim();
    if (name.isNotEmpty) o['tenantName'] = name;
    o['leaseNumber'] = _leaseNumber.text.trim();
    if (_start != null) o['startDate'] = _iso(_start!);
    if (_end != null) o['endDate'] = _iso(_end!);
    if (_rent.text.trim().isNotEmpty)
      o['monthlyRent'] = num.tryParse(_rent.text.trim());
    if (_deposit.text.trim().isNotEmpty)
      o['securityDeposit'] = num.tryParse(_deposit.text.trim());
    if (_lateFee.text.trim().isNotEmpty)
      o['lateFee'] = num.tryParse(_lateFee.text.trim());
    if (_dueDay.text.trim().isNotEmpty)
      o['rentDueDay'] = int.tryParse(_dueDay.text.trim());
    return jsonEncode(o);
  }

  String _iso(DateTime d) =>
      '${d.year.toString().padLeft(4, '0')}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';

  bool _validateStep() {
    String? err;
    if (_step == 0) {
      if (_propName.text.trim().isEmpty && _propAddress.text.trim().isEmpty) {
        err = 'Enter a property name or address.';
      }
    } else if (_step == 1) {
      if (_unitNumber.text.trim().isEmpty) err = 'Enter a unit number.';
    } else if (_step == 2) {
      if (_tenantFirst.text.trim().isEmpty)
        err = "Enter the tenant's first name.";
    } else if (_step == 3) {
      if (_start == null || _end == null) {
        err = 'Pick the lease start and end dates.';
      } else if ((num.tryParse(_rent.text.trim()) ?? 0) <= 0) {
        err = 'Enter the monthly rent.';
      }
    }
    if (err != null) {
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(err)));
      return false;
    }
    return true;
  }

  Future<void> _confirm() async {
    setState(() => _confirming = true);
    try {
      final res = await ref
          .read(scanRepositoryProvider)
          .confirm(
            widget.draftId,
            jsonDecode(_overridesJson()) as Map<String, dynamic>,
          );
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(const SnackBar(content: Text('Your rental is set up.')));
      Navigator.of(context).pop(res?['agreementId']);
    } catch (_) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text(
              'Could not create the rental. Check the details and try again.',
            ),
          ),
        );
      }
    } finally {
      if (mounted) setState(() => _confirming = false);
    }
  }

  Future<void> _pickDate(bool start) async {
    final picked = await showDatePicker(
      context: context,
      initialDate: (start ? _start : _end) ?? DateTime.now(),
      firstDate: DateTime(2000),
      lastDate: DateTime(2100),
    );
    if (picked != null) setState(() => start ? _start = picked : _end = picked);
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: Text(
          _step < _total
              ? 'Step ${_step + 1} of $_total · ${_labels[_step]}'
              : 'Review & confirm',
        ),
      ),
      body: _loading
          ? const Center(
              child: Padding(
                padding: EdgeInsets.all(24),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    CircularProgressIndicator(),
                    SizedBox(height: 12),
                    Text('Reading your lease…'),
                  ],
                ),
              ),
            )
          : _error != null
          ? Center(
              child: Padding(
                padding: const EdgeInsets.all(24),
                child: Text(_error!),
              ),
            )
          : Column(
              children: [
                LinearProgressIndicator(value: (_step + 1) / (_total + 1)),
                Expanded(
                  child: SingleChildScrollView(
                    padding: const EdgeInsets.all(16),
                    child: _stepBody(),
                  ),
                ),
                _bottomBar(),
              ],
            ),
    );
  }

  /// Persistent Back / Next (or Confirm) action bar pinned to the bottom of the
  /// stepped form.
  ///
  /// Why `OverflowBar` and not a plain `Row` + `Spacer`: this bar is the last
  /// child of a `Column` that also has an `Expanded` child. Flex sizes its
  /// non-flex children with an unbounded main-axis extent to measure them, so
  /// the bar is laid out with an unbounded height. A horizontal `Row` in that
  /// context hands its non-flex children (a default `FilledButton`, whose
  /// `ButtonStyle.maximumSize` is `Size.infinite`) an unbounded width, tripping
  /// `RenderConstrainedBox`'s "BoxConstraints forces an infinite width" assert.
  /// That assert aborts layout of the entire body subtree — the symptom was a
  /// completely blank Step 1. `OverflowBar` lays its children out without
  /// forcing them to an unbounded width, so the buttons size to their content.
  Widget _bottomBar() {
    return SafeArea(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: OverflowBar(
          alignment: MainAxisAlignment.spaceBetween,
          overflowAlignment: OverflowBarAlignment.end,
          children: [
            TextButton(
              onPressed: _step == 0 ? null : () => setState(() => _step -= 1),
              child: const Text('Back'),
            ),
            if (_step < _total)
              FilledButton(
                onPressed: () {
                  if (_validateStep()) setState(() => _step += 1);
                },
                child: const Text('Next'),
              )
            else
              FilledButton(
                onPressed: _confirming ? null : _confirm,
                child: Text(_confirming ? 'Creating…' : 'Confirm & create'),
              ),
          ],
        ),
      ),
    );
  }

  Widget _stepBody() {
    switch (_step) {
      case 0:
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            _field('Property name', _propName, extractionKey: 'property_name'),
            // Places-backed street address (AC: Google Places on mobile via existing proxy)
            Row(
              children: [
                Text('Address', style: Theme.of(context).textTheme.bodySmall),
                if (_badge('property_address')) ...[
                  const SizedBox(width: 6),
                  _fromLeaseBadge(),
                ],
              ],
            ),
            const SizedBox(height: 4),
            AddressAutocompleteField(
              controller: _propAddress,
              onResolved: (a) {
                if (a.city.isNotEmpty) _propCity.text = a.city;
                if (a.state.isNotEmpty) _propState.text = a.state;
                if (a.zip.isNotEmpty) _propZip.text = a.zip;
              },
            ),
            const SizedBox(height: 12),
            _field('City', _propCity, extractionKey: 'property_city'),
            _field('State', _propState, extractionKey: 'property_state'),
            _field('ZIP', _propZip, extractionKey: 'property_postal_code'),
          ],
        );
      case 1:
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            _field('Unit number', _unitNumber, extractionKey: 'unit_number'),
            _field(
              'Bedrooms',
              _unitBeds,
              extractionKey: 'unit_bedrooms',
              keyboard: TextInputType.number,
            ),
            _field(
              'Bathrooms',
              _unitBaths,
              extractionKey: 'unit_bathrooms',
              keyboard: TextInputType.number,
            ),
          ],
        );
      case 2:
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            _field('First name', _tenantFirst, extractionKey: 'tenant_name'),
            _field('Last name', _tenantLast, extractionKey: 'tenant_name'),
          ],
        );
      case 3:
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            _field('Lease number', _leaseNumber, extractionKey: 'lease_number'),
            ListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Start date'),
              subtitle: Text(_start == null ? 'Pick a date' : _iso(_start!)),
              trailing: const Icon(Icons.calendar_today_outlined),
              onTap: () => _pickDate(true),
            ),
            ListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('End date'),
              subtitle: Text(_end == null ? 'Pick a date' : _iso(_end!)),
              trailing: const Icon(Icons.calendar_today_outlined),
              onTap: () => _pickDate(false),
            ),
            _field(
              'Monthly rent',
              _rent,
              extractionKey: 'monthly_rent',
              keyboard: TextInputType.number,
            ),
            _field(
              'Security deposit',
              _deposit,
              extractionKey: 'security_deposit',
              keyboard: TextInputType.number,
            ),
            _field(
              'Late fee',
              _lateFee,
              extractionKey: 'late_fee',
              keyboard: TextInputType.number,
            ),
            _field(
              'Rent due day',
              _dueDay,
              extractionKey: 'rent_due_day',
              keyboard: TextInputType.number,
            ),
          ],
        );
      default:
        // AC-4: review before save
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text(
              "Here's what I'll add",
              style: TextStyle(fontWeight: FontWeight.w600, fontSize: 16),
            ),
            const SizedBox(height: 8),
            Card(
              child: Padding(
                padding: const EdgeInsets.all(12),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Property: ${_propName.text.isNotEmpty ? _propName.text : _propAddress.text} — ${[_propAddress.text, _propCity.text, _propState.text].where((s) => s.isNotEmpty).join(', ')}',
                    ),
                    const SizedBox(height: 4),
                    Text('Unit: ${_unitNumber.text}'),
                    const SizedBox(height: 4),
                    Text(
                      'Tenant: ${'${_tenantFirst.text} ${_tenantLast.text}'.trim()}',
                    ),
                    const SizedBox(height: 4),
                    Text(
                      'Lease: \$${_rent.text}/mo, ${_start != null ? _iso(_start!) : '—'} – ${_end != null ? _iso(_end!) : '—'}',
                    ),
                  ],
                ),
              ),
            ),
            const SizedBox(height: 8),
            Text(
              'Nothing is saved until you tap Confirm.',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ],
        );
    }
  }
}
