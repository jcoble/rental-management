import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/work_order.dart';
import 'package:rental_command/features/maintenance/work_orders_repository.dart';
import 'package:rental_command/features/money/expense_form_sheet.dart';
import 'package:rental_command/features/money/expense_models.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/vendors/vendors_models.dart';
import 'package:rental_command/features/vendors/vendors_repository.dart';

void main() {
  testWidgets('expense form autofills from a linked work order and vendor', (
    tester,
  ) async {
    final moneyRepo = _FakeMoneyRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          moneyRepositoryProvider.overrideWithValue(moneyRepo),
          workOrdersRepositoryProvider.overrideWithValue(
            _FakeWorkOrdersRepository(),
          ),
          vendorsRepositoryProvider.overrideWithValue(_FakeVendorsRepository()),
        ],
        child: MaterialApp(
          home: Consumer(
            builder: (context, ref, _) => Scaffold(
              body: Center(
                child: ElevatedButton(
                  onPressed: () => showCreateExpenseSheet(context, ref),
                  child: const Text('Open expense form'),
                ),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('Open expense form'));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const Key('expense-work-order-field')));
    await tester.pumpAndSettle();
    await tester.tap(find.textContaining('Kitchen sink leak').last);
    await tester.pumpAndSettle();

    expect(find.text('Akron Plumbing'), findsAtLeastNWidgets(1));

    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    final descField = tester.widget<TextFormField>(
      find.byKey(const Key('expense-description-field')),
    );
    final amountField = tester.widget<TextFormField>(
      find.byKey(const Key('expense-amount-field')),
    );
    expect(descField.controller?.text, 'Kitchen sink leak');
    expect(amountField.controller?.text, '185.75');

    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Save expense'));
    await tester.pumpAndSettle();

    expect(moneyRepo.createdData?['description'], 'Kitchen sink leak');
    expect(moneyRepo.createdData?['amount'], 185.75);
    expect(moneyRepo.createdData?['propertyId'], 7);
    expect(moneyRepo.createdData?['workOrderId'], 17);
    expect(moneyRepo.createdData?['vendorId'], 8);
  });
}

class _FakeMoneyRepository extends MoneyRepository {
  _FakeMoneyRepository() : super(Dio());

  Map<String, dynamic>? createdData;

  @override
  Future<Expense> createExpense(Map<String, dynamic> data) async {
    createdData = Map<String, dynamic>.from(data);
    return Expense(
      id: 101,
      portfolioId: 1,
      propertyId: (data['propertyId'] as num?)?.toInt(),
      vendorId: (data['vendorId'] as num?)?.toInt(),
      workOrderId: (data['workOrderId'] as num?)?.toInt(),
      category: ScheduleECategory.fromWire(data['category'] as String?),
      description: data['description'] as String? ?? '',
      status: ExpenseStatus.fromWire(data['status'] as String?),
      amount: (data['amount'] as num?)?.toDouble() ?? 0,
      incurredAt: DateTime.parse(data['incurredAt'] as String),
      paidAt: DateTime.tryParse(data['paidAt'] as String? ?? ''),
      billableToOwner: data['billableToOwner'] as bool? ?? false,
      hasReceipt: false,
      receiptIsImage: false,
      lineItems: const [],
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    );
  }
}

class _FakeWorkOrdersRepository extends WorkOrdersRepository {
  _FakeWorkOrdersRepository() : super(Dio());

  @override
  Future<List<WorkOrder>> listWorkOrders({
    int? propertyId,
    int? unitId,
    int? vendorId,
    bool openOnly = false,
    int take = 50,
    String sort = '-updatedAt',
  }) async {
    expect(take, lessThanOrEqualTo(100));
    expect(sort, '-updatedAt');
    return [
      WorkOrder(
        id: 17,
        portfolioId: 1,
        propertyId: 7,
        unitId: 22,
        vendorId: 8,
        title: 'Kitchen sink leak',
        description: 'Water under the cabinet',
        category: 'Plumbing',
        priority: 'High',
        status: 'Completed',
        requestedAt: DateTime(2026, 6, 1),
        completedAt: DateTime(2026, 6, 2),
        actualCost: 185.75,
        updatedAt: DateTime(2026, 6, 2),
        propertyName: 'Maple Ridge',
        unitNumber: '4B',
        vendorName: 'Akron Plumbing',
      ),
    ];
  }
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
      taxId: '12-3456789',
    ),
  ];
}
