import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/payments/payment_detail_screen.dart';
import 'package:rental_command/features/payments/payments_repository.dart';

void main() {
  testWidgets('payment edit can move payment to another property/unit lease', (
    tester,
  ) async {
    final repo = _FakePaymentsRepository(
      payment: _payment(leaseId: 10, propertyName: 'Maple Ridge', unit: '1A'),
      leases: [
        _lease(id: 10, propertyName: 'Maple Ridge', unit: '1A'),
        _lease(id: 22, propertyName: 'Oak Terrace', unit: '2B'),
      ],
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [paymentsRepositoryProvider.overrideWithValue(repo)],
        child: const MaterialApp(home: PaymentDetailScreen(paymentId: 8)),
      ),
    );

    await tester.pump();
    await tester.pump();

    expect(find.text('Maple Ridge · Unit 1A'), findsOneWidget);

    await tester.tap(find.text('Edit'));
    await tester.pumpAndSettle();

    expect(find.text('Lease'), findsWidgets);
    expect(find.text('Maple Ridge · Unit 1A — Jesse Coble'), findsOneWidget);

    await tester.tap(find.text('Maple Ridge · Unit 1A — Jesse Coble'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Oak Terrace · Unit 2B — Jesse Coble').last);
    await tester.pumpAndSettle();

    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    await tester.ensureVisible(find.text('Save'));
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(repo.lastUpdate?['leaseId'], 22);
  });
}

class _FakePaymentsRepository extends PaymentsRepository {
  _FakePaymentsRepository({required this.payment, required this.leases})
    : super(Dio());

  Payment payment;
  final List<Lease> leases;
  Map<String, dynamic>? lastUpdate;

  @override
  Future<Payment> getPayment(int id) async => payment;

  @override
  Future<List<Lease>> listLeases() async => leases;

  @override
  Future<Payment> updatePayment(int id, Map<String, dynamic> data) async {
    lastUpdate = data;
    final lease = leases.firstWhere((item) => item.id == data['leaseId']);
    payment = _payment(
      leaseId: lease.id,
      propertyName: lease.propertyName ?? '',
      unit: lease.unitNumber ?? '',
    );
    return payment;
  }
}

Payment _payment({
  required int leaseId,
  required String propertyName,
  required String unit,
}) {
  return Payment(
    id: 8,
    portfolioId: 1,
    leaseId: leaseId,
    type: 'Rent',
    status: 'Scheduled',
    amount: 1200,
    dueDate: DateTime(2026, 7),
    tenantName: 'Jesse Coble',
    leaseNumber: 'L-2026-0004',
    propertyName: propertyName,
    unitNumber: unit,
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}

Lease _lease({
  required int id,
  required String propertyName,
  required String unit,
}) {
  return Lease(
    id: id,
    portfolioId: 1,
    propertyId: 7,
    unitId: id + 100,
    tenantId: 3,
    leaseNumber: 'L-2026-$id',
    status: 'Active',
    startDate: DateTime(2026),
    endDate: DateTime(2027),
    monthlyRent: 1200,
    securityDeposit: 1200,
    lateFeeAmount: 50,
    rentDueDay: 1,
    tenantName: 'Jesse Coble',
    propertyName: propertyName,
    unitNumber: unit,
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}
