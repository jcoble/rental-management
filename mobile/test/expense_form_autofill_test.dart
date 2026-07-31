import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/property.dart';
import 'package:rental_command/core/models/work_order.dart';
import 'package:rental_command/core/time/app_clock.dart';
import 'package:rental_command/features/maintenance/work_orders_repository.dart';
import 'package:rental_command/features/money/expense_form_sheet.dart';
import 'package:rental_command/features/money/expense_models.dart';
import 'package:rental_command/features/money/money_repository.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
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
          appNowProvider.overrideWith(
            (ref) async => DateTime.utc(2026, 6, 3, 5),
          ),
          moneyRepositoryProvider.overrideWithValue(moneyRepo),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
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
    expect(moneyRepo.createdData?['operationalScope'], 'WorkOrder');
    expect(moneyRepo.createdData?['propertyId'], 7);
    expect(moneyRepo.createdData?['unitId'], 22);
    expect(moneyRepo.createdData?['workOrderId'], 17);
    expect(moneyRepo.createdData?['vendorId'], 8);
    expect(moneyRepo.createdData?['lineItems'], [
      {
        'description': 'Kitchen sink leak',
        'quantity': 1,
        'unitPrice': 185.75,
        'amount': 185.75,
        'lineNumber': 1,
      },
    ]);
  });

  testWidgets('manual property-only expense submits complete bill fields', (
    tester,
  ) async {
    final moneyRepo = _FakeMoneyRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          appNowProvider.overrideWith(
            (ref) async => DateTime.utc(2027, 2, 15, 5),
          ),
          moneyRepositoryProvider.overrideWithValue(moneyRepo),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
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

    await tester.tap(find.byKey(const Key('expense-property-field-0')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Zenith Duplex Property 26').last);
    await tester.pumpAndSettle();

    tester
        .widget<DropdownButtonFormField<ScheduleECategory>>(
          find.byKey(const Key('expense-category-field')),
        )
        .onChanged
        ?.call(ScheduleECategory.utilities);
    await tester.pumpAndSettle();

    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('expense-description-field')),
      'City Water & Sewer',
    );
    await tester.enterText(
      find.byKey(const Key('expense-amount-field')),
      '175',
    );

    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('No due date'));
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Next month'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('2').last);
    await tester.tap(find.text('OK'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('expense-receipt-number-field')),
      'EXP-20270215-1045',
    );
    await tester.enterText(
      find.byKey(const Key('expense-subtotal-field')),
      '175',
    );
    await tester.enterText(find.byKey(const Key('expense-tax-field')), '0');
    await tester.enterText(
      find.byKey(const Key('expense-line-description-field')),
      'City Water & Sewer service',
    );
    await tester.enterText(
      find.byKey(const Key('expense-line-unit-price-field')),
      '175',
    );
    await tester.enterText(
      find.byKey(const Key('expense-line-amount-field')),
      '175',
    );
    await tester.enterText(
      find.byKey(const Key('expense-payment-method-field')),
      'ACH',
    );

    await tester.tap(find.text('Save expense'));
    await tester.pumpAndSettle();

    final data = moneyRepo.createdData!;
    expect(data['operationalScope'], 'Property');
    expect(data['propertyId'], 26);
    expect(data.containsKey('unitId'), isFalse);
    expect(data.containsKey('workOrderId'), isFalse);
    expect(data['description'], 'City Water & Sewer');
    expect(data['amount'], 175);
    expect(data['category'], 'Utilities');
    expect(data['status'], 'Paid');
    expect(DateTime.parse(data['incurredAt'] as String), DateTime(2027, 2, 15));
    expect(DateTime.parse(data['paidAt'] as String), DateTime(2027, 2, 15));
    expect(DateTime.parse(data['dueDate'] as String), DateTime(2027, 3, 2));
    expect(data['paymentMethod'], 'ACH');
    expect(data['documentKind'], 'ManualBill');
    expect(data['subtotal'], 175);
    expect(data['taxAmount'], 0);
    expect(data['lineItems'], [
      {
        'description': 'City Water & Sewer service',
        'quantity': 1,
        'unitPrice': 175,
        'amount': 175,
        'lineNumber': 1,
      },
    ]);
    expect(data['receiptData'] as String, contains('EXP-20270215-1045'));
  });

  testWidgets('manual expense requires property before saving', (tester) async {
    final moneyRepo = _FakeMoneyRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          appNowProvider.overrideWith(
            (ref) async => DateTime.utc(2027, 2, 15, 5),
          ),
          moneyRepositoryProvider.overrideWithValue(moneyRepo),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
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
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    expect(find.text('Property is required'), findsOneWidget);
    expect(moneyRepo.createdData, isNull);
  });

  testWidgets(
    'manual expense dates default from app clock and submit selected business dates',
    (tester) async {
      final moneyRepo = _FakeMoneyRepository();

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            appNowProvider.overrideWith(
              (ref) async => DateTime.utc(2027, 1, 18, 5),
            ),
            moneyRepositoryProvider.overrideWithValue(moneyRepo),
            propertiesRepositoryProvider.overrideWithValue(
              _FakePropertiesRepository(),
            ),
            workOrdersRepositoryProvider.overrideWithValue(
              _FakeWorkOrdersRepository(),
            ),
            vendorsRepositoryProvider.overrideWithValue(
              _FakeVendorsRepository(),
            ),
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

      await tester.tap(find.byKey(const Key('expense-property-field-0')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Zenith Duplex Property 26').last);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byKey(const Key('expense-description-field')),
        'Jan simulated repair',
      );
      await tester.enterText(
        find.byKey(const Key('expense-amount-field')),
        '42',
      );

      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();

      expect(find.text('Incurred date'), findsOneWidget);
      expect(find.text('Paid date'), findsOneWidget);
      expect(find.text('Jan 18, 2027'), findsNWidgets(2));

      await tester.tap(find.text('Jan 18, 2027').first);
      await tester.pumpAndSettle();
      await tester.tap(find.text('20').last);
      await tester.tap(find.text('OK'));
      await tester.pumpAndSettle();

      expect(find.text('Jan 20, 2027'), findsNWidgets(2));

      await tester.tap(find.text('Next'));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byKey(const Key('expense-line-description-field')),
        'Jan simulated repair',
      );
      await tester.enterText(
        find.byKey(const Key('expense-line-amount-field')),
        '42',
      );
      await tester.tap(find.text('Save expense'));
      await tester.pumpAndSettle();

      expect(moneyRepo.createdData?['description'], 'Jan simulated repair');
      expect(moneyRepo.createdData?['amount'], 42);
      expect(
        DateTime.parse(moneyRepo.createdData?['incurredAt'] as String),
        DateTime(2027, 1, 20),
      );
      expect(
        DateTime.parse(moneyRepo.createdData?['paidAt'] as String),
        DateTime(2027, 1, 20),
      );
    },
  );
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

