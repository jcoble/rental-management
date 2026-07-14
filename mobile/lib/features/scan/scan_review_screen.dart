import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/models/models.dart';
import '../home/mobile_domain_navigation.dart';
import '../places/address_autocomplete_field.dart';
import '../properties/properties_repository.dart';
import '../tenants/tenants_repository.dart';
import '../units/unit_command_center_screen.dart';
import 'scan_models.dart';
import 'scan_repository.dart';

// ---------------------------------------------------------------------------
// Providers
// ---------------------------------------------------------------------------

/// Holds the draft being reviewed.
final _draftProvider = FutureProvider.autoDispose.family<ScanDraft, int>((
  ref,
  id,
) async {
  return ref.read(scanRepositoryProvider).getDraft(id);
});

/// Holds the authed image bytes for the document preview.
final _imageProvider = FutureProvider.autoDispose.family<Uint8List, int>((
  ref,
  id,
) async {
  // Keep bytes alive while the screen is open so rebuilds (e.g. field edits)
  // do not re-download the image. The link is released after a short delay
  // once the provider is disposed (screen popped), avoiding unbounded growth.
  final link = ref.keepAlive();
  ref.onDispose(() {
    Future<void>.delayed(const Duration(seconds: 10), link.close);
  });
  return ref.read(scanRepositoryProvider).downloadFile(id);
});

typedef _TenantAccountQuery = ({String search, int skip});

/// Holds one server-filtered page of canonical tenant-account options.
final _tenantAccountOptionsProvider = FutureProvider.autoDispose
    .family<TenantAccountOptionPage, _TenantAccountQuery>((ref, query) async {
      ref.keepAlive();
      return ref
          .read(scanRepositoryProvider)
          .listTenantAccountOptions(search: query.search, skip: query.skip);
    });

/// Resolves a contextual account that is not present in the current server page.
/// A stale/not-found context is intentionally treated as no selection.
final _tenantAccountOptionProvider = FutureProvider.autoDispose
    .family<TenantAccountOption?, int>((ref, tenantAccountId) async {
      try {
        return await ref
            .read(scanRepositoryProvider)
            .getTenantAccountOption(tenantAccountId);
      } on ApiException catch (error) {
        if (error.statusCode == 404) return null;
        rethrow;
      }
    });

/// Properties for the in-portfolio property picker (Lease drafts only).
final _propertiesProvider = FutureProvider.autoDispose<List<Property>>((
  ref,
) async {
  ref.keepAlive();
  return ref.read(propertiesRepositoryProvider).listProperties();
});

/// Units for the selected property, scoping the unit picker (Lease drafts only).
final _unitsForPropertyProvider = FutureProvider.autoDispose
    .family<List<Unit>, int>((ref, propertyId) async {
      ref.keepAlive();
      return ref.read(propertiesRepositoryProvider).listUnits(propertyId);
    });

/// Tenants for the optional tenant picker (Lease drafts only).
final _tenantsProvider = FutureProvider.autoDispose<List<Tenant>>((ref) async {
  ref.keepAlive();
  return ref.read(tenantsRepositoryProvider).listTenants();
});

// ---------------------------------------------------------------------------
// Field groups (mirrors web review page)
// ---------------------------------------------------------------------------

const _fieldGroups = <({String label, List<String> fields})>[
  (
    label: 'Vendor',
    fields: [
      'vendor_name',
      'vendor_address',
      'vendor_phone',
      'vendor_website',
      'vendor_tax_id',
      'receipt_number',
    ],
  ),
  (
    label: 'Amounts',
    fields: [
      'transaction_date',
      'subtotal',
      'tax',
      'tax_rate',
      'tip',
      'discount',
      'shipping',
      'total',
      'payment_method',
      'card_last4',
      'due_date',
    ],
  ),
  (label: 'Details', fields: ['document_kind', 'category', 'notes']),
];

// Lease-draft field group (target == 'LeaseAgreement'). The property/unit/tenant are
// chosen with pickers below, so only the lease *terms* live here.
const _leaseFieldOrder = <String>[
  'lease_number',
  'start_date',
  'end_date',
  'monthly_rent',
  'security_deposit',
  'late_fee',
  'rent_due_day',
];

// Applicant-draft field group (target == 'Application'). The applicant scalar
// fields are shown as editable inputs in this display order (mirrors the web
// review page's APPLICATION_FIELDS). property_id/unit_id are NOT shown — they're
// the in-portfolio link carried through to confirm, validated server-side.
const _applicationFieldOrder = <String>[
  'first_name',
  'last_name',
  'email',
  'phone',
  'date_of_birth',
  'current_address',
  'employer',
  'monthly_income',
  'applying_for',
  'desired_move_in_date',
  'id_last4',
  'co_signer_name',
];

// Loan-draft field group (target == 'Loan'). The property is supplied by the
// property detail that launched the scan, so property_id/property_address are
// not edited here.
const _loanFieldOrder = <String>[
  'lender',
  'original_amount',
  'current_balance',
  'annual_interest_rate_pct',
  'term_months',
  'start_date',
  'day_of_month_due',
  'monthly_principal_interest',
  'monthly_escrow',
  'escrow_covers_taxes',
  'escrow_covers_insurance',
  'notes',
];

const _knownScalarFields = {
  'vendor_name',
  'vendor_address',
  'vendor_phone',
  'vendor_website',
  'vendor_tax_id',
  'receipt_number',
  'transaction_date',
  'subtotal',
  'tax',
  'tax_rate',
  'tip',
  'discount',
  'shipping',
  'total',
  'payment_method',
  'card_last4',
  'due_date',
  'document_kind',
  'category',
  'notes',
  'lender',
  'original_amount',
  'current_balance',
  'annual_interest_rate_pct',
  'term_months',
  'day_of_month_due',
  'monthly_principal_interest',
  'monthly_escrow',
  'escrow_covers_taxes',
  'escrow_covers_insurance',
  'property_id',
  'property_address',
};

// Money fields use a decimal number keypad; date fields open a date picker so a
// non-technical landlord never has to fight the on-screen keyboard.
const _moneyFields = {
  'amount',
  'total',
  'subtotal',
  'tax',
  'tip',
  'discount',
  'shipping',
  // Lease terms
  'monthly_rent',
  'security_deposit',
  'late_fee',
  // Applicant fields
  'monthly_income',
  // Loan terms
  'original_amount',
  'current_balance',
  'annual_interest_rate_pct',
  'monthly_principal_interest',
  'monthly_escrow',
};

const _dateFields = {
  'transaction_date',
  'due_date',
  // Lease terms
  'start_date',
  'end_date',
  // Applicant fields
  'date_of_birth',
  'desired_move_in_date',
};

// Whole-number fields use an integer keypad (e.g. the rent due day-of-month).
const _intFields = {'rent_due_day', 'term_months', 'day_of_month_due'};

// Friendly label overrides for keys where plain Title Case reads awkwardly.
const _labelOverrides = <String, String>{
  'vendor_name': 'Vendor',
  'vendor_tax_id': 'Vendor tax ID',
  'transaction_date': 'Transaction date',
  'due_date': 'Due date',
  'receipt_number': 'Receipt number',
  'card_last4': 'Card last 4',
  'tax_rate': 'Tax rate',
  'payment_method': 'Payment method',
  'document_kind': 'Document type',
  'total': 'Total',
  'subtotal': 'Subtotal',
  'tax': 'Tax',
  // Lease terms
  'lease_number': 'Lease number',
  'start_date': 'Start date',
  'end_date': 'End date',
  'monthly_rent': 'Monthly rent',
  'security_deposit': 'Security deposit',
  'late_fee': 'Late fee',
  'rent_due_day': 'Rent due day (of month)',
  'tenant_name': 'Tenant',
  // Applicant fields (mirror the web review page's labels)
  'first_name': 'First name',
  'last_name': 'Last name',
  'date_of_birth': 'Date of birth',
  'current_address': 'Current address',
  'monthly_income': 'Monthly income',
  'applying_for': 'Applying for',
  'desired_move_in_date': 'Desired move-in',
  'id_last4': 'ID last 4',
  'co_signer_name': 'Co-signer',
  // Loan terms
  'lender': 'Lender',
  'original_amount': 'Original amount',
  'current_balance': 'Current balance',
  'annual_interest_rate_pct': 'Interest rate %',
  'term_months': 'Term (months)',
  'day_of_month_due': 'Day due',
  'monthly_principal_interest': 'Monthly P&I',
  'monthly_escrow': 'Monthly escrow',
  'escrow_covers_taxes': 'Escrow covers taxes',
  'escrow_covers_insurance': 'Escrow covers insurance',
};

/// Turns a raw snake_case field name into a human-readable label, e.g.
/// `transaction_date` -> "Transaction date", `vendor_name` -> "Vendor".
String _prettifyLabel(String name) {
  final override = _labelOverrides[name];
  if (override != null) return override;
  final words = name.split('_').where((w) => w.isNotEmpty).toList();
  if (words.isEmpty) return name;
  return words
      .asMap()
      .entries
      .map((e) {
        final w = e.value;
        final lower = w.toLowerCase();
        // Only the first word is capitalised (sentence case) so labels read
        // naturally; the rest stay lower-case unless they're a single letter.
        if (e.key == 0) {
          return lower[0].toUpperCase() + lower.substring(1);
        }
        return lower;
      })
      .join(' ');
}

// ScheduleECategory values (mirrors RentalCommand.Core.Enums.ScheduleECategory)
const _scheduleECategories = [
  'Advertising',
  'AutoTravel',
  'CleaningMaintenance',
  'Commissions',
  'Insurance',
  'LegalProfessional',
  'ManagementFees',
  'MortgageInterest',
  'Repairs',
  'Supplies',
  'Taxes',
  'Utilities',
  'Depreciation',
  'Other',
];

// ---------------------------------------------------------------------------
// Overrides map builder (mirrors web's buildOverridesJson)
// ---------------------------------------------------------------------------

/// Key mapping: certain snake_case names get camelCase equivalents to match
/// the legacy override keys the API's ConfirmAndCreateAsync accepts.
const _keyMap = <String, String>{
  'vendor_name': 'vendorName',
  'amount': 'amount',
  'transaction_date': 'transactionDate',
  'category': 'category',
  'notes': 'notes',
};

