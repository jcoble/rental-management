import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/home/mobile_domain_navigation.dart';
import 'package:rental_command/features/leases/lease_detail_screen.dart';
import 'package:rental_command/features/leases/leases_repository.dart';
import 'package:rental_command/features/payments/payment_detail_screen.dart';
import 'package:rental_command/features/payments/payments_repository.dart';

void main() {
  tearDown(() {
    final current = MobileShellNavigationRegistry.current;
    if (current != null) {
      MobileShellNavigationRegistry.detach(current);
    }
  });

  testWidgets(
    'lease payment drill-in pushes over the current unit lease stack',
    (tester) async {
      final lease = _lease();
      final payment = _payment(id: 91, leaseId: lease.id);
      final shellCalls =
          <
            ({
              MobileShellTabId tab,
              MobileDestinationId? destination,
              MobileDetailBuilder? detailBuilder,
            })
          >[];

      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            leasesRepositoryProvider.overrideWithValue(_FakeLeasesRepository()),
            paymentsRepositoryProvider.overrideWithValue(
              _FakePaymentsRepository(payment),
            ),
          ],
          child: MaterialApp(
            home: MobileShellNavigation(
              controller: MobileShellNavigator(
                openTab: (tab, {destination, detailBuilder}) {
                  shellCalls.add((
                    tab: tab,
                    destination: destination,
                    detailBuilder: detailBuilder,
                  ));
                },
                openRoute: (_) => false,
              ),
              child: LeaseDetailScreen(lease: lease),
            ),
          ),
        ),
      );

      await tester.pump();
      await tester.pump();
      await tester.scrollUntilVisible(find.text(r'$1,234'), 260);
      await tester.tap(find.text(r'$1,234'));
      await tester.pumpAndSettle();

      expect(shellCalls, isEmpty);
      expect(find.byType(PaymentDetailScreen), findsOneWidget);

      await tester.pageBack();
      await tester.pumpAndSettle();

      expect(find.byType(LeaseDetailScreen), findsOneWidget);
      expect(find.text(r'$1,234'), findsOneWidget);
    },
  );
}

class _FakeLeasesRepository extends LeasesRepository {
  _FakeLeasesRepository() : super(Dio());

  @override
  Future<LeaseSignatureStatus> signatureStatus(int id) async {
    return LeaseSignatureStatus(
      leaseId: id,
      esignStatus: 'None',
      leaseStatus: 'Active',
      hasSignedDocument: false,
    );
  }
}

class _FakePaymentsRepository extends PaymentsRepository {
  _FakePaymentsRepository(this.payment) : super(Dio());

  final Payment payment;

  @override
  Future<Payment> getPayment(int id) async {
    return payment;
  }

  @override
  Future<List<Payment>> listPayments({
    int? leaseId,
    String sort = '-createdAt',
    String? dueFrom,
    String? dueTo,
    String? paidFrom,
    String? paidTo,
  }) async {
    return [payment];
  }
}

Lease _lease() {
  return Lease(
    id: 12,
    portfolioId: 1,
    propertyId: 7,
    unitId: 42,
    tenantId: 5,
    leaseNumber: 'L-12',
    status: 'Active',
    startDate: DateTime(2026),
    endDate: DateTime(2026, 12, 31),
    monthlyRent: 1200,
    securityDeposit: 1200,
    lateFeeAmount: 50,
    rentDueDay: 1,
    tenantName: 'Jesse Coble',
    propertyName: '123 Main',
    unitNumber: 'Unit A',
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}

Payment _payment({required int id, required int leaseId}) {
  return Payment(
    id: id,
    portfolioId: 1,
    leaseId: leaseId,
    type: 'Rent',
    status: 'Paid',
    amount: 1234,
    dueDate: DateTime(2026),
    paidDate: DateTime(2026),
    tenantName: 'Jesse Coble',
    leaseNumber: 'L-12',
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}
