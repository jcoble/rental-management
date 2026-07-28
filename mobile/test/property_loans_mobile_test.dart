import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/auth/auth_controller.dart';
import 'package:rental_command/core/auth/auth_models.dart';
import 'package:rental_command/core/models/models.dart';
import 'package:rental_command/features/properties/properties_repository.dart';
import 'package:rental_command/features/properties/property_detail_screen.dart';
import 'package:rental_command/features/properties/property_loans_repository.dart';

void main() {
  test(
    'property loans repository uses server-side property filtering',
    () async {
      final adapter = _RecordingAdapter(
        responseBody: jsonEncode([_loanJson()]),
      );
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = PropertyLoansRepository(dio);

      final loans = await repo.listLoans(propertyId: 7, take: 20);

      expect(adapter.method, 'GET');
      expect(adapter.path, '/loans');
      expect(adapter.query, containsPair('propertyId', 7));
      expect(adapter.query, containsPair('take', 20));
      expect(loans.single.lender, 'First Federal');
      expect(loans.single.currentBalance, 196500);
    },
  );

  test(
    'property loans repository exposes create, update, and delete',
    () async {
      final adapter = _RecordingAdapter(responseBody: jsonEncode(_loanJson()));
      final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
        ..httpClientAdapter = adapter;
      final repo = PropertyLoansRepository(dio);

      await repo.createLoan(propertyId: 7, data: _loanPayload());
      expect(adapter.method, 'POST');
      expect(adapter.path, '/loans');
      expect(adapter.data, containsPair('propertyId', 7));
      expect(adapter.data, containsPair('lender', 'First Federal'));

      await repo.updateLoan(42, _loanPayload(lender: 'Updated Bank'));
      expect(adapter.method, 'PATCH');
      expect(adapter.path, '/loans/42');
      expect(adapter.data, containsPair('lender', 'Updated Bank'));

      await repo.deleteLoan(42);
      expect(adapter.method, 'DELETE');
      expect(adapter.path, '/loans/42');
    },
  );

  test('property loans repository loads amortization schedule', () async {
    final adapter = _RecordingAdapter(
      responseBody: jsonEncode([_paymentJson()]),
    );
    final dio = Dio(BaseOptions(baseUrl: 'https://example.test'))
      ..httpClientAdapter = adapter;
    final repo = PropertyLoansRepository(dio);

    final payments = await repo.getLoanPayments(42);

    expect(adapter.method, 'GET');
    expect(adapter.path, '/loans/42/payments');
    expect(adapter.query, isEmpty);
    expect(payments.single.periodKey, '2026-07');
    expect(payments.single.principalAmount, 825.25);
    expect(payments.single.balanceAfter, 195674.75);
  });

  test(
    'property loans provider requests additional pages server-side',
    () async {
      final repo = _FakePropertyLoansRepository(
        pages: [
          [
            for (var i = 1; i <= 20; i++)
              PropertyLoan.fromJson(_loanJson(id: i, lender: 'Lender $i')),
          ],
          [PropertyLoan.fromJson(_loanJson(id: 21, lender: 'Lender 21'))],
        ],
      );
      final container = ProviderContainer(
        overrides: [propertyLoansRepositoryProvider.overrideWithValue(repo)],
      );
      addTearDown(container.dispose);

      final notifier = container.read(propertyLoansProvider(7).notifier);
      await notifier.refresh();

      final firstPage = container.read(propertyLoansProvider(7)).value!;
      expect(firstPage.loans, hasLength(20));
      expect(firstPage.hasMore, isTrue);
      expect(repo.requests.single, {
        'propertyId': 7,
        'skip': 0,
        'take': 20,
        'sort': '-createdAt',
      });

      await notifier.loadMore();

      final secondPage = container.read(propertyLoansProvider(7)).value!;
      expect(secondPage.loans, hasLength(21));
      expect(secondPage.hasMore, isFalse);
      expect(repo.requests.last, {
        'propertyId': 7,
        'skip': 20,
        'take': 20,
        'sort': '-createdAt',
      });
    },
  );

  testWidgets('property detail exposes property loan actions', (tester) async {
    final loansRepo = _FakePropertyLoansRepository();

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          authControllerProvider.overrideWith(
            () => _StaticAuthController(
              _managementAuthority({'money.expenses.manage'}),
            ),
          ),
          propertiesRepositoryProvider.overrideWithValue(
            _FakePropertiesRepository(),
          ),
          propertyLoansRepositoryProvider.overrideWithValue(loansRepo),
        ],
        child: MaterialApp(home: PropertyDetailScreen(property: _property())),
      ),
    );
    await tester.pumpAndSettle();

    await tester.ensureVisible(find.text('Property finances'));
    await tester.tap(find.text('Property finances'));
    await tester.pumpAndSettle();

    expect(find.text('Mortgage / Loans'), findsOneWidget);
    expect(find.text('First Federal'), findsOneWidget);
    expect(find.textContaining(r'$196,500'), findsOneWidget);
    expect(find.byTooltip('Add loan'), findsOneWidget);
    expect(find.byTooltip('Scan mortgage statement'), findsOneWidget);
    expect(find.byTooltip('View property activity'), findsOneWidget);
    expect(find.byTooltip('Edit loan'), findsOneWidget);
    expect(find.byTooltip('Delete loan'), findsOneWidget);
    expect(loansRepo.lastListedPropertyId, 7);

    await tester.tap(find.byTooltip('View amortization schedule'));
    await tester.pumpAndSettle();

    expect(find.text('Amortization schedule'), findsOneWidget);
    expect(find.text('2026-07'), findsOneWidget);
    expect(find.text('Principal'), findsOneWidget);
    expect(find.text(r'$825'), findsOneWidget);
    expect(find.text(r'$195,675'), findsOneWidget);
    expect(find.text('Record paid'), findsOneWidget);
    expect(loansRepo.lastPaymentLoanId, 42);

    await tester.tap(find.byTooltip('Scan mortgage statement'));
    await tester.pumpAndSettle();

    expect(find.text('Scan a mortgage statement'), findsOneWidget);
    expect(find.text('What are you scanning?'), findsNothing);
    expect(find.text('Record voice note'), findsNothing);
    expect(find.text('Take a photo'), findsOneWidget);
  });
}