Map<String, dynamic> buildOverridesMap({
  required Map<String, String> editedFields,
  required bool isPayment,
  required bool isWorkOrder,
  required bool isLease,
  required bool isApplication,
  required bool isLoan,
  required bool isPaid,
  required int? selectedTenantAccountId,
  // Extracted, in-portfolio-validated property/unit link for an Application
  // draft, carried through so the applicant can be filed under the unit they
  // applied for (the server re-validates both in-portfolio).
  required int? applicationPropertyId,
  required int? applicationUnitId,
  required bool createNewProperty,
  required int? selectedPropertyId,
  required int? selectedUnitId,
  required int? selectedTenantId,
  required int? loanPropertyId,
  // Create-new-property fields (only used when createNewProperty is true).
  String? newPropertyName,
  String? newPropertyAddress,
  String? newPropertyCity,
}) {
  final overrides = <String, dynamic>{};
  for (final entry in editedFields.entries) {
    if (entry.key == 'line_items') continue;
    final key = _keyMap[entry.key] ?? entry.key;
    overrides[key] = entry.value;
  }
  if (isLease) {
    if (createNewProperty) {
      // Empty-portfolio bootstrap (C3): send no propertyId/unitId so the server
      // resolves-or-CREATES the property from the (possibly edited) extracted
      // leased-premises address, then creates the unit from the unit number.
      // propertyId=0 forces "create new" even if the model had guessed an id.
      overrides['propertyId'] = 0;
      final name = newPropertyName?.trim() ?? '';
      final address = newPropertyAddress?.trim() ?? '';
      final city = newPropertyCity?.trim() ?? '';
      if (name.isNotEmpty) overrides['propertyName'] = name;
      if (address.isNotEmpty) overrides['propertyAddress'] = address;
      if (city.isNotEmpty) overrides['propertyCity'] = city;
    } else {
      // Link to existing property + unit (both required in this mode). The API
      // also accepts the edited lease terms passed through above.
      if (selectedPropertyId != null) {
        overrides['propertyId'] = selectedPropertyId;
      }
      if (selectedUnitId != null) overrides['unitId'] = selectedUnitId;
    }
    // Tenant: a chosen id links; null lets the server match/create from
    // tenant_name (same in both property modes).
    if (selectedTenantId != null) overrides['tenantId'] = selectedTenantId;
  } else if (isApplication) {
    // The edited applicant scalar fields (first_name, last_name, email, …) are
    // already in `overrides` as snake_case keys, which the server's
    // ApplyApplicationOverrides accepts directly. Carry through the extracted
    // property/unit link (camelCase) so the applicant is filed under the unit
    // they applied for; the server re-validates both in-portfolio.
    if (applicationPropertyId != null) {
      overrides['propertyId'] = applicationPropertyId;
    }
    if (applicationUnitId != null) overrides['unitId'] = applicationUnitId;
  } else if (isPayment) {
    overrides['tenantAccountId'] = selectedTenantAccountId;
  } else if (isLoan) {
    if (loanPropertyId != null) overrides['propertyId'] = loanPropertyId;
  } else if (!isWorkOrder) {
    overrides['is_paid'] = isPaid;
  }
  return overrides;
}

// ---------------------------------------------------------------------------
// ScanReviewScreen
// ---------------------------------------------------------------------------

class ScanReviewScreen extends ConsumerStatefulWidget {
  const ScanReviewScreen({
    super.key,
    required this.draftId,
    this.loanPropertyId,
  });

  final int draftId;
  final int? loanPropertyId;

  @override
  ConsumerState<ScanReviewScreen> createState() => _ScanReviewScreenState();
}

class _ScanReviewScreenState extends ConsumerState<ScanReviewScreen> {
  // Editable field values, keyed by field name.
  final Map<String, String> _editedFields = {};
  bool _fieldsInitialized = false;

  // Paid/unpaid toggle (Expense only).
  bool _isPaid = true;
  bool _isPaidInitialized = false;

  // Selected canonical tenant account (Payment only).
  int? _selectedTenantAccountId;

  // Lease-draft selections. In LINK mode property + unit are required; tenant is
  // optional (null = create/match from the extracted tenant_name). Seeded once
  // from any in-portfolio ids the backend already resolved.
  int? _selectedPropertyId;
  int? _selectedUnitId;
  int? _selectedTenantId;
  bool _leaseSelectionsInitialized = false;

  // CREATE-NEW-PROPERTY mode (C3): the flagship empty-portfolio bootstrap. When
  // true, the property + unit are created from the scanned document instead of
  // being linked to existing rows. Defaulted from the server's import proposal
  // (create/select → true) so a brand-new landlord with zero properties can
  // actually confirm. The address fields live in _editedFields under
  // property_name/property_address/property_city.
  bool _createNewProperty = false;

  // Loan scans launched from a property arrive with this preselected. Reopened
  // drafts choose here so a pending mortgage scan never dead-ends after restart.
  int? _selectedLoanPropertyId;

  // Polling timer — used while draft is Pending.
  Timer? _pollTimer;

  // Action states
  bool _confirming = false;
  bool _rejecting = false;

  @override
  void initState() {
    super.initState();
    _selectedLoanPropertyId = widget.loanPropertyId;
    _startPollingIfNeeded();
  }

  @override
  void dispose() {
    _pollTimer?.cancel();
    super.dispose();
  }

  void _startPollingIfNeeded() {
    // After the provider loads we check status; the timer is set up in _onDraftLoaded.
  }

  void _onDraftLoaded(ScanDraft draft) {
    // Initialize editable field values once the extracted fields actually
    // arrive. The screen first loads while the draft is still Processing (no
    // fields yet); seeding then would lock in empty values and never refresh
    // when extraction completes — leaving confirm guards that read
    // `_editedFields` (e.g. the Application first/last-name guard) permanently
    // unsatisfied. Wait for non-empty scalar fields before seeding.
    if (!_fieldsInitialized && draft.scalarFields.isNotEmpty) {
      _fieldsInitialized = true;
      for (final f in draft.scalarFields) {
        _editedFields[f.name] = f.value;
      }
    }

    // Initialize isPaid from document_kind once.
    if (!_isPaidInitialized) {
      _isPaidInitialized = true;
      final kindField = draft.fields
          .where((f) => f.name == 'document_kind')
          .firstOrNull;
      _isPaid = _defaultIsPaidFromKind(kindField?.value);
    }

    // Seed lease pickers once from any ids the backend grounding resolved (it
    // only emits property_id/unit_id/tenant_id that are in-portfolio). The
    // pickers still validate against the loaded lists, so a stale id just shows
    // as "unselected" until the user chooses.
    if (draft.isLease &&
        !_leaseSelectionsInitialized &&
        draft.fields.isNotEmpty) {
      _leaseSelectionsInitialized = true;
      _selectedPropertyId = _extractedInt(draft, 'property_id');
      _selectedUnitId = _extractedInt(draft, 'unit_id');
      _selectedTenantId = _extractedInt(draft, 'tenant_id');

      // Seed the create-new-property address fields from the extracted
      // leased-premises so the create-mode form is pre-filled (C3). These
      // aren't part of the lease-terms group, so seed them explicitly.
      for (final key in const [
        'property_name',
        'property_address',
        'property_city',
      ]) {
        _editedFields[key] ??= _extractedValue(draft, key);
      }

      // Default the property mode from the server's import proposal: when it
      // would CREATE (no existing match) or needs the reviewer to choose
      // (SELECT, e.g. empty portfolio), start in create-new mode so a landlord
      // with zero properties can confirm. A LINK proposal starts in link mode
      // with the matched property pre-selected.
      final proposal = draft.leaseProposal;
      if (proposal != null) {
        _createNewProperty =
            proposal.property.isCreate || proposal.property.isSelect;
        if (proposal.property.isLink && _selectedPropertyId == null) {
          _selectedPropertyId = proposal.property.existingId;
        }
      } else {
        // No proposal (older server) — fall back to create mode only when we
        // have no grounded property id to link.
        _createNewProperty = _selectedPropertyId == null;
      }
    }

    // Start/stop polling based on status. The worker flips the draft
    // Pending → Processing → Reviewing, so we must keep polling through BOTH
    // Pending and Processing to catch the final Reviewing state.
    if (draft.status == 'Pending' || draft.status == 'Processing') {
      _ensurePolling(draft.id);
    } else {
      _pollTimer?.cancel();
      _pollTimer = null;
    }
  }

  void _ensurePolling(int id) {
    if (_pollTimer != null && _pollTimer!.isActive) return;
    _pollTimer = Timer.periodic(const Duration(milliseconds: 1500), (_) {
      if (!mounted) {
        _pollTimer?.cancel();
        return;
      }
      // Invalidate the provider to trigger a re-fetch.
      ref.invalidate(_draftProvider(id));
    });
  }

  bool _defaultIsPaidFromKind(String? kind) {
    if (kind == null) return true;
    return !['Bill', 'Invoice', 'UtilityBill', 'PropertyTax'].contains(kind);
  }

  /// Reads an extracted scalar field as a positive int, or null when absent /
  /// unparsable / non-positive. Used to seed the lease pickers.
  int? _extractedInt(ScanDraft draft, String name) {
    final field = draft.fields.where((f) => f.name == name).firstOrNull;
    final parsed = int.tryParse(field?.value.trim() ?? '');
    return (parsed != null && parsed > 0) ? parsed : null;
  }

  /// Reads an extracted scalar field's raw value, or '' when absent. Used to
  /// seed the create-new-property address fields.
  String _extractedValue(ScanDraft draft, String name) {
    final field = draft.fields.where((f) => f.name == name).firstOrNull;
    return field?.value ?? '';
  }

