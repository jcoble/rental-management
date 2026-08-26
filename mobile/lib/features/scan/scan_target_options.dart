import 'package:flutter/material.dart';

/// One server-supported destination for document extraction.
///
/// [value] is the exact `targetEntityType` sent to the API. An empty value is
/// the canonical Auto/classify request; the server persists the classifier's
/// result before the draft becomes reviewable.
class ScanTargetOption {
  const ScanTargetOption({
    required this.value,
    required this.label,
    required this.description,
    required this.icon,
    required this.capabilities,
  });

  final String value;
  final String label;
  final String description;
  final IconData icon;
  final Set<String> capabilities;

  bool isAllowed(Set<String> granted) => capabilities.any(granted.contains);
}

const scanTargetOptions = <ScanTargetOption>[
  ScanTargetOption(
    value: '',
    label: 'Auto',
    description: 'Let Rental Command classify the document.',
    icon: Icons.auto_awesome_outlined,
    capabilities: {
      'money.expenses.manage',
      'money.payments.manage',
      'work.manage',
      'rentals.manage',
      'leasing.agreements.prepare',
      'leasing.applications.manage',
    },
  ),
  ScanTargetOption(
    value: 'LeaseEndingNotice',
    label: 'Move-out notice',
    description: 'Attach a notice and start the move-out workflow.',
    icon: Icons.event_busy_outlined,
    capabilities: {'rentals.manage'},
  ),
  ScanTargetOption(
    value: 'Expense',
    label: 'Receipt or bill',
    description: 'Create an expense and keep the source document.',
    icon: Icons.receipt_long_outlined,
    capabilities: {'money.expenses.manage'},
  ),
  ScanTargetOption(
    value: 'Payment',
    label: 'Rent payment',
    description: 'Record a payment on the selected tenant balance.',
    icon: Icons.payments_outlined,
    capabilities: {'money.payments.manage'},
  ),
  ScanTargetOption(
    value: 'WorkOrder',
    label: 'Maintenance',
    description: 'Create or update an authorized repair.',
    icon: Icons.home_repair_service_outlined,
    capabilities: {'work.manage', 'maintenance.assigned-work.update'},
  ),
  ScanTargetOption(
    value: 'LeaseAgreement',
    label: 'Signed lease',
    description: 'Import lease terms for review before creating a lease.',
    icon: Icons.description_outlined,
    capabilities: {'rentals.manage', 'leasing.agreements.prepare'},
  ),
  ScanTargetOption(
    value: 'Application',
    label: 'Application',
    description: 'Import a completed paper rental application.',
    icon: Icons.assignment_ind_outlined,
    capabilities: {'leasing.applications.manage'},
  ),
  ScanTargetOption(
    value: 'Loan',
    label: 'Loan statement',
    description: 'Read a mortgage statement or closing disclosure.',
    icon: Icons.account_balance_outlined,
    capabilities: {'money.expenses.manage'},
  ),
];

List<ScanTargetOption> allowedScanTargets(Set<String> capabilities) =>
    scanTargetOptions
        .where((option) => option.isAllowed(capabilities))
        .toList(growable: false);

ScanTargetOption scanTargetFor(String value) => scanTargetOptions.firstWhere(
  (option) => option.value == value,
  orElse: () => scanTargetOptions.first,
);