AuthStateAuthenticated _managementAuthority(Set<String> capabilities) {
  const experience = WorkspaceExperience.management;
  return AuthStateAuthenticated(
    const AuthUser(
      id: 1,
      email: 'manager@example.test',
      displayName: 'Test manager',
      emailVerified: true,
    ),
    AccessEnvelope(
      identity: const AccessIdentity(userId: 1, displayName: 'Test manager'),
      selectedContext: const SelectedAccessContext(
        accessContextId: 1,
        portfolioId: 1,
        workspaceName: 'Test workspace',
        accessRevision: 1,
        activeExperience: experience,
      ),
      defaultExperience: experience,
      availableExperiences: const [experience],
      assignments: const [],
      navigation: [
        NavigationCapabilities(
          experience: experience,
          capabilityKeys: capabilities.toList(growable: false),
        ),
      ],
    ),
    activeExperience: experience,
  );
}

class _StaticAuthController extends AuthController {
  _StaticAuthController(this.initialState);

  final AuthState initialState;

  @override
  AuthState build() => initialState;
}

Map<String, dynamic> _loanJson({
  int id = 42,
  String lender = 'First Federal',
}) => {
  'id': id,
  'portfolioId': 1,
  'propertyId': 7,
  'propertyName': 'Lake House',
  'lender': lender,
  'originalAmount': 250000,
  'currentBalance': 196500,
  'annualInterestRatePct': 5.875,
  'termMonths': 360,
  'startDate': '2024-01-15T00:00:00.000Z',
  'dayOfMonthDue': 1,
  'monthlyPrincipalInterest': 1478.25,
  'monthlyEscrow': 420,
  'escrowCoversTaxes': true,
  'escrowCoversInsurance': false,
  'status': 'Active',
  'notes': 'Imported statement verified.',
  'createdAt': '2026-07-01T00:00:00.000Z',
  'updatedAt': '2026-07-01T00:00:00.000Z',
  'testId': 'loan-$id',
};