  Future<void> _confirm(ScanDraft draft) async {
    if (_confirming) return;
    setState(() => _confirming = true);
    try {
      final overrides = buildOverridesMap(
        editedFields: _editedFields,
        isPayment: draft.isPayment,
        isWorkOrder: draft.isWorkOrder,
        isLease: draft.isLease,
        isApplication: draft.isApplication,
        isLoan: draft.isLoan,
        isPaid: _isPaid,
        selectedTenantAccountId: _selectedTenantAccountId,
        applicationPropertyId: _extractedInt(draft, 'property_id'),
        applicationUnitId: _extractedInt(draft, 'unit_id'),
        createNewProperty: _createNewProperty,
        selectedPropertyId: _selectedPropertyId,
        selectedUnitId: _selectedUnitId,
        selectedTenantId: _selectedTenantId,
        loanPropertyId: _selectedLoanPropertyId,
        newPropertyName: _editedFields['property_name'],
        newPropertyAddress: _editedFields['property_address'],
        newPropertyCity: _editedFields['property_city'],
      );
      final result = await ref
          .read(scanRepositoryProvider)
          .confirm(draft.id, overrides);
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            draft.isPayment
                ? 'Payment recorded!'
                : draft.isWorkOrder
                ? 'Work order created!'
                : draft.isLease
                ? 'Lease agreement imported!'
                : draft.isApplication
                ? 'Application created!'
                : draft.isLoan
                ? 'Loan created!'
                : 'Expense created!',
          ),
          backgroundColor: Colors.green,
        ),
      );

      // For an imported agreement, jump to its Unit relationship so the
      // landlord can review the canonical agreement history. Replace this
      // review screen so Back returns to the scan list.
      final agreementId = (result?['agreementId'] as num?)?.toInt();
      final unitId = (result?['unitId'] as num?)?.toInt();
      if (draft.isLease && agreementId != null && unitId != null) {
        final shellNavigator = mobileShellNavigatorOf(context);
        if (shellNavigator != null) {
          shellNavigator.openTab(
            MobileShellTabId.rentals,
            destination: MobileDestinationId.units,
            detailBuilder: (_) => UnitCommandCenterLoaderScreen(
              unitId: unitId,
              initialTab: UnitCommandCenterTab.tenantLease,
              initialView: UnitCommandCenterView.agreements,
            ),
          );
          revealMobileShellIfDetached(context);
          return;
        }

        Navigator.of(context).pushReplacement(
          MaterialPageRoute<void>(
            builder: (_) => UnitCommandCenterLoaderScreen(
              unitId: unitId,
              initialTab: UnitCommandCenterTab.tenantLease,
              initialView: UnitCommandCenterView.agreements,
            ),
          ),
        );
        return;
      }
      Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (!mounted) return;
      _showError(e.message);
    } catch (_) {
      if (!mounted) return;
      _showError('Something went wrong. Please try again.');
    } finally {
      if (mounted) setState(() => _confirming = false);
    }
  }

  Future<void> _reject(ScanDraft draft) async {
    final reason = await _promptReason(context);
    if (!mounted) return;
    if (reason == null) return; // user cancelled

    setState(() => _rejecting = true);
    try {
      await ref
          .read(scanRepositoryProvider)
          .reject(draft.id, reason: reason.isEmpty ? null : reason);
      if (!mounted) return;
      ScaffoldMessenger.of(
        context,
      ).showSnackBar(const SnackBar(content: Text('Scan rejected.')));
      Navigator.of(context).pop();
    } on ApiException catch (e) {
      if (!mounted) return;
      _showError(e.message);
    } finally {
      if (mounted) setState(() => _rejecting = false);
    }
  }

  void _showError(String message) {
    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(
        content: Text(message),
        backgroundColor: Theme.of(context).colorScheme.error,
      ),
    );
  }

  static Future<String?> _promptReason(BuildContext context) {
    final controller = TextEditingController();
    return showDialog<String>(
      context: context,
      builder: (ctx) => AlertDialog(
        title: const Text('Reject scan'),
        content: TextField(
          controller: controller,
          decoration: const InputDecoration(hintText: 'Reason (optional)'),
          autofocus: true,
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(controller.text),
            child: const Text('Reject'),
          ),
        ],
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final draftAsync = ref.watch(_draftProvider(widget.draftId));

    // React to new draft data without scheduling a redundant setState each build.
    // _onDraftLoaded's internal guards (_fieldsInitialized, _isPaidInitialized)
    // ensure idempotent field init; polling start/stop is also safe to call again.
    ref.listen<AsyncValue<ScanDraft>>(_draftProvider(widget.draftId), (
      _,
      next,
    ) {
      next.whenData((draft) {
        if (mounted) setState(() => _onDraftLoaded(draft));
      });
    });

    return draftAsync.when(
      loading: () => Scaffold(
        appBar: AppBar(title: Text('Review Scan #${widget.draftId}')),
        body: const Center(child: CircularProgressIndicator()),
      ),
      error: (e, _) => Scaffold(
        appBar: AppBar(title: Text('Review Scan #${widget.draftId}')),
        body: Center(
          child: Text(
            e is ApiException ? e.message : e.toString(),
            textAlign: TextAlign.center,
          ),
        ),
      ),
      data: (draft) {
        return _ReviewBody(
          draft: draft,
          editedFields: _editedFields,
          onFieldChanged: (name, value) =>
              setState(() => _editedFields[name] = value),
          isPaid: _isPaid,
          onIsPaidChanged: (v) => setState(() => _isPaid = v),
          selectedTenantAccountId: _selectedTenantAccountId,
          onTenantAccountSelected: (id) =>
              setState(() => _selectedTenantAccountId = id),
          createNewProperty: _createNewProperty,
          onCreateNewPropertyChanged: (v) => setState(() {
            _createNewProperty = v;
            // Switching to link mode drops any half-entered create state's unit;
            // switching to create mode drops the linked unit too (it's recreated
            // from the document). Either way clear the unit selection.
            _selectedUnitId = null;
          }),
          selectedPropertyId: _selectedPropertyId,
          onPropertySelected: (id) => setState(() {
            _selectedPropertyId = id;
            // Changing the property invalidates any unit chosen under the old
            // one, so clear it (the unit picker rescopes to the new property).
            _selectedUnitId = null;
          }),
          selectedUnitId: _selectedUnitId,
          onUnitSelected: (id) => setState(() => _selectedUnitId = id),
          selectedTenantId: _selectedTenantId,
          onTenantSelected: (id) => setState(() => _selectedTenantId = id),
          loanPropertyId: _selectedLoanPropertyId,
          onLoanPropertySelected: (id) =>
              setState(() => _selectedLoanPropertyId = id),
          confirming: _confirming,
          rejecting: _rejecting,
          onConfirm: () => _confirm(draft),
          onReject: () => _reject(draft),
        );
      },
    );
  }
}

// ---------------------------------------------------------------------------
// _ReviewBody
// ---------------------------------------------------------------------------

class _ReviewBody extends ConsumerWidget {
  const _ReviewBody({
    required this.draft,
    required this.editedFields,
    required this.onFieldChanged,
    required this.isPaid,
    required this.onIsPaidChanged,
    required this.selectedTenantAccountId,
    required this.onTenantAccountSelected,
    required this.createNewProperty,
    required this.onCreateNewPropertyChanged,
    required this.selectedPropertyId,
    required this.onPropertySelected,
    required this.selectedUnitId,
    required this.onUnitSelected,
    required this.selectedTenantId,
    required this.onTenantSelected,
    required this.loanPropertyId,
    required this.onLoanPropertySelected,
    required this.confirming,
    required this.rejecting,
    required this.onConfirm,
    required this.onReject,
  });

  final ScanDraft draft;
  final Map<String, String> editedFields;
  final void Function(String name, String value) onFieldChanged;
  final bool isPaid;
  final ValueChanged<bool> onIsPaidChanged;
  final int? selectedTenantAccountId;
  final ValueChanged<int?> onTenantAccountSelected;
  final bool createNewProperty;
  final ValueChanged<bool> onCreateNewPropertyChanged;
  final int? selectedPropertyId;
  final ValueChanged<int?> onPropertySelected;
  final int? selectedUnitId;
  final ValueChanged<int?> onUnitSelected;
  final int? selectedTenantId;
  final ValueChanged<int?> onTenantSelected;
  final int? loanPropertyId;
  final ValueChanged<int?> onLoanPropertySelected;
  final bool confirming;
  final bool rejecting;
  final VoidCallback onConfirm;
  final VoidCallback onReject;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    // Use the data-layer contract getters so the interim Processing/Confirming
    // states the server reports are all handled here (L19).
    //
    // `extracting` drives the full-page "reading your document…" view: it's only
    // true while the server is still pulling fields out of the scan.
    // `actionsLocked` additionally covers the in-flight confirm state so Confirm
    // and Reject can't be tapped while the server is mid-confirm.
    final extracting = draft.isProcessing;
    final actionsLocked = draft.isProcessing || draft.isInFlight;
    final isTerminal = draft.isTerminal;
    final isFailed = draft.status == 'Failed';
    final busy = confirming || rejecting;

    // Confirm is only possible once the draft is ready for review (or Failed, so
    // manual values can still be entered) and nothing is in flight. For a lease:
    //  - CREATE mode (C3): a usable property name/address is needed (the server
    //    creates the property + unit from the document).
    //  - LINK mode: an existing property + unit must be chosen.
    final hasUsablePropertyText =
        (editedFields['property_address']?.trim().isNotEmpty ?? false) ||
        (editedFields['property_name']?.trim().isNotEmpty ?? false);
    final leaseReady =
        !draft.isLease ||
        (createNewProperty
            ? hasUsablePropertyText
            : (selectedPropertyId != null && selectedUnitId != null));
    // An Application needs a first + last name (both [Required] on the create),
    // mirroring the web review page's applicationInvalid guard.
    final applicationReady =
        !draft.isApplication ||
        ((editedFields['first_name']?.trim().isNotEmpty ?? false) &&
            (editedFields['last_name']?.trim().isNotEmpty ?? false));
    final loanReady = !draft.isLoan || loanPropertyId != null;
    final confirmEnabled =
        !actionsLocked &&
        !busy &&
        !isTerminal &&
        (!draft.isPayment || selectedTenantAccountId != null) &&
        leaseReady &&
        applicationReady &&
        loanReady;
    // Reject stays available on Failed so a bad scan can always be cleared, but
    // never while processing, mid-action, or already terminal.
    final rejectEnabled = (!actionsLocked || isFailed) && !busy && !isTerminal;

