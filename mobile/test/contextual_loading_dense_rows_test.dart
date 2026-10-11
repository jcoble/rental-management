import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/inspection.dart';
import 'package:rental_command/core/models/lease.dart';
import 'package:rental_command/core/widgets/mobile_m3_list.dart';
import 'package:rental_command/features/inspections/inspections_list_screen.dart';
import 'package:rental_command/features/inspections/inspections_repository.dart';
import 'package:rental_command/features/leases/leases_list_screen.dart';
import 'package:rental_command/features/leases/leases_repository.dart';

void main() {
  testWidgets(
    'pending inspections preserve list chrome and show contextual structure',
    (tester) async {
      final pending = Completer<InspectionListPage>();

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            inspectionsPageProvider.overrideWith(
              (ref, query) => pending.future,
            ),
          ],
          child: const MaterialApp(home: InspectionsListScreen()),
        ),
      );
      await tester.pump();

      expect(find.text('Inspections'), findsOneWidget);
      expect(find.byType(SearchBar), findsOneWidget);
      expect(find.byTooltip('Sort and filter'), findsOneWidget);
      expect(find.byKey(const Key('inspections-loading')), findsOneWidget);
      expect(find.byType(MobileM3ListItem), findsOneWidget);
      expect(find.text('Loading inspections'), findsOneWidget);
      expect(find.text('Property, unit, schedule, and status'), findsOneWidget);

      final progress = find.byType(CircularProgressIndicator);
      expect(progress, findsOneWidget);
      expect(tester.getSize(progress), const Size.square(22));
    },
  );

  testWidgets('inspection property and unit identity may wrap twice', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(360, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    const propertyName =
        'The Residences at North Market Historic District Building';
    const unitNumber = 'Penthouse Apartment 1204 East';
    final inspection = Inspection(
      id: 1,
      portfolioId: 1,
      propertyId: 2,
      unitId: 3,
      type: 'Routine',
      status: 'Reviewed',
      scheduledFor: DateTime(2026, 7, 30),
      propertyName: propertyName,
      unitNumber: unitNumber,
      createdAt: DateTime(2026, 7, 1),
      updatedAt: DateTime(2026, 7, 1),
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          inspectionsPageProvider.overrideWith(
            (ref, query) async => InspectionListPage(
              items: [inspection],
              totalCount: 1,
              skip: query.skip,
              take: query.take,
            ),
          ),
        ],
        child: const MaterialApp(home: InspectionsListScreen()),
      ),
    );
    await tester.pump();
    await tester.pump();

    final identity = tester.widget<Text>(
      find.text('$propertyName  ·  Unit $unitNumber'),
    );
    expect(identity.maxLines, 2);
    expect(find.text('Reviewed'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('lease tenant or relationship identity may wrap twice', (
    tester,
  ) async {
    await tester.binding.setSurfaceSize(const Size(360, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    const tenantName =
        'Alexandria Catherine Montgomery-Worthington and Household';
    final relationship = LeaseManagementSummary(
      id: 11,
      publicId: 'lease-management-11',
      relationshipNumber: 'RELATIONSHIP-2026-000000011',
      propertyId: 2,
      propertyName: 'North Market Apartments',
      unitId: 3,
      unitNumber: '1204 East',
      lifecycle: 'Occupied',
      businessDate: DateTime(2026, 7, 25),
      primaryTenantName: tenantName,
      currentPartyCount: 2,
      currentResidentCount: 2,
      hasReconciliationException: false,
      updatedAt: DateTime(2026, 7, 25),
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          leaseManagementsPageProvider.overrideWith(
            (ref, query) async => LeaseManagementListPage(
              items: [relationship],
              totalCount: 1,
              skip: query.skip,
              take: query.take,
            ),
          ),
        ],
        child: const MaterialApp(home: LeasesListScreen()),
      ),
    );
    await tester.pump();
    await tester.pump();

    final identity = tester.widget<Text>(find.text(tenantName));
    expect(identity.maxLines, 2);
    expect(find.widgetWithText(Chip, 'Occupied'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
