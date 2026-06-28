import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/features/vendors/vendors_list_screen.dart';
import 'package:rental_command/features/vendors/vendors_models.dart';
import 'package:rental_command/features/vendors/vendors_repository.dart';

void main() {
  testWidgets('vendors screen exposes native add, edit, and delete actions', (
    tester,
  ) async {
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          vendorsRepositoryProvider.overrideWithValue(_FakeVendorsRepository()),
        ],
        child: const MaterialApp(home: VendorsListScreen()),
      ),
    );
    await tester.pumpAndSettle();

    expect(find.byTooltip('Add vendor'), findsOneWidget);
    expect(find.byTooltip('Edit vendor'), findsOneWidget);
    expect(find.byTooltip('Delete vendor'), findsOneWidget);
    expect(find.textContaining('Add vendors on the web'), findsNothing);
  });

  test('mobile vendors repository exposes create, update, and delete endpoints', () {
    final source = File(
      'lib/features/vendors/vendors_repository.dart',
    ).readAsStringSync();

    expect(source, contains('Future<Vendor> createVendor('));
    expect(
      source,
      contains("_dio.post<Map<String, dynamic>>('/vendors', data: data)"),
    );
    expect(source, contains('Future<Vendor> updateVendor('));
    expect(source, contains("_dio.patch<Map<String, dynamic>>("));
    expect(source, contains("'/vendors/\$id'"));
    expect(source, contains('Future<void> deleteVendor('));
    expect(source, contains("_dio.delete<dynamic>('/vendors/\$id')"));
  });
}

class _FakeVendorsRepository extends VendorsRepository {
  _FakeVendorsRepository() : super(Dio());

  @override
  Future<List<Vendor>> list() async => const [
    Vendor(
      id: 8,
      name: 'Akron Plumbing',
      serviceType: 'Plumbing',
      phone: '330-555-0199',
      jobsCompleted: 3,
    ),
  ];
}