    return Scaffold(
      appBar: AppBar(
        title: Text('Review Scan #${draft.id}'),
        actions: [
          _StatusChip(status: draft.status),
          const SizedBox(width: 12),
        ],
      ),
      body: extracting
          ? _ProcessingView(draftId: draft.id)
          : ListView(
              padding: const EdgeInsets.only(bottom: 140),
              children: [
                if (draft.status == 'Failed')
                  _Banner(
                    color: colorScheme.errorContainer,
                    borderColor: colorScheme.error.withValues(alpha: 0.4),
                    textColor: colorScheme.onErrorContainer,
                    child: const Text(
                      'Extraction failed. You can still enter the values below and confirm.',
                    ),
                  ),

                if (draft.isNoOp)
                  _Banner(
                    color: Colors.amber.shade50,
                    borderColor: Colors.amber.shade300,
                    textColor: Colors.amber.shade900,
                    child: const Text(
                      'AI is off — no API key is configured. Enter the details below manually.',
                    ),
                  ),

                if (draft.status == 'Confirmed')
                  _Banner(
                    color: Colors.green.shade50,
                    borderColor: Colors.green.shade300,
                    textColor: Colors.green.shade900,
                    child: const Text('This scan has already been confirmed.'),
                  ),

                if (draft.status == 'Rejected')
                  _Banner(
                    color: colorScheme.surfaceContainerHighest,
                    borderColor: colorScheme.outlineVariant,
                    textColor: colorScheme.onSurfaceVariant,
                    child: const Text('This scan has been rejected.'),
                  ),

                if (draft.captureContext?.hasBusinessContext ?? false)
                  _CaptureContextBanner(context: draft.captureContext!),

                // ---- Document preview ----
                _DocumentPreview(draftId: draft.id),

                const Divider(height: 1),

                // ---- Lease selector (Payment only) ----
                if (draft.isPayment)
                  _TenantAccountSelector(
                    selectedTenantAccountId: selectedTenantAccountId,
                    contextualTenantAccountId:
                        draft.captureContext?.tenantAccountId,
                    onTenantAccountSelected: onTenantAccountSelected,
                  ),

                if (draft.isLease) ...[
                  // ---- "What confirming will do" proposal summary ----
                  if (draft.leaseProposal != null)
                    _LeaseProposalSummary(proposal: draft.leaseProposal!),

                  // ---- Property / Unit / Tenant pickers ----
                  _LeasePickers(
                    extractedTenantName: editedFields['tenant_name'],
                    createNewProperty: createNewProperty,
                    onCreateNewPropertyChanged: onCreateNewPropertyChanged,
                    editedFields: editedFields,
                    onFieldChanged: onFieldChanged,
                    selectedPropertyId: selectedPropertyId,
                    onPropertySelected: onPropertySelected,
                    selectedUnitId: selectedUnitId,
                    onUnitSelected: onUnitSelected,
                    selectedTenantId: selectedTenantId,
                    onTenantSelected: onTenantSelected,
                  ),

                  // ---- Lease terms (editable) ----
                  if (draft.scalarFields.isNotEmpty)
                    _LeaseTermsSection(
                      draft: draft,
                      editedFields: editedFields,
                      onFieldChanged: onFieldChanged,
                    ),
                ] else if (draft.isApplication) ...[
                  // ---- Applicant details (editable, single-entity review) ----
                  // Mirrors the web review page: a flat applicant form (NOT the
                  // guided lease flow). Confirming creates a RentalApplication.
                  // When extraction fails (no scalar fields) the same applicant
                  // form is shown with empty inputs — NOT the Expense fallback.
                  if (draft.scalarFields.isNotEmpty ||
                      draft.status != 'Pending')
                    _ApplicantSection(
                      draft: draft,
                      editedFields: editedFields,
                      onFieldChanged: onFieldChanged,
                    ),
                ] else if (draft.isLoan) ...[
                  _LoanPropertyPicker(
                    selectedPropertyId: loanPropertyId,
                    onPropertySelected: onLoanPropertySelected,
                  ),
                  if (draft.scalarFields.isNotEmpty ||
                      draft.status != 'Pending')
                    _LoanFieldsSection(
                      draft: draft,
                      editedFields: editedFields,
                      onFieldChanged: onFieldChanged,
                    ),
                ] else ...[
                  // ---- Extracted field groups ----
                  if (draft.scalarFields.isNotEmpty)
                    _FieldsSection(
                      draft: draft,
                      editedFields: editedFields,
                      onFieldChanged: onFieldChanged,
                    )
                  else if (draft.status != 'Pending')
                    _ManualEntrySection(
                      editedFields: editedFields,
                      onFieldChanged: onFieldChanged,
                    ),

                  // ---- Line items (read-only) ----
                  if (draft.lineItems.isNotEmpty)
                    _LineItemsSection(items: draft.lineItems),

                  // ---- Paid/Unpaid toggle (Expense only) ----
                  if (!draft.isPayment && !draft.isWorkOrder)
                    _PaidToggle(
                      isPaid: isPaid,
                      dueDate: editedFields['due_date'],
                      onChanged: onIsPaidChanged,
                    ),
                ],

                const SizedBox(height: 8),
              ],
            ),
      // ---- Bottom action bar ----
      bottomSheet: Container(
        padding: EdgeInsets.fromLTRB(
          16,
          12,
          16,
          12 + MediaQuery.of(context).padding.bottom,
        ),
        decoration: BoxDecoration(
          color: colorScheme.surface,
          border: Border(top: BorderSide(color: colorScheme.outlineVariant)),
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (draft.isPayment &&
                selectedTenantAccountId == null &&
                !isTerminal)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Text(
                  'Select a rental account above to enable payment creation.',
                  style: TextStyle(fontSize: 12, color: Colors.amber.shade700),
                  textAlign: TextAlign.center,
                ),
              ),
            if (draft.isLease && !leaseReady && !isTerminal)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Text(
                  createNewProperty
                      ? 'Enter a property name or address above to create this lease.'
                      : 'Choose a property and unit above to create this lease.',
                  style: TextStyle(fontSize: 12, color: Colors.amber.shade700),
                  textAlign: TextAlign.center,
                ),
              ),
            if (draft.isApplication && !applicationReady && !isTerminal)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Text(
                  'Enter the applicant’s first and last name to create this application.',
                  style: TextStyle(fontSize: 12, color: Colors.amber.shade700),
                  textAlign: TextAlign.center,
                ),
              ),
            if (draft.isLoan && !loanReady && !isTerminal)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Text(
                  'Select a property to enable loan creation.',
                  style: TextStyle(fontSize: 12, color: Colors.amber.shade700),
                  textAlign: TextAlign.center,
                ),
              ),
            // Surface the interim "Confirming" state (L19): the server is mid-
            // confirm even though this client didn't start it.
            if (draft.isInFlight && !isFailed && !confirming)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Text(
                  'Saving this record…',
                  style: TextStyle(
                    fontSize: 12,
                    color: colorScheme.onSurfaceVariant,
                  ),
                  textAlign: TextAlign.center,
                ),
              ),
            // Why `OverflowBar` and not a plain `Row`: this Scaffold.bottomSheet
            // is driven by a DraggableScrollableActuator/Stack that lays its
            // child out with an unbounded width (constraints: unconstrained in
            // the render tree). A horizontal `Row` in that context hands its
            // non-flex children an unbounded width; a default OutlinedButton
            // (ButtonStyle.maximumSize == Size.infinite) cannot accept that and
            // trips RenderConstrainedBox's "BoxConstraints forces an infinite
            // width" assert, which aborts layout of the whole bottom-sheet
            // subtree — the symptom was a bottom bar that never rendered, a body
            // ListView that would not scroll, and a lease dropdown that would
            // not open. Wrapping the buttons in Expanded does NOT help, because
            // an Expanded child of an unbounded-width Row also fails to resolve.
            // `OverflowBar` lays its children out without forcing an unbounded
            // width, so the buttons size to their content (and wrap to a second
            // line if they ever overflow). Mirrors the guided-flow fix in
            // commit cee9e78.
            OverflowBar(
              alignment: MainAxisAlignment.spaceBetween,
              overflowAlignment: OverflowBarAlignment.end,
              overflowSpacing: 8,
              children: [
                FilledButton(
                  onPressed: confirmEnabled ? onConfirm : null,
                  child: (confirming || (draft.isInFlight && !isFailed))
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: Colors.white,
                          ),
                        )
                      : Text(
                          draft.isPayment
                              ? 'Create Payment'
                              : draft.isWorkOrder
                              ? 'Create Work Order'
                              : draft.isLease
                              ? 'Create Lease'
                              : draft.isApplication
                              ? 'Create Application'
                              : draft.isLoan
                              ? 'Create Loan'
                              : 'Confirm & Create Expense',
                        ),
                ),
                // design#9: Reject is clearly destructive (error-coloured text +
                // border) so it can't be mistaken for a secondary action.
                OutlinedButton(
                  onPressed: rejectEnabled ? onReject : null,
                  style: OutlinedButton.styleFrom(
                    foregroundColor: colorScheme.error,
                    side: BorderSide(
                      color: rejectEnabled
                          ? colorScheme.error
                          : colorScheme.outlineVariant,
                    ),
                  ),
                  child: rejecting
                      ? SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(
                            strokeWidth: 2,
                            color: colorScheme.error,
                          ),
                        )
                      : const Text('Reject'),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _CaptureContextBanner extends StatelessWidget {
  const _CaptureContextBanner({required this.context});

  final ScanCaptureContext context;

  @override
  Widget build(BuildContext buildContext) {
    final colorScheme = Theme.of(buildContext).colorScheme;
    final items = <String>[
      if (context.propertyId != null) 'Property #${context.propertyId}',
      if (context.unitId != null) 'Unit #${context.unitId}',
      if (context.leaseManagementId != null)
        'Rental relationship #${context.leaseManagementId}',
      if (context.leaseAgreementId != null)
        'Agreement #${context.leaseAgreementId}',
      if (context.tenantAccountId != null)
        'Rental account #${context.tenantAccountId}',
      if (context.tenantLedgerEntryId != null)
        'Ledger entry #${context.tenantLedgerEntryId}',
      if (context.workOrderId != null) 'Work order #${context.workOrderId}',
      if (context.applicationId != null)
        'Application #${context.applicationId}',
      if (context.rentalListingId != null)
        'Listing #${context.rentalListingId}',
    ];

    return _Banner(
      color: colorScheme.secondaryContainer,
      borderColor: colorScheme.secondary.withValues(alpha: 0.35),
      textColor: colorScheme.onSecondaryContainer,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'This scan will stay connected to:',
            style: Theme.of(buildContext).textTheme.labelLarge?.copyWith(
              color: colorScheme.onSecondaryContainer,
              fontWeight: FontWeight.w700,
            ),
          ),
          const SizedBox(height: 8),
          Wrap(
            spacing: 8,
            runSpacing: 6,
            children: items.map((item) => Chip(label: Text(item))).toList(),
          ),
          if (context.sourceLabel?.trim().isNotEmpty ?? false) ...[
            const SizedBox(height: 6),
            Text('Source: ${context.sourceLabel!.trim()}'),
          ],
          const SizedBox(height: 6),
          const Text(
            'Rental Command will verify this context again when you confirm.',
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _ProcessingView — prominent full-page "scanning…" state
// ---------------------------------------------------------------------------

class _ProcessingView extends StatelessWidget {
  const _ProcessingView({required this.draftId});

  final int draftId;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Column(
      children: [
        // Show the captured document so the user knows what's being read.
        _DocumentPreview(draftId: draftId),
        Expanded(
          child: Center(
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 32),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const SizedBox(
                    width: 64,
                    height: 64,
                    child: CircularProgressIndicator(strokeWidth: 5),
                  ),
                  const SizedBox(height: 28),
                  Text(
                    'Reading your document…',
                    style: theme.textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w600,
                    ),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 10),
                  Text(
                    'The computer is pulling out the vendor, amounts, and dates '
                    'for you. This usually takes just a few seconds.',
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: theme.colorScheme.onSurfaceVariant,
                    ),
                    textAlign: TextAlign.center,
                  ),
                ],
              ),
            ),
          ),
        ),
      ],
    );
  }
}

