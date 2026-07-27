import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('new appointment start picker defaults to the app clock', () {
    final sheet = _classBody(
      'lib/features/appointments/appointments_screen.dart',
      '_AppointmentFormSheetState',
    );
    final pickStart = _methodBody(sheet, '_pickStartDateTime');

    expect(pickStart, contains('ref.read(appNowProvider.future)'));
    expect(pickStart, contains('initialDate: _startDate ?? now'));
    expect(
      pickStart,
      contains(
        'initialTime: _startTime ?? TimeOfDay.fromDateTime(now.toLocal())',
      ),
    );
    expect(pickStart, isNot(contains('DateTime.now()')));
    expect(pickStart, isNot(contains('TimeOfDay.now()')));
  });

  test(
    'new appointment end picker preserves edit/start values before clock',
    () {
      final sheet = _classBody(
        'lib/features/appointments/appointments_screen.dart',
        '_AppointmentFormSheetState',
      );
      final pickEnd = _methodBody(sheet, '_pickEndDateTime');

      expect(pickEnd, contains('ref.read(appNowProvider.future)'));
      expect(pickEnd, contains('initialDate: _endDate ?? _startDate ?? now'));
      expect(
        pickEnd,
        contains(
          '_endTime ?? _startTime ?? TimeOfDay.fromDateTime(now.toLocal())',
        ),
      );
      expect(pickEnd, isNot(contains('DateTime.now()')));
      expect(pickEnd, isNot(contains('TimeOfDay.now()')));
    },
  );

  test(
    'appointment tenant selector is property scoped and clears stale tenant',
    () {
      final source = _source(
        'lib/features/appointments/appointments_screen.dart',
      );
      final sheet = _classBody(
        'lib/features/appointments/appointments_screen.dart',
        '_AppointmentFormSheetState',
      );

      expect(source, isNot(contains("dio.get<List<dynamic>>('/tenants')")));
      expect(sheet, contains('tenantsPageProvider('));
      expect(sheet, contains('TenantListQuery('));
      expect(sheet, contains('propertyId: _selectedPropertyId'));
      expect(sheet, contains('if (_selectedPropertyId != v)'));
      expect(sheet, contains('_selectedTenantId = null'));
      expect(
        sheet,
        contains('tenants.any((tenant) => tenant.id == _selectedTenantId)'),
      );
    },
  );

  test('appointment tenant options include server-projected context', () {
    final sheet = _classBody(
      'lib/features/appointments/appointments_screen.dart',
      '_AppointmentFormSheetState',
    );
    final label = _methodDeclarationBody(sheet, 'String', '_tenantOptionLabel');

    expect(label, contains('tenant.currentPropertyName'));
    expect(label, contains('tenant.currentUnitNumber'));
    expect(label, contains("'Unit \${tenant.currentUnitNumber}'"));
  });
}

String _source(String relativePath) => File(relativePath).readAsStringSync();

String _classBody(String relativePath, String className) {
  final source = _source(relativePath);
  final start = source.indexOf('class $className');
  expect(start, isNonNegative, reason: 'Expected to find class $className');
  final bodyStart = source.indexOf('{', start);
  final end = _matchingBrace(source, bodyStart);
  return source.substring(bodyStart, end + 1);
}

String _methodBody(String source, String methodName) {
  final match = RegExp(
    '${RegExp.escape(methodName)}\\s*\\(',
  ).firstMatch(source);
  final start = match?.start ?? -1;
  expect(start, isNonNegative, reason: 'Expected to find method $methodName');
  final parameterStart = source.indexOf('(', start);
  final parameterEnd = _matchingParen(source, parameterStart);
  final bodyStart = source.indexOf('{', parameterEnd);
  final end = _matchingBrace(source, bodyStart);
  return source.substring(bodyStart, end + 1);
}

String _methodDeclarationBody(
  String source,
  String returnType,
  String methodName,
) {
  final match = RegExp(
    '${RegExp.escape(returnType)}\\s+${RegExp.escape(methodName)}\\s*\\(',
  ).firstMatch(source);
  final start = match?.start ?? -1;
  expect(start, isNonNegative, reason: 'Expected to find method $methodName');
  final parameterStart = source.indexOf('(', start);
  final parameterEnd = _matchingParen(source, parameterStart);
  final bodyStart = source.indexOf('{', parameterEnd);
  final end = _matchingBrace(source, bodyStart);
  return source.substring(bodyStart, end + 1);
}

int _matchingBrace(String source, int openIndex) =>
    _matchingDelimiter(source, openIndex, '{', '}');

int _matchingParen(String source, int openIndex) =>
    _matchingDelimiter(source, openIndex, '(', ')');

int _matchingDelimiter(
  String source,
  int openIndex,
  String open,
  String close,
) {
  expect(openIndex, isNonNegative, reason: 'Expected opening $open');
  var depth = 0;
  for (var i = openIndex; i < source.length; i++) {
    final char = source[i];
    if (char == open) depth++;
    if (char == close) {
      depth--;
      if (depth == 0) return i;
    }
  }
  fail('Expected closing $close');
}
