import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/notices/notices_models.dart';
import 'package:rental_command/features/notices/notices_repository.dart';
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

  testWidgets('payment detail exposes delete from the detail toolbar', (
    tester,
  ) async {
    final repo = _FakePaymentsRepository(
      payment: _payment(leaseId: 10, propertyName: 'Maple Ridge', unit: '1A'),
      leases: [_lease(id: 10, propertyName: 'Maple Ridge', unit: '1A')],
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [paymentsRepositoryProvider.overrideWithValue(repo)],
        child: const MaterialApp(home: PaymentDetailScreen(paymentId: 8)),
      ),
    );

    await tester.pump();
    await tester.pump();

    await tester.tap(find.byTooltip('Delete payment'));
    await tester.pumpAndSettle();

    expect(find.text('Delete payment?'), findsOneWidget);

    await tester.tap(find.text('Delete').last);
    await tester.pumpAndSettle();

    expect(repo.deletedPaymentId, 8);
  });

  testWidgets('eligible payment can create a scoped notice or reminder', (
    tester,
  ) async {
    final paymentsRepo = _FakePaymentsRepository(
      payment: _payment(leaseId: 10, propertyName: 'Maple Ridge', unit: '1A'),
      leases: [_lease(id: 10, propertyName: 'Maple Ridge', unit: '1A')],
    );
    final noticesRepo = _FakeNoticesRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          paymentsRepositoryProvider.overrideWithValue(paymentsRepo),
          noticesRepositoryProvider.overrideWithValue(noticesRepo),
        ],
        child: const MaterialApp(home: PaymentDetailScreen(paymentId: 8)),
      ),
    );

    await tester.pump();
    await tester.pump();

    await tester.ensureVisible(find.text('Notice / reminder'));
    await tester.tap(find.text('Notice / reminder'));
    await tester.pumpAndSettle();

    expect(noticesRepo.lastTenantId, isNull);
    expect(noticesRepo.lastLeaseId, 10);
    expect(noticesRepo.lastPaymentId, 8);
    expect(noticesRepo.lastNoticeType, 'RentReminder');
    expect(find.text('Review notice'), findsOneWidget);
  });

  testWidgets('settled payment does not show notice or reminder action', (
    tester,
  ) async {
    final repo = _FakePaymentsRepository(
      payment: _payment(
        leaseId: 10,
        propertyName: 'Maple Ridge',
        unit: '1A',
        status: 'Paid',
      ),
      leases: [_lease(id: 10, propertyName: 'Maple Ridge', unit: '1A')],
    );

    await tester.pumpWidget(
      ProviderScope(
        overrides: [paymentsRepositoryProvider.overrideWithValue(repo)],
        child: const MaterialApp(home: PaymentDetailScreen(paymentId: 8)),
      ),
    );

    await tester.pump();
    await tester.pump();

    expect(find.text('Notice / reminder'), findsNothing);
  });
}

class _FakePaymentsRepository extends PaymentsRepository {
  _FakePaymentsRepository({required this.payment, required this.leases})
    : super(Dio());

  Payment payment;
  final List<Lease> leases;
  Map<String, dynamic>? lastUpdate;
  int? deletedPaymentId;

  @override
  Future<Payment> getPayment(int id) async => payment;

  @override
  Future<List<Lease>> listLeases() async => leases;

  @override
  Future<AccountingSummary> accountingSummary() async {
    return const AccountingSummary(
      portfolioId: 1,
      collected: 0,
      outstanding: 0,
      overdue: 0,
      overdueCount: 0,
      totalExpenses: 0,
      expensesByCategory: [],
      snapshot: MoneySnapshot(title: '', summary: '', bullets: []),
    );
  }

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

  @override
  Future<void> deletePayment(int id) async {
    deletedPaymentId = id;
  }
}

class _FakeNoticesRepository extends NoticesRepository {
  _FakeNoticesRepository() : super(Dio());

  int? lastTenantId;
  int? lastLeaseId;
  int? lastPaymentId;
  String? lastNoticeType;

  @override
  Future<List<NoticeDraft>> generateForTenant(
    int? tenantId, {
    int? leaseId,
    int? paymentId,
    String? noticeType,
  }) async {
    lastTenantId = tenantId;
    lastLeaseId = leaseId;
    lastPaymentId = paymentId;
    lastNoticeType = noticeType;
    return [
      NoticeDraft(
        id: 42,
        leaseId: leaseId ?? 0,
        paymentId: paymentId,
        tenantId: tenantId ?? 0,
        tenantName: 'Jesse Coble',
        noticeType: noticeType ?? 'RentReminder',
        status: 'Draft',
        subject: 'Upcoming rent reminder',
        body: 'Rent is due soon.',
        reason: '',
        triggerDate: DateTime(2026, 7),
      ),
    ];
  }
}

Payment _payment({
  required int leaseId,
  required String propertyName,
  required String unit,
  String type = 'Rent',
  String status = 'Scheduled',
  DateTime? dueDate,
}) {
  return Payment(
    id: 8,
    portfolioId: 1,
    leaseId: leaseId,
    type: type,
    status: status,
    amount: 1200,
    dueDate: dueDate ?? DateTime(2099, 7),
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