// ---------------------------------------------------------------------------
// _DocumentPreview — fetches image bytes via Dio (bearer-authed)
// ---------------------------------------------------------------------------

class _DocumentPreview extends ConsumerWidget {
  const _DocumentPreview({required this.draftId});

  final int draftId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final imageAsync = ref.watch(_imageProvider(draftId));

    return Container(
      height: 240,
      color: Colors.grey.shade100,
      child: imageAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                Icons.broken_image_outlined,
                color: Colors.grey.shade400,
                size: 48,
              ),
              const SizedBox(height: 8),
              Text(
                'Preview unavailable',
                style: TextStyle(color: Colors.grey.shade500),
              ),
            ],
          ),
        ),
        data: (bytes) => InteractiveViewer(
          child: Image.memory(
            bytes,
            fit: BoxFit.contain,
            // Decode at preview scale (not full sensor resolution) so a large
            // stored image doesn't pin the CPU on lower-end / throttled devices.
            cacheHeight: 1080,
            errorBuilder: (ctx, e, _) => Center(
              child: Icon(
                Icons.picture_as_pdf_outlined,
                size: 64,
                color: Colors.grey.shade400,
              ),
            ),
          ),
        ),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _TenantAccountSelector
// ---------------------------------------------------------------------------

class _TenantAccountSelector extends ConsumerStatefulWidget {
  const _TenantAccountSelector({
    required this.selectedTenantAccountId,
    required this.contextualTenantAccountId,
    required this.onTenantAccountSelected,
  });

  final int? selectedTenantAccountId;
  final int? contextualTenantAccountId;
  final ValueChanged<int?> onTenantAccountSelected;

  @override
  ConsumerState<_TenantAccountSelector> createState() =>
      _TenantAccountSelectorState();
}

class _TenantAccountSelectorState
    extends ConsumerState<_TenantAccountSelector> {
  static const _pageSize = 25;
  final _searchController = TextEditingController();
  Timer? _debounce;
  String _search = '';
  int _skip = 0;
  TenantAccountOption? _selectedOption;

  @override
  void dispose() {
    _debounce?.cancel();
    _searchController.dispose();
    super.dispose();
  }

  void _searchChanged(String value) {
    _debounce?.cancel();
    _debounce = Timer(const Duration(milliseconds: 300), () {
      if (!mounted) return;
      setState(() {
        _search = value.trim();
        _skip = 0;
      });
    });
  }

  @override
  Widget build(BuildContext context) {
    final accountsAsync = ref.watch(
      _tenantAccountOptionsProvider((search: _search, skip: _skip)),
    );
    final pageAccounts =
        accountsAsync.value?.items ?? const <TenantAccountOption>[];
    final contextualId = widget.selectedTenantAccountId == null
        ? widget.contextualTenantAccountId
        : null;
    final contextualIsInPage =
        contextualId != null &&
        pageAccounts.any((item) => item.tenantAccountId == contextualId);
    final contextualAccountAsync =
        contextualId != null &&
            accountsAsync.value != null &&
            !contextualIsInPage
        ? ref.watch(_tenantAccountOptionProvider(contextualId))
        : const AsyncValue<TenantAccountOption?>.data(null);
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Which rental account is this payment for?',
            style: theme.textTheme.labelLarge?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 2),
          Text(
            'Required to record this payment.',
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _searchController,
            onChanged: _searchChanged,
            decoration: const InputDecoration(
              border: OutlineInputBorder(),
              prefixIcon: Icon(Icons.search),
              hintText: 'Search tenant, property, unit, or relationship',
            ),
          ),
          const SizedBox(height: 8),
          accountsAsync.when(
            loading: () => const LinearProgressIndicator(),
            error: (e, _) => Text(
              'Could not load rental accounts.',
              style: TextStyle(color: colorScheme.error),
            ),
            data: (page) {
              final accounts = [...page.items];
              final contextualAccount = contextualIsInPage
                  ? accounts
                        .where((item) => item.tenantAccountId == contextualId)
                        .firstOrNull
                  : contextualAccountAsync.value;
              if (contextualAccount != null &&
                  !accounts.any(
                    (item) =>
                        item.tenantAccountId ==
                        contextualAccount.tenantAccountId,
                  )) {
                accounts.insert(0, contextualAccount);
              }
              if (_selectedOption != null &&
                  !accounts.any(
                    (item) =>
                        item.tenantAccountId ==
                        _selectedOption!.tenantAccountId,
                  )) {
                accounts.insert(0, _selectedOption!);
              }
              if (widget.selectedTenantAccountId == null &&
                  contextualAccount != null) {
                WidgetsBinding.instance.addPostFrameCallback((_) {
                  if (mounted && widget.selectedTenantAccountId == null) {
                    _selectedOption = contextualAccount;
                    widget.onTenantAccountSelected(
                      contextualAccount.tenantAccountId,
                    );
                  }
                });
              }
              final visibleSelectedId =
                  accounts.any(
                    (item) =>
                        item.tenantAccountId ==
                        (widget.selectedTenantAccountId ??
                            contextualAccount?.tenantAccountId),
                  )
                  ? widget.selectedTenantAccountId ??
                        contextualAccount?.tenantAccountId
                  : null;
              return Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  DropdownButtonFormField<int>(
                    initialValue: visibleSelectedId,
                    hint: const Text('Select a rental account'),
                    isExpanded: true,
                    decoration: const InputDecoration(
                      border: OutlineInputBorder(),
                      contentPadding: EdgeInsets.symmetric(
                        horizontal: 12,
                        vertical: 10,
                      ),
                    ),
                    items: accounts.map((account) {
                      final tenant = account.primaryTenantName?.trim();
                      final label =
                          '${account.propertyName} · Unit ${account.unitNumber}'
                          '${tenant == null || tenant.isEmpty ? '' : ' — $tenant'}'
                          ' · ${account.relationshipNumber}';
                      return DropdownMenuItem(
                        value: account.tenantAccountId,
                        child: Text(label, overflow: TextOverflow.ellipsis),
                      );
                    }).toList(),
                    onChanged: (id) {
                      _selectedOption = accounts
                          .where((item) => item.tenantAccountId == id)
                          .firstOrNull;
                      widget.onTenantAccountSelected(id);
                    },
                  ),
                  const SizedBox(height: 6),
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          page.totalCount == 0
                              ? 'No matching rental accounts'
                              : '${page.skip + 1}–${page.skip + page.items.length} of ${page.totalCount}',
                          style: theme.textTheme.bodySmall?.copyWith(
                            color: colorScheme.onSurfaceVariant,
                          ),
                        ),
                      ),
                      TextButton(
                        onPressed: _skip == 0
                            ? null
                            : () => setState(
                                () => _skip = (_skip - _pageSize)
                                    .clamp(0, page.totalCount)
                                    .toInt(),
                              ),
                        child: const Text('Previous'),
                      ),
                      TextButton(
                        onPressed: _skip + page.items.length >= page.totalCount
                            ? null
                            : () => setState(() => _skip += _pageSize),
                        child: const Text('Next'),
                      ),
                    ],
                  ),
                ],
              );
            },
          ),
          const SizedBox(height: 4),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _LeasePickers — property (required) → unit (required, scoped) → tenant (opt.)
// ---------------------------------------------------------------------------

/// Builds a tenant's display name, preferring the server-provided full name.
String _tenantLabel(Tenant t) {
  final full = t.fullName?.trim();
  if (full != null && full.isNotEmpty) return full;
  final name = '${t.firstName} ${t.lastName}'.trim();
  return name.isEmpty ? 'Tenant #${t.id}' : name;
}

/// "What confirming will do" summary for a lease draft (C3): one line each for
/// the property and the unit — link an existing record vs create a new one from
/// the document — so the empty-portfolio bootstrap is visible up front.
class _LeaseProposalSummary extends StatelessWidget {
  const _LeaseProposalSummary({required this.proposal});

  final LeaseImportProposal proposal;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    return Container(
      margin: const EdgeInsets.fromLTRB(16, 16, 16, 0),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: scheme.surfaceContainerHighest.withValues(alpha: 0.4),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: scheme.outlineVariant),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'When you confirm',
            style: theme.textTheme.labelMedium?.copyWith(
              fontWeight: FontWeight.w700,
              color: scheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          _ProposalLine(record: proposal.property, kind: 'property'),
          const SizedBox(height: 6),
          _ProposalLine(record: proposal.unit, kind: 'unit'),
        ],
      ),
    );
  }
}

class _ProposalLine extends StatelessWidget {
  const _ProposalLine({required this.record, required this.kind});

  final ProposedRecord record;
  final String kind;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;

    final IconData icon;
    final Color color;
    final String text;
    if (record.isLink) {
      icon = Icons.link_rounded;
      color = scheme.primary;
      final label = record.label?.trim();
      text =
          'Link existing $kind${label != null && label.isNotEmpty ? ': $label' : ''}';
    } else if (record.isCreate) {
      icon = Icons.add_circle_outline_rounded;
      color = scheme.tertiary;
      final label = record.label?.trim();
      text =
          'Create new $kind${label != null && label.isNotEmpty ? ': $label' : ''}';
    } else {
      icon = Icons.help_outline_rounded;
      color = Colors.amber.shade700;
      text = 'Choose a $kind below';
    }

    final detail = record.detail?.trim();

    return Row(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Icon(icon, size: 16, color: color),
        const SizedBox(width: 8),
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(text, style: theme.textTheme.bodySmall),
              if (detail != null && detail.isNotEmpty)
                Text(
                  detail,
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: scheme.onSurfaceVariant,
                  ),
                ),
            ],
          ),
        ),
      ],
    );
  }
}

class _LeasePickers extends ConsumerWidget {
  const _LeasePickers({
    required this.extractedTenantName,
    required this.createNewProperty,
    required this.onCreateNewPropertyChanged,
    required this.editedFields,
    required this.onFieldChanged,
    required this.selectedPropertyId,
    required this.onPropertySelected,
    required this.selectedUnitId,
    required this.onUnitSelected,
    required this.selectedTenantId,
    required this.onTenantSelected,
  });

  final String? extractedTenantName;
  final bool createNewProperty;
  final ValueChanged<bool> onCreateNewPropertyChanged;
  final Map<String, String> editedFields;
  final void Function(String name, String value) onFieldChanged;
  final int? selectedPropertyId;
  final ValueChanged<int?> onPropertySelected;
  final int? selectedUnitId;
  final ValueChanged<int?> onUnitSelected;
  final int? selectedTenantId;
  final ValueChanged<int?> onTenantSelected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final tenantsAsync = ref.watch(_tenantsProvider);

