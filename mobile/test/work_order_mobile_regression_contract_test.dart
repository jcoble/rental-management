import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

void main() {
  test('work order edit date picker is anchored to the app clock', () {
    final editSheet = _classBody(
      'lib/features/maintenance/work_order_detail_screen.dart',
      '_EditWorkOrderSheetState',
    );
    final pickScheduledDate = _methodBody(editSheet, '_pickScheduledDate');

    expect(pickScheduledDate, contains('ref.read(appNowProvider.future)'));
    expect(pickScheduledDate, contains('showDatePicker('));
    expect(pickScheduledDate, isNot(contains('DateTime.now()')));
  });

  test('work order edit time picker is anchored to the app clock', () {
    final editSheet = _classBody(
      'lib/features/maintenance/work_order_detail_screen.dart',
      '_EditWorkOrderSheetState',
    );
    final pickTime = _methodBody(editSheet, '_pickTime');

    expect(pickTime, contains('ref.read(appNowProvider.future)'));
    expect(pickTime, contains('showTimePicker('));
    expect(pickTime, contains('TimeOfDay.fromDateTime(now.toLocal())'));
    expect(pickTime, isNot(contains('DateTime.now()')));
  });

  test(
    'work order edit preserves selected tenant absent from lookup options',
    () {
      final editSheet = _classBody(
        'lib/features/maintenance/work_order_detail_screen.dart',
        '_EditWorkOrderSheetState',
      );

      expect(
        editSheet,
        contains('if (!hasSelectedTenant && _selectedTenantId != null)'),
      );
      expect(
        editSheet,
        contains(
          "widget.workOrder.tenantName ?? 'Tenant #\$_selectedTenantId'",
        ),
      );
      expect(editSheet, contains("'tenantId': ?_selectedTenantId"));
    },
  );

  test('work order scan category dropdown uses maintenance taxonomy', () {
    final source = _source('lib/features/scan/scan_review_screen.dart');
    final categories = _constList(source, '_workOrderCategories');
    final fieldGroups = _constList(source, '_fieldGroups');
    final fieldList = _classBody(
      'lib/features/scan/scan_review_screen.dart',
      '_FieldsSection',
    );

    expect(fieldGroups, contains("'category'"));
    expect(categories, contains("'Plumbing'"));
    expect(categories, contains("'Electrical'"));
    expect(categories, contains("'HVAC'"));
    expect(categories, contains("'Appliance'"));
    expect(categories, isNot(contains("'MortgageInterest'")));
    expect(categories, isNot(contains("'CleaningMaintenance'")));
    expect(
      fieldList,
      matches(
        RegExp(
          r'categoryOptions:\s*draft\.isWorkOrder\s*\?\s*_workOrderCategories\s*:\s*_scheduleECategories',
        ),
      ),
    );
  });

  test('work order scan confirmation refreshes mobile work-order surfaces', () {
    final source = _source('lib/features/scan/scan_review_screen.dart');
    final confirm = _methodBody(source, '_confirm');

    expect(confirm, contains('if (draft.isWorkOrder)'));
    expect(confirm, contains("result?['workOrderId']"));
    expect(confirm, contains("result?['entityType'] == 'WorkOrder'"));
    expect(confirm, contains('ref.invalidate(workOrdersPageProvider)'));
    expect(
      confirm,
      contains('ref.read(workOrdersProvider.notifier).refresh()'),
    );
    expect(confirm, contains('ref.invalidate(unitDashboardProvider(unitId))'));
    expect(confirm, contains('MobileShellTabId.work'));
    expect(confirm, contains('MobileDestinationId.workOrders'));
    expect(confirm, contains('WorkOrderUnitAwareLoaderScreen('));
    expect(confirm, contains('pushReplacement('));
  });

  test(
    'work order detail action sheets hide global quick actions while open',
    () {
      final detailState = _classBody(
        'lib/features/maintenance/work_order_detail_screen.dart',
        '_WorkOrderDetailScreenState',
      );
      final helper = _genericMethodBody(
        detailState,
        '_showWorkOrderActionSheet',
      );
      final shellHelper = _genericMethodBody(
        detailState,
        '_runWithWorkOrderQuickActionsHidden',
      );

      expect(helper, contains('showModalBottomSheet<T>('));
      expect(helper, contains('MobileQuickActionHider('));
      expect(helper, contains('child: builder(sheetContext)'));
      expect(shellHelper, contains('mobileShellNavigatorOf(context)'));
      expect(shellHelper, contains('MobileShellTabId.work'));
      expect(shellHelper, contains('true'));
      expect(shellHelper, contains('false'));

      for (final method in [
        '_changeStatus',
        '_showResponsibilitySheet',
        '_showPhotoSourceSheet',
        '_showEditSheet',
      ]) {
        final body = _methodBody(detailState, method);
        expect(body, contains('_showWorkOrderActionSheet'));
        expect(body, isNot(contains('showModalBottomSheet')));
      }

      for (final method in ['_callVendor', '_dispatchVendor', '_rateVendor']) {
        final body = _methodBody(detailState, method);
        expect(body, contains('_runWithWorkOrderQuickActionsHidden'));
      }
    },
  );

  test('work order detail actions are gated by server detail capabilities', () {
    final source = _source(
      'lib/features/maintenance/work_order_detail_screen.dart',
    );

    expect(source, contains('RoleAwareWorkOrderDetail.fromBase'));
    expect(source, contains('capabilities.canDispatchVendor'));
    expect(source, contains('detail.hasActiveDispatch'));
    expect(source, contains('detail.activeDispatchId'));
    expect(source, contains('Cancel dispatch'));
    expect(source, contains('capabilities.canEditManagementFields'));
    expect(source, contains('capabilities.canUpdateStatus'));
    expect(source, contains('capabilities.canViewCosts'));
    expect(source, contains('capabilities.canViewPrivateManagementNotes'));
    expect(source, contains('capabilities.allowedStatusTransitions'));
    expect(source, isNot(contains('work.manage')));
  });

  test('tenant work order detail stays tenant-safe', () {
    final source = _source(
      'lib/features/portal/tenant_work_order_detail_screen.dart',
    );

    expect(source, isNot(contains('estimatedCost')));
    expect(source, isNot(contains('actualCost')));
    expect(source, isNot(contains('Text a vendor')));
    expect(source, isNot(contains('Rate this vendor')));
    expect(source, isNot(contains('canUploadPhoto: true')));
    expect(source, isNot(contains('technicianAccessInstructions')));
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

String _genericMethodBody(String source, String methodName) {
  final match = RegExp(
    '${RegExp.escape(methodName)}\\s*<[^>]+>\\s*\\(',
  ).firstMatch(source);
  final start = match?.start ?? -1;
  expect(start, isNonNegative, reason: 'Expected to find method $methodName');
  final parameterStart = source.indexOf('(', start);
  final parameterEnd = _matchingParen(source, parameterStart);
  final bodyStart = source.indexOf('{', parameterEnd);
  final end = _matchingBrace(source, bodyStart);
  return source.substring(bodyStart, end + 1);
}

String _constList(String source, String constName) {
  final start = source.indexOf('const $constName');
  expect(start, isNonNegative, reason: 'Expected to find const $constName');
  final listStart = source.indexOf('[', start);
  final end = _matchingBracket(source, listStart);
  return source.substring(listStart, end + 1);
}

int _matchingBrace(String source, int openIndex) =>
    _matchingDelimiter(source, openIndex, '{', '}');

int _matchingBracket(String source, int openIndex) =>
    _matchingDelimiter(source, openIndex, '[', ']');

int _matchingParen(String source, int openIndex) =>
    _matchingDelimiter(source, openIndex, '(', ')');

int _matchingDelimiter(
  String source,
  int openIndex,
  String open,
  String close,
) {
  expect(openIndex, isNonNegative, reason: 'Expected to find $open delimiter');
  var depth = 0;
  for (var i = openIndex; i < source.length; i++) {
    final char = source[i];
    if (char == open) depth++;
    if (char == close) {
      depth--;
      if (depth == 0) return i;
    }
  }
  fail('No matching $close delimiter found');
}
