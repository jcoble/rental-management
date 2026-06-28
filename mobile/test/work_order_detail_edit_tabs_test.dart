import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/maintenance/work_order_detail_screen.dart';
import 'package:rental_command/features/maintenance/work_orders_repository.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/tenants/tenants_repository.dart';
import 'package:rental_command/features/vendors/vendors_models.dart' as vendors;
import 'package:rental_command/features/vendors/vendors_repository.dart';

void main() {
  testWidgets(
    'work order edit form is tabbed and saves schedule and cost fields',
    (tester) async {
      final repo = _FakeWorkOrdersRepository();

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            workOrdersRepositoryProvider.overrideWithValue(repo),
            propertiesRepositoryProvider.overrideWithValue(
              _FakePropertiesRepository(),
            ),
            tenantsRepositoryProvider.overrideWithValue(
              _FakeTenantsRepository(),
            ),
            vendorsRepositoryProvider.overrideWithValue(
              _FakeVendorsRepository(),
            ),
          ],
          child: const MaterialApp(
            home: WorkOrderDetailScreen(workOrderId: 17),
          ),
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.byTooltip('Edit'));
      await tester.pumpAndSettle();

      expect(find.text('Edit Work Order'), findsOneWidget);
      final tabs = find.byType(TabBar);
      expect(
        find.descendant(of: tabs, matching: find.text('Details')),
        findsOneWidget,
      );
      expect(
        find.descendant(of: tabs, matching: find.text('Schedule')),
        findsOneWidget,
      );
      expect(
        find.descendant(of: tabs, matching: find.text('Costs')),
        findsOneWidget,
      );
      expect(
        find.descendant(of: tabs, matching: find.text('Assign')),
        findsOneWidget,
      );

      await tester.tap(find.text('Schedule'));
      await tester.pumpAndSettle();

      expect(find.text('Scheduled date (optional)'), findsOneWidget);
      expect(find.text('Start time'), findsOneWidget);

      await tester.tap(find.text('Costs'));
      await tester.pumpAndSettle();

      expect(find.text('Arrival window end'), findsOneWidget);

      await tester.enterText(
        find.byKey(const Key('work-order-estimated-cost-field')),
        '185.75',
      );
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      expect(find.text('Vendor'), findsOneWidget);

      await tester.tap(find.text('Save Changes'));
      await tester.pumpAndSettle();

      expect(repo.updatedData?['scheduledFor'], isA<String>());
      expect(repo.updatedData?['scheduledWindowEnd'], isA<String>());
      expect(repo.updatedData?['estimatedCost'], 185.75);
    },
  );
}

WorkOrder _workOrder() {
  return WorkOrder(
    id: 17,
    portfolioId: 1,
    propertyId: 7,
    unitId: 42,
    tenantId: 5,
    vendorId: 8,
    title: 'Kitchen sink leak',
    description: 'Water is dripping under the sink.',
    category: 'Plumbing',
    priority: 'High',
    status: 'Scheduled',
    requestedAt: DateTime.utc(2026, 6, 20, 13),
    scheduledFor: DateTime.utc(2026, 6, 21, 18),
    scheduledWindowEnd: DateTime.utc(2026, 6, 21, 20),
    estimatedCost: 125.50,
    createdBy: 'Staff',
    updatedAt: DateTime.utc(2026, 6, 20, 13),
    propertyName: 'Maple Ridge',
    unitNumber: '4B',
    tenantName: 'Jesse Coble',
    vendorName: 'Akron Plumbing',
  );
}

Property _property() {
  return Property(
    id: 7,
    portfolioId: 1,
    name: 'Maple Ridge',
    type: 'MultiFamily',
    status: 'Active',
    addressLine1: '123 Main St',
    city: 'Akron',
    state: 'OH',
    postalCode: '44319',
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}

Unit _unit() {
  return Unit(
    id: 42,
    propertyId: 7,
    unitNumber: '4B',
    bedrooms: 2,
    bathrooms: 1,
    marketRent: 1400,
    status: 'Occupied',
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}

Tenant _tenant() {
  return Tenant(
    id: 5,
    portfolioId: 1,
    firstName: 'Jesse',
    lastName: 'Coble',
    fullName: 'Jesse Coble',
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}

vendors.Vendor _vendor() {
  return const vendors.Vendor(
    id: 8,
    name: 'Akron Plumbing',
    serviceType: 'Plumbing',
  );
}

class _FakeWorkOrdersRepository extends WorkOrdersRepository {
  _FakeWorkOrdersRepository() : super(Dio());

  Map<String, dynamic>? updatedData;

  @override
  Future<WorkOrderDetail> getWorkOrderDetail(int id) async {
    return WorkOrderDetail(workOrder: _workOrder(), timeline: const []);
  }

  @override
  Future<WorkOrder> updateWorkOrder(int id, Map<String, dynamic> data) async {
    updatedData = Map<String, dynamic>.from(data);
    return _workOrder();
  }

  @override
  Future<List<Document>> listWorkOrderDocuments(int workOrderId) async => [];

  @override
  Future<Uint8List> downloadDocument(int documentId) async => Uint8List(0);

  @override
  Future<List<Property>> listProperties() async => [_property()];
}

class _FakePropertiesRepository extends PropertiesRepository {
  _FakePropertiesRepository() : super(Dio());

  @override
  Future<List<Unit>> listUnits(int propertyId) async => [_unit()];
}

class _FakeTenantsRepository extends TenantsRepository {
  _FakeTenantsRepository() : super(Dio());

  @override
  Future<List<Tenant>> listTenants() async => [_tenant()];
}

class _FakeVendorsRepository extends VendorsRepository {
  _FakeVendorsRepository() : super(Dio());

  @override
  Future<List<vendors.Vendor>> list() async => [_vendor()];
}