    final newTenantName = (extractedTenantName ?? '').trim();
    final createLabel = newTenantName.isEmpty
        ? 'Create a new tenant'
        : 'Create “$newTenantName”';

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Where does this lease belong?',
            style: theme.textTheme.labelLarge?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 2),
          Text(
            createNewProperty
                ? 'No matching property — a new one will be created from the '
                      'document. Check the address, or link an existing property '
                      'instead.'
                : 'Link the property and unit. The tenant is matched from the '
                      'lease — or you can choose one.',
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 12),

          // ── Property mode toggle: create-new vs link-existing (C3) ────────
          SegmentedButton<bool>(
            segments: const [
              ButtonSegment<bool>(
                value: true,
                label: Text('Create new'),
                icon: Icon(Icons.add_home_outlined),
              ),
              ButtonSegment<bool>(
                value: false,
                label: Text('Link existing'),
                icon: Icon(Icons.link_outlined),
              ),
            ],
            selected: {createNewProperty},
            onSelectionChanged: (s) => onCreateNewPropertyChanged(s.first),
            showSelectedIcon: false,
          ),
          const SizedBox(height: 14),

          if (createNewProperty)
            _CreatePropertyFields(
              editedFields: editedFields,
              onFieldChanged: onFieldChanged,
            )
          else
            _LinkPropertyFields(
              selectedPropertyId: selectedPropertyId,
              onPropertySelected: onPropertySelected,
              selectedUnitId: selectedUnitId,
              onUnitSelected: onUnitSelected,
            ),
          const SizedBox(height: 14),

          // ── Tenant (optional) ────────────────────────────────────────────
          _PickerLabel(text: 'Tenant', required: false),
          tenantsAsync.when(
            loading: () => const LinearProgressIndicator(),
            error: (e, _) => Text(
              'Could not load tenants.',
              style: TextStyle(color: colorScheme.error),
            ),
            data: (tenants) {
              final ids = tenants.map((t) => t.id).toSet();
              final value = ids.contains(selectedTenantId)
                  ? selectedTenantId
                  : null;
              return DropdownButtonFormField<int?>(
                initialValue: value,
                isExpanded: true,
                decoration: _pickerDecoration,
                // A null value means "let the server match/create from the
                // extracted tenant_name" — surfaced as the first option.
                items: <DropdownMenuItem<int?>>[
                  DropdownMenuItem<int?>(
                    value: null,
                    child: Text(
                      createLabel,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(color: colorScheme.primary),
                    ),
                  ),
                  ...tenants.map(
                    (t) => DropdownMenuItem<int?>(
                      value: t.id,
                      child: Text(
                        _tenantLabel(t),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                  ),
                ],
                onChanged: onTenantSelected,
              );
            },
          ),
        ],
      ),
    );
  }
}

/// Shared input decoration for the lease pickers (property / unit / tenant).
const _pickerDecoration = InputDecoration(
  border: OutlineInputBorder(),
  contentPadding: EdgeInsets.symmetric(horizontal: 12, vertical: 10),
);

/// Link-to-existing property → unit pickers (both required in this mode).
class _LinkPropertyFields extends ConsumerWidget {
  const _LinkPropertyFields({
    required this.selectedPropertyId,
    required this.onPropertySelected,
    required this.selectedUnitId,
    required this.onUnitSelected,
  });

  final int? selectedPropertyId;
  final ValueChanged<int?> onPropertySelected;
  final int? selectedUnitId;
  final ValueChanged<int?> onUnitSelected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final propertiesAsync = ref.watch(_propertiesProvider);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // ── Property (required) ──────────────────────────────────────────
        _PickerLabel(text: 'Property', required: true),
        propertiesAsync.when(
          loading: () => const LinearProgressIndicator(),
          error: (e, _) => Text(
            'Could not load properties.',
            style: TextStyle(color: colorScheme.error),
          ),
          data: (properties) {
            if (properties.isEmpty) {
              return Text(
                'You have no properties yet — switch to "Create new" to add one '
                'from this lease.',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: colorScheme.error,
                ),
              );
            }
            final ids = properties.map((p) => p.id).toSet();
            final value = ids.contains(selectedPropertyId)
                ? selectedPropertyId
                : null;
            return DropdownButtonFormField<int>(
              initialValue: value,
              hint: const Text('Select a property'),
              isExpanded: true,
              decoration: _pickerDecoration,
              items: properties
                  .map(
                    (p) => DropdownMenuItem(
                      value: p.id,
                      child: Text(p.name, overflow: TextOverflow.ellipsis),
                    ),
                  )
                  .toList(),
              onChanged: onPropertySelected,
            );
          },
        ),
        const SizedBox(height: 14),

        // ── Unit (required, scoped to property) ──────────────────────────
        _PickerLabel(text: 'Unit', required: true),
        if (selectedPropertyId == null)
          Text(
            'Choose a property first.',
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
          )
        else
          _UnitPicker(
            propertyId: selectedPropertyId!,
            selectedUnitId: selectedUnitId,
            onUnitSelected: onUnitSelected,
          ),
      ],
    );
  }
}

/// Create-new-property address form (C3). The fields are bound to the draft's
/// extracted property_name / property_address / property_city / property_state /
/// property_postal_code via [onFieldChanged]; the server creates the property
/// (and the unit, from the extracted unit number) on confirm.
///
/// The street address uses the shared Places-backed [AddressAutocompleteField]
/// (same server-side proxy as the web AddressAutocomplete). Picking a suggestion
/// fills city/state/zip; manual entry always works when no key is configured.
class _CreatePropertyFields extends StatefulWidget {
  const _CreatePropertyFields({
    required this.editedFields,
    required this.onFieldChanged,
  });

  final Map<String, String> editedFields;
  final void Function(String name, String value) onFieldChanged;

  @override
  State<_CreatePropertyFields> createState() => _CreatePropertyFieldsState();
}

class _CreatePropertyFieldsState extends State<_CreatePropertyFields> {
  late final TextEditingController _addressCtrl;

  @override
  void initState() {
    super.initState();
    _addressCtrl = TextEditingController(
      text: widget.editedFields['property_address'] ?? '',
    );
    // Mirror manual typing in the address field back into the edited map.
    _addressCtrl.addListener(_syncAddress);
  }

  @override
  void didUpdateWidget(_CreatePropertyFields old) {
    super.didUpdateWidget(old);
    // Keep the address field in sync when a resolved suggestion (or an external
    // edit) changes the underlying value, without clobbering in-progress typing.
    final incoming = widget.editedFields['property_address'] ?? '';
    if (incoming != (old.editedFields['property_address'] ?? '') &&
        _addressCtrl.text != incoming) {
      _addressCtrl.text = incoming;
    }
  }

  @override
  void dispose() {
    _addressCtrl.removeListener(_syncAddress);
    _addressCtrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _PickerLabel(text: 'Property name or address', required: true),
        AddressAutocompleteField(
          controller: _addressCtrl,
          testKey: 'create-property-property_address',
          onResolved: (a) {
            if (a.line1.isNotEmpty) {
              widget.onFieldChanged('property_address', a.line1);
            }
            if (a.city.isNotEmpty) {
              widget.onFieldChanged('property_city', a.city);
            }
            if (a.state.isNotEmpty) {
              widget.onFieldChanged('property_state', a.state);
            }
            if (a.zip.isNotEmpty) {
              widget.onFieldChanged('property_postal_code', a.zip);
            }
          },
        ),
        const SizedBox(height: 10),
        _PlainFieldInput(
          fieldName: 'property_name',
          value: widget.editedFields['property_name'] ?? '',
          hintText: 'Property name (optional)',
          onChanged: (v) => widget.onFieldChanged('property_name', v),
        ),
        const SizedBox(height: 10),
        _PlainFieldInput(
          fieldName: 'property_city',
          value: widget.editedFields['property_city'] ?? '',
          hintText: 'City (optional)',
          onChanged: (v) => widget.onFieldChanged('property_city', v),
        ),
        const SizedBox(height: 10),
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(
              child: _PlainFieldInput(
                fieldName: 'property_state',
                value: widget.editedFields['property_state'] ?? '',
                hintText: 'State (optional)',
                onChanged: (v) => widget.onFieldChanged('property_state', v),
              ),
            ),
            const SizedBox(width: 12),
            Expanded(
              child: _PlainFieldInput(
                fieldName: 'property_postal_code',
                value: widget.editedFields['property_postal_code'] ?? '',
                hintText: 'ZIP (optional)',
                onChanged: (v) =>
                    widget.onFieldChanged('property_postal_code', v),
              ),
            ),
          ],
        ),
        const SizedBox(height: 6),
        Text(
          'The unit is created from the lease automatically.',
          style: Theme.of(context).textTheme.bodySmall?.copyWith(
            color: Theme.of(context).colorScheme.onSurfaceVariant,
          ),
        ),
      ],
    );
  }

  void _syncAddress() {
    widget.onFieldChanged('property_address', _addressCtrl.text);
  }
}

/// A self-contained editable text field (manages its own controller) used by the
/// create-new-property form. Mirrors [_FieldInputState]'s controller handling
/// but without the confidence chrome.
class _PlainFieldInput extends StatefulWidget {
  const _PlainFieldInput({
    required this.fieldName,
    required this.value,
    required this.hintText,
    required this.onChanged,
  });

  final String fieldName;
  final String value;
  final String hintText;
  final ValueChanged<String> onChanged;

  @override
  State<_PlainFieldInput> createState() => _PlainFieldInputState();
}

class _PlainFieldInputState extends State<_PlainFieldInput> {
  late TextEditingController _controller;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(text: widget.value);
  }

  @override
  void didUpdateWidget(_PlainFieldInput old) {
    super.didUpdateWidget(old);
    if (old.value != widget.value && _controller.text != widget.value) {
      _controller.text = widget.value;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return TextFormField(
      key: Key('create-property-${widget.fieldName}'),
      controller: _controller,
      onChanged: widget.onChanged,
      textInputAction: TextInputAction.next,
      textCapitalization: TextCapitalization.words,
      decoration: InputDecoration(
        border: const OutlineInputBorder(),
        hintText: widget.hintText,
        contentPadding: const EdgeInsets.symmetric(
          horizontal: 12,
          vertical: 10,
        ),
      ),
    );
  }
}

/// Unit dropdown scoped to a single property; rebuilds when [propertyId] changes.
class _UnitPicker extends ConsumerWidget {
  const _UnitPicker({
    required this.propertyId,
    required this.selectedUnitId,
    required this.onUnitSelected,
  });

  final int propertyId;
  final int? selectedUnitId;
  final ValueChanged<int?> onUnitSelected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final colorScheme = Theme.of(context).colorScheme;
    final unitsAsync = ref.watch(_unitsForPropertyProvider(propertyId));