class _FakePropertiesRepository extends PropertiesRepository {
  _FakePropertiesRepository() : super(Dio());

  @override
  Future<List<Property>> listProperties({
    bool availableForLease = false,
  }) async {
    return [
      Property(
        id: 26,
        portfolioId: 1,
        name: 'Zenith Duplex Property 26',
        type: 'Duplex',
        rentalStructure: RentalStructure.multiRental,
        status: 'Active',
        addressLine1: '26 Zenith Way',
        city: 'Akron',
        state: 'OH',
        postalCode: '44308',
        createdAt: DateTime(2026),
        updatedAt: DateTime(2026),
      ),
      Property(
        id: 7,
        portfolioId: 1,
        name: 'Maple Ridge',
        type: 'Apartment',
        rentalStructure: RentalStructure.multiRental,
        status: 'Active',
        addressLine1: '7 Maple Ridge',
        city: 'Akron',
        state: 'OH',
        postalCode: '44308',
        createdAt: DateTime(2026),
        updatedAt: DateTime(2026),
      ),
    ];
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
    int skip = 0,
    int take = 50,
    String? search,
    String sort = '-updatedAt',
    String? requestedFrom,
    String? requestedTo,
    String? scheduledFrom,
    String? scheduledTo,
    String? completedFrom,
    String? completedTo,
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
  Future<List<Vendor>> list({
    int skip = 0,
    int take = 50,
    String sort = 'name',
  }) async => const [
    Vendor(
      id: 8,
      name: 'Akron Plumbing',
      serviceType: 'Plumbing',
      phone: '330-555-0199',
      taxId: '12-3456789',
    ),
  ];
}
