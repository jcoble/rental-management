import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('requests server-paged Unit inspections', () {
    final repository = File(
      'lib/features/inspections/inspections_repository.dart',
    ).readAsStringSync();
    final screen = File(
      'lib/features/units/unit_command_center_screen.dart',
    ).readAsStringSync();

    expect(repository, contains("'/inspections/page'"));
    expect(repository, contains("'unitId': query.unitId"));
    expect(screen, contains('InspectionListQuery('));
    expect(screen, contains('unitId: widget.unitId'));
  });

  test('requests server-paged Unit recurring maintenance', () {
    final repository = File(
      'lib/features/recurring_maintenance/recurring_maintenance_repository.dart',
    ).readAsStringSync();
    final screen = File(
      'lib/features/units/unit_command_center_screen.dart',
    ).readAsStringSync();

    expect(repository, contains("'/recurring-maintenance/page'"));
    expect(repository, contains("'unitId': query.unitId"));
    expect(screen, contains('RecurringMaintenanceListQuery('));
    expect(screen, contains('unitId: widget.unitId'));
  });
}