    return unitsAsync.when(
      loading: () => const LinearProgressIndicator(),
      error: (e, _) => Text(
        'Could not load units.',
        style: TextStyle(color: colorScheme.error),
      ),
      data: (units) {
        if (units.isEmpty) {
          return Text(
            'This property has no units yet.',
            style: TextStyle(color: colorScheme.error, fontSize: 13),
          );
        }
        final ids = units.map((u) => u.id).toSet();
        final value = ids.contains(selectedUnitId) ? selectedUnitId : null;
        return DropdownButtonFormField<int>(
          initialValue: value,
          hint: const Text('Select a unit'),
          isExpanded: true,
          decoration: const InputDecoration(
            border: OutlineInputBorder(),
            contentPadding: EdgeInsets.symmetric(horizontal: 12, vertical: 10),
          ),
          items: units
              .map(
                (u) => DropdownMenuItem(
                  value: u.id,
                  child: Text(
                    'Unit ${u.unitNumber}',
                    overflow: TextOverflow.ellipsis,
                  ),
                ),
              )
              .toList(),
          onChanged: onUnitSelected,
        );
      },
    );
  }
}

class _PickerLabel extends StatelessWidget {
  const _PickerLabel({required this.text, required this.required});

  final String text;
  final bool required;

  @override
  Widget build(BuildContext context) {
    final colorScheme = Theme.of(context).colorScheme;
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Row(
        children: [
          Text(
            text,
            style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600),
          ),
          if (required) ...[
            const SizedBox(width: 4),
            Text('*', style: TextStyle(fontSize: 12, color: colorScheme.error)),
          ] else ...[
            const SizedBox(width: 6),
            Text(
              '(optional)',
              style: TextStyle(
                fontSize: 11,
                color: colorScheme.onSurfaceVariant,
              ),
            ),
          ],
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _LeaseTermsSection — editable lease terms in a fixed, readable order
// ---------------------------------------------------------------------------

class _LeaseTermsSection extends StatelessWidget {
  const _LeaseTermsSection({
    required this.draft,
    required this.editedFields,
    required this.onFieldChanged,
  });

  final ScanDraft draft;
  final Map<String, String> editedFields;
  final void Function(String, String) onFieldChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final fieldMap = {for (final f in draft.scalarFields) f.name: f};

    // Show the lease terms in a stable order; the property/unit/tenant ids and
    // tenant_name are handled by the pickers, so they're not repeated here.
    final ordered = <ScanField>[
      for (final name in _leaseFieldOrder)
        fieldMap[name] ?? ScanField(name: name, value: '', confidence: 1.0),
    ];

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'LEASE TERMS',
            style: theme.textTheme.labelSmall?.copyWith(
              fontWeight: FontWeight.w700,
              letterSpacing: 0.8,
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          ...ordered.map(
            (field) => Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: _FieldInput(
                field: field,
                value: editedFields[field.name] ?? field.value,
                onChanged: (v) => onFieldChanged(field.name, v),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _ApplicantSection — editable applicant fields in a fixed, readable order
// ---------------------------------------------------------------------------

/// Renders a scanned application's applicant fields as a flat editable form, in
/// the same display order as the web review page. property_id/unit_id are NOT
/// shown — they're the in-portfolio link carried to confirm (validated server-
/// side), not user-editable here.
class _ApplicantSection extends StatelessWidget {
  const _ApplicantSection({
    required this.draft,
    required this.editedFields,
    required this.onFieldChanged,
  });

  final ScanDraft draft;
  final Map<String, String> editedFields;
  final void Function(String, String) onFieldChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final fieldMap = {for (final f in draft.scalarFields) f.name: f};

    final ordered = <ScanField>[
      for (final name in _applicationFieldOrder)
        fieldMap[name] ?? ScanField(name: name, value: '', confidence: 1.0),
    ];

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'APPLICANT',
            style: theme.textTheme.labelSmall?.copyWith(
              fontWeight: FontWeight.w700,
              letterSpacing: 0.8,
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          ...ordered.map(
            (field) => Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: _FieldInput(
                field: field,
                value: editedFields[field.name] ?? field.value,
                onChanged: (v) => onFieldChanged(field.name, v),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _LoanFieldsSection — editable loan fields in web form order
// ---------------------------------------------------------------------------

class _LoanPropertyPicker extends ConsumerWidget {
  const _LoanPropertyPicker({
    required this.selectedPropertyId,
    required this.onPropertySelected,
  });

  final int? selectedPropertyId;
  final ValueChanged<int?> onPropertySelected;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final propertiesAsync = ref.watch(_propertiesProvider);

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Which property does this loan belong to?',
            style: theme.textTheme.labelLarge?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 2),
          Text(
            'Attach the mortgage or loan to one property before creating it.',
            style: theme.textTheme.bodySmall?.copyWith(
              color: colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 12),
          _PickerLabel(text: 'Property', required: true),
          propertiesAsync.when(
            loading: () => const LinearProgressIndicator(),
            error: (e, _) => Text(
              'Could not load properties.',
              style: TextStyle(color: colorScheme.error),
            ),
            data: (properties) {
              if (properties.isEmpty) {
                return Text(
                  'Add a property before creating a loan from this scan.',
                  style: theme.textTheme.bodySmall?.copyWith(
                    color: colorScheme.error,
                  ),
                );
              }
              final ids = properties.map((p) => p.id).toSet();
              final value = ids.contains(selectedPropertyId)
                  ? selectedPropertyId
                  : null;
              return DropdownButtonFormField<int>(
                initialValue: value,
                hint: const Text('Select a property'),
                isExpanded: true,
                decoration: _pickerDecoration,
                items: properties
                    .map(
                      (p) => DropdownMenuItem(
                        value: p.id,
                        child: Text(p.name, overflow: TextOverflow.ellipsis),
                      ),
                    )
                    .toList(),
                onChanged: onPropertySelected,
              );
            },
          ),
        ],
      ),
    );
  }
}

class _LoanFieldsSection extends StatelessWidget {
  const _LoanFieldsSection({
    required this.draft,
    required this.editedFields,
    required this.onFieldChanged,
  });

  final ScanDraft draft;
  final Map<String, String> editedFields;
  final void Function(String, String) onFieldChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final fieldMap = {for (final f in draft.scalarFields) f.name: f};

    final fields = _loanFieldOrder.map((name) {
      return fieldMap[name] ??
          ScanField(name: name, value: editedFields[name] ?? '', confidence: 1);
    }).toList();

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'LOAN DETAILS',
            style: theme.textTheme.labelSmall?.copyWith(
              fontWeight: FontWeight.w700,
              letterSpacing: 0.8,
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          ...fields.map(
            (field) => Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: _FieldInput(
                field: field,
                value: editedFields[field.name] ?? field.value,
                onChanged: (v) => onFieldChanged(field.name, v),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _FieldsSection — grouped editable fields with confidence indicators
// ---------------------------------------------------------------------------

class _FieldsSection extends StatelessWidget {
  const _FieldsSection({
    required this.draft,
    required this.editedFields,
    required this.onFieldChanged,
  });

  final ScanDraft draft;
  final Map<String, String> editedFields;
  final void Function(String, String) onFieldChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final fieldMap = {for (final f in draft.scalarFields) f.name: f};

    // Build ordered groups
    final groups = <({String label, List<ScanField> fields})>[];
    for (final g in _fieldGroups) {
      final present = g.fields
          .map((n) => fieldMap[n])
          .whereType<ScanField>()
          .toList();
      if (present.isNotEmpty) {
        groups.add((label: g.label, fields: present));
      }
    }
    // "Other" group
    final otherFields = draft.scalarFields
        .where((f) => !_knownScalarFields.contains(f.name))
        .toList();
    if (otherFields.isNotEmpty) {
      groups.add((label: 'Other', fields: otherFields));
    }

    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: groups.map((group) {
          return Padding(
            padding: const EdgeInsets.only(bottom: 24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  group.label.toUpperCase(),
                  style: theme.textTheme.labelSmall?.copyWith(
                    fontWeight: FontWeight.w700,
                    letterSpacing: 0.8,
                    color: theme.colorScheme.onSurfaceVariant,
                  ),
                ),
                const SizedBox(height: 8),
                ...group.fields.map(
                  (field) => Padding(
                    padding: const EdgeInsets.only(bottom: 12),
                    child: _FieldInput(
                      field: field,
                      value: editedFields[field.name] ?? field.value,
                      onChanged: (v) => onFieldChanged(field.name, v),
                    ),
                  ),
                ),
              ],
            ),
          );
        }).toList(),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _FieldInput — editable field with confidence indicator
// ---------------------------------------------------------------------------

class _FieldInput extends StatefulWidget {
  const _FieldInput({
    required this.field,
    required this.value,
    required this.onChanged,
  });

  final ScanField field;
  final String value;
  final ValueChanged<String> onChanged;

  @override
  State<_FieldInput> createState() => _FieldInputState();
}

class _FieldInputState extends State<_FieldInput> {
  late TextEditingController _controller;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(text: widget.value);
  }

  @override
  void didUpdateWidget(_FieldInput old) {
    super.didUpdateWidget(old);
    if (old.value != widget.value && _controller.text != widget.value) {
      _controller.text = widget.value;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final colorScheme = theme.colorScheme;
    final level = widget.field.confidenceLevel;

    final labelColor = level == ConfidenceLevel.low
        ? colorScheme.error
        : level == ConfidenceLevel.medium
        ? Colors.amber.shade700
        : colorScheme.onSurface;

    final fieldLabel = _prettifyLabel(widget.field.name);
    final isDate = _dateFields.contains(widget.field.name);
    final isMoney = _moneyFields.contains(widget.field.name);
    final isInt = _intFields.contains(widget.field.name);

    // Date fields open a calendar picker so the landlord taps a date instead of
    // typing one (design#5). Free-text editing is still allowed as a fallback.
    if (isDate) {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _FieldLabel(label: fieldLabel, level: level, labelColor: labelColor),
          TextFormField(
            controller: _controller,
            onChanged: widget.onChanged,
            keyboardType: TextInputType.datetime,
            decoration: InputDecoration(
              border: const OutlineInputBorder(),
              hintText: 'YYYY-MM-DD',
              contentPadding: const EdgeInsets.symmetric(
                horizontal: 12,
                vertical: 10,
              ),
              suffixIcon: IconButton(
                icon: const Icon(Icons.calendar_today_outlined, size: 20),
                tooltip: 'Pick a date',
                onPressed: _pickDate,
              ),
              enabledBorder: _borderForLevel(level, colorScheme),
            ),
          ),
        ],
      );
    }

    // Category field gets a dropdown
    if (widget.field.name == 'category') {
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _FieldLabel(label: fieldLabel, level: level, labelColor: labelColor),
          DropdownButtonFormField<String>(
            initialValue: _scheduleECategories.contains(widget.value)
                ? widget.value
                : null,
            hint: const Text('Select category'),
            isExpanded: true,
            decoration: const InputDecoration(
              border: OutlineInputBorder(),
              contentPadding: EdgeInsets.symmetric(
                horizontal: 12,
                vertical: 10,
              ),
            ),
            items: _scheduleECategories
                .map((c) => DropdownMenuItem(value: c, child: Text(c)))
                .toList(),
            onChanged: (v) {
              if (v != null) {
                _controller.text = v;
                widget.onChanged(v);
              }
            },
          ),
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _FieldLabel(label: fieldLabel, level: level, labelColor: labelColor),
        TextFormField(
          controller: _controller,
          onChanged: widget.onChanged,
          // Money fields get a decimal number pad so amounts are easy to enter
          // on a phone (design#5); integer fields get a plain number pad.
          keyboardType: isMoney
              ? const TextInputType.numberWithOptions(decimal: true)
              : isInt
              ? TextInputType.number
              : null,
          decoration: InputDecoration(
            border: const OutlineInputBorder(),
            contentPadding: const EdgeInsets.symmetric(
              horizontal: 12,
              vertical: 10,
            ),
            enabledBorder: _borderForLevel(level, colorScheme),
          ),
        ),
      ],
    );
  }

  /// Confidence-tinted border for an input, or null for high confidence.
  InputBorder? _borderForLevel(ConfidenceLevel level, ColorScheme colorScheme) {
    if (level == ConfidenceLevel.low) {
      return OutlineInputBorder(
        borderSide: BorderSide(color: colorScheme.error),
      );
    }
    if (level == ConfidenceLevel.medium) {
      return OutlineInputBorder(
        borderSide: BorderSide(color: Colors.amber.shade600),
      );
    }
    return null;
  }

  /// Opens a calendar picker, seeding it from the current value when parseable,
  /// and writes the chosen date back as an ISO `yyyy-MM-dd` string.
  Future<void> _pickDate() async {
    final current = DateTime.tryParse(_controller.text.trim());
    final now = DateTime.now();
    final picked = await showDatePicker(
      context: context,
      initialDate: current ?? now,
      firstDate: DateTime(2000),
      lastDate: DateTime(now.year + 5),
    );
    if (picked == null) return;
    final iso =
        '${picked.year.toString().padLeft(4, '0')}-'
        '${picked.month.toString().padLeft(2, '0')}-'
        '${picked.day.toString().padLeft(2, '0')}';
    _controller.text = iso;
    widget.onChanged(iso);
  }
}

class _FieldLabel extends StatelessWidget {
  const _FieldLabel({
    required this.label,
    required this.level,
    required this.labelColor,
  });

  final String label;
  final ConfidenceLevel level;
  final Color labelColor;

  @override
  Widget build(BuildContext context) {
    // Plain-language explanation of what a confidence badge means, so a non-
    // technical landlord knows to double-check it (design#14).
    final hint = level == ConfidenceLevel.low
        ? 'The computer is not sure about this — please double-check it.'
        : 'The computer is fairly sure, but give this a quick look.';

    return Padding(
      padding: const EdgeInsets.only(bottom: 4),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Text(
                label,
                style: TextStyle(
                  fontSize: 12,
                  fontWeight: FontWeight.w500,
                  color: labelColor,
                ),
              ),
              if (level != ConfidenceLevel.high) ...[
                const SizedBox(width: 6),
                Tooltip(
                  message: hint,
                  triggerMode: TooltipTriggerMode.tap,
                  child: Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 6,
                      vertical: 1,
                    ),
                    decoration: BoxDecoration(
                      color: level == ConfidenceLevel.low
                          ? Colors.red.shade100
                          : Colors.amber.shade100,
                      borderRadius: BorderRadius.circular(4),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(
                          level == ConfidenceLevel.low
                              ? 'Low confidence'
                              : 'Medium confidence',
                          style: TextStyle(
                            fontSize: 10,
                            color: level == ConfidenceLevel.low
                                ? Colors.red.shade800
                                : Colors.amber.shade800,
                          ),
                        ),
                        const SizedBox(width: 3),
                        Icon(
                          Icons.info_outline,
                          size: 11,
                          color: level == ConfidenceLevel.low
                              ? Colors.red.shade800
                              : Colors.amber.shade800,
                        ),
                      ],
                    ),
                  ),
                ),
              ],
            ],
          ),
          // For low confidence, also spell out the hint inline — tooltips are
          // easy to miss on touch, and this is the field most likely wrong.
          if (level == ConfidenceLevel.low)
            Padding(
              padding: const EdgeInsets.only(top: 2),
              child: Text(
                hint,
                style: TextStyle(fontSize: 10.5, color: Colors.red.shade700),
              ),
            ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _ManualEntrySection — shown when no fields were extracted
// ---------------------------------------------------------------------------

class _ManualEntrySection extends StatelessWidget {
  const _ManualEntrySection({
    required this.editedFields,
    required this.onFieldChanged,
  });

  final Map<String, String> editedFields;
  final void Function(String, String) onFieldChanged;

  static const _manualFields = [
    'vendor_name',
    'total',
    'subtotal',
    'tax',
    'transaction_date',
    'category',
    'payment_method',
    'notes',
  ];

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'ENTER DETAILS',
            style: Theme.of(context).textTheme.labelSmall?.copyWith(
              fontWeight: FontWeight.w700,
              letterSpacing: 0.8,
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          ..._manualFields.map((name) {
            // Synthesise a placeholder ScanField with high confidence.
            final field = ScanField(
              name: name,
              value: editedFields[name] ?? '',
              confidence: 1.0,
            );
            return Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: _FieldInput(
                field: field,
                value: editedFields[name] ?? '',
                onChanged: (v) => onFieldChanged(name, v),
              ),
            );
          }),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _LineItemsSection — read-only
// ---------------------------------------------------------------------------

class _LineItemsSection extends StatelessWidget {
  const _LineItemsSection({required this.items});

  final List<ScanLineItem> items;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'LINE ITEMS',
            style: theme.textTheme.labelSmall?.copyWith(
              fontWeight: FontWeight.w700,
              letterSpacing: 0.8,
              color: theme.colorScheme.onSurfaceVariant,
            ),
          ),
          const SizedBox(height: 8),
          Table(
            columnWidths: const {
              0: FlexColumnWidth(3),
              1: FlexColumnWidth(1),
              2: FlexColumnWidth(1.5),
              3: FlexColumnWidth(1.5),
            },
            children: [
              TableRow(
                decoration: BoxDecoration(
                  color: theme.colorScheme.surfaceContainerHighest,
                ),
                children: const [
                  _TableHeaderCell(text: 'Description'),
                  _TableHeaderCell(text: 'Qty', align: TextAlign.right),
                  _TableHeaderCell(text: 'Unit', align: TextAlign.right),
                  _TableHeaderCell(text: 'Amount', align: TextAlign.right),
                ],
              ),
              ...items.map(
                (item) => TableRow(
                  children: [
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child: Text(
                        item.description ?? '',
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child: Text(
                        _fmtNum(item.quantity),
                        textAlign: TextAlign.right,
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child: Text(
                        _fmtMoney(item.unitPrice),
                        textAlign: TextAlign.right,
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                    Padding(
                      padding: const EdgeInsets.all(8),
                      child: Text(
                        _fmtMoney(item.amount),
                        textAlign: TextAlign.right,
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }

  String _fmtNum(double? v) => v == null ? '' : v.toStringAsFixed(0);
  String _fmtMoney(double? v) => v == null ? '' : v.toStringAsFixed(2);
}

class _TableHeaderCell extends StatelessWidget {
  const _TableHeaderCell({required this.text, this.align = TextAlign.left});

  final String text;
  final TextAlign align;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
      child: Text(
        text,
        textAlign: align,
        style: const TextStyle(fontSize: 11, fontWeight: FontWeight.w700),
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _PaidToggle — Expense paid/unpaid selector
// ---------------------------------------------------------------------------

class _PaidToggle extends StatelessWidget {
  const _PaidToggle({
    required this.isPaid,
    required this.dueDate,
    required this.onChanged,
  });

  final bool isPaid;
  final String? dueDate;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 0, 16, 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Payment status',
            style: theme.textTheme.labelLarge?.copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          const SizedBox(height: 8),
          SegmentedButton<bool>(
            segments: [
              const ButtonSegment(
                value: true,
                icon: Icon(Icons.check_circle_outline, size: 16),
                label: Text('Already paid'),
              ),
              ButtonSegment(
                value: false,
                icon: const Icon(Icons.schedule, size: 16),
                label: Text(
                  dueDate != null && dueDate!.isNotEmpty
                      ? 'Unpaid — due $dueDate'
                      : 'Unpaid bill',
                ),
              ),
            ],
            selected: {isPaid},
            onSelectionChanged: (s) => onChanged(s.first),
          ),
        ],
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _Banner — coloured informational strip
// ---------------------------------------------------------------------------

class _Banner extends StatelessWidget {
  const _Banner({
    required this.color,
    required this.borderColor,
    required this.textColor,
    required this.child,
  });

  final Color color;
  final Color borderColor;
  final Color textColor;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.all(16),
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: color,
        border: Border.all(color: borderColor),
        borderRadius: BorderRadius.circular(8),
      ),
      child: DefaultTextStyle(
        style: TextStyle(fontSize: 13, color: textColor),
        child: child,
      ),
    );
  }
}

// ---------------------------------------------------------------------------
// _StatusChip
// ---------------------------------------------------------------------------

class _StatusChip extends StatelessWidget {
  const _StatusChip({required this.status});

  final String status;

  String _label(String s) {
    switch (s) {
      case 'Pending':
        return 'Processing';
      case 'Reviewing':
        return 'Ready to review';
      case 'Confirmed':
        return 'Confirmed';
      case 'Failed':
        return 'Failed';
      case 'Rejected':
        return 'Rejected';
      default:
        return s;
    }
  }

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    final (bg, fg) = switch (status) {
      'Pending' => (Colors.amber.shade100, Colors.amber.shade900),
      'Reviewing' => (Colors.blue.shade100, Colors.blue.shade900),
      'Confirmed' => (Colors.green.shade100, Colors.green.shade900),
      'Failed' || 'Rejected' => (cs.errorContainer, cs.onErrorContainer),
      _ => (cs.surfaceContainerHighest, cs.onSurface),
    };

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        _label(status),
        style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600, color: fg),
      ),
    );
  }
}
