import 'package:flutter/material.dart';

import '../../core/widgets/tabbed_form_sheet.dart';

class WorkOrderFormTabSpec extends TabbedFormStepSpec {
  const WorkOrderFormTabSpec({
    required super.label,
    required super.child,
    super.isComplete,
    super.validate,
  });
}

class WorkOrderFormShell extends StatelessWidget {
  const WorkOrderFormShell({
    super.key,
    required this.title,
    required this.tabs,
    required this.saveLabel,
    required this.saving,
    required this.onSave,
    this.error,
  });

  final String title;
  final List<WorkOrderFormTabSpec> tabs;
  final String saveLabel;
  final bool saving;
  final VoidCallback onSave;
  final String? error;

  @override
  Widget build(BuildContext context) {
    return TabbedFormSheet(
      title: title,
      tabs: tabs,
      saveLabel: saveLabel,
      saving: saving,
      onSave: onSave,
      error: error,
    );
  }
}