Map<String, dynamic> _loanPayload({String lender = 'First Federal'}) => {
  'lender': lender,
  'originalAmount': 250000.0,
  'currentBalance': 196500.0,
  'annualInterestRatePct': 5.875,
  'termMonths': 360,
  'startDate': '2024-01-15',
  'dayOfMonthDue': 1,
  'monthlyPrincipalInterest': 1478.25,
  'monthlyEscrow': 420.0,
  'escrowCoversTaxes': true,
  'escrowCoversInsurance': false,
  'status': 'Active',
  'notes': 'Imported statement verified.',
};

Map<String, dynamic> _paymentJson() => {
  'id': 501,
  'loanId': 42,
  'periodKey': '2026-07',
  'dueDate': '2026-07-01T00:00:00.000Z',
  'paidDate': null,
  'interestAmount': 653,
  'principalAmount': 825.25,
  'escrowAmount': 420,
  'totalAmount': 1898.25,
  'balanceAfter': 195674.75,
  'status': 'Scheduled',
  'paymentDoesNotCoverInterest': false,
};

Property _property() {
  return Property(
    id: 7,
    portfolioId: 1,
    name: 'Lake House',
    type: 'SingleFamily',
    rentalStructure: RentalStructure.singleRental,
    status: 'Active',
    addressLine1: '12 Lake Dr',
    city: 'Akron',
    state: 'OH',
    postalCode: '44308',
    unitCount: 1,
    occupiedUnits: 1,
    createdAt: DateTime(2026),
    updatedAt: DateTime(2026),
  );
}

class _RecordingAdapter implements HttpClientAdapter {
  _RecordingAdapter({required this.responseBody});

  final String responseBody;
  String? method;
  String? path;
  Map<String, dynamic>? query;
  Map<String, dynamic>? data;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    method = options.method;
    path = options.path;
    query = Map<String, dynamic>.from(options.queryParameters);
    data = options.data is Map<String, dynamic>
        ? Map<String, dynamic>.from(options.data as Map<String, dynamic>)
        : null;

    return ResponseBody.fromString(
      responseBody,
      options.method == 'POST' ? 201 : 200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

class _FakePropertiesRepository extends PropertiesRepository {
  _FakePropertiesRepository() : super(Dio());

  @override
  Future<List<Unit>> listUnits(
    int propertyId, {
    bool availableForLease = false,
  }) async => [
    Unit(
      id: 9,
      propertyId: 7,
      unitNumber: 'Lake House',
      bedrooms: 3,
      bathrooms: 2,
      marketRent: 2100,
      status: 'Occupied',
      createdAt: DateTime(2026),
      updatedAt: DateTime(2026),
    ),
  ];

  @override
  Future<List<LeaseManagementSummary>> listLeaseManagementsForProperty(
    int propertyId,
  ) async => const [];

  @override
  Future<Property> getProperty(int id) async => _property();
}

class _FakePropertyLoansRepository extends PropertyLoansRepository {
  _FakePropertyLoansRepository({List<List<PropertyLoan>>? pages})
    : _pages = pages,
      super(Dio());

  final List<List<PropertyLoan>>? _pages;
  final requests = <Map<String, Object?>>[];
  int? lastListedPropertyId;
  int? lastPaymentLoanId;

  @override
  Future<List<PropertyLoan>> listLoans({
    required int propertyId,
    int skip = 0,
    int? take,
    String sort = '-createdAt',
    String? search,
  }) async {
    lastListedPropertyId = propertyId;
    requests.add({
      'propertyId': propertyId,
      'skip': skip,
      'take': take,
      'sort': sort,
    });
    final pages = _pages;
    if (pages != null) {
      final pageIndex = requests.length - 1;
      return pageIndex < pages.length ? pages[pageIndex] : const [];
    }
    return [PropertyLoan.fromJson(_loanJson())];
  }

  @override
  Future<List<LoanPayment>> getLoanPayments(int loanId) async {
    lastPaymentLoanId = loanId;
    return [LoanPayment.fromJson(_paymentJson())];
  }
}
