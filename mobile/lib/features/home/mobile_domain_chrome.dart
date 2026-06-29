import 'package:flutter/material.dart';

/// Marks screens that are rendered inside a domain hub tab group.
///
/// Root list/grid screens still need their own AppBar when opened standalone
/// from Browse or a pushed route. When the same screen is embedded under the
/// Rentals/Money/Work/Inbox top tabs, the hub owns the header and actions.
class MobileDomainChromeScope extends InheritedWidget {
  const MobileDomainChromeScope({
    super.key,
    required this.embedded,
    required super.child,
  });

  final bool embedded;

  static bool isEmbedded(BuildContext context) {
    return context
            .dependOnInheritedWidgetOfExactType<MobileDomainChromeScope>()
            ?.embedded ??
        false;
  }

  @override
  bool updateShouldNotify(MobileDomainChromeScope oldWidget) =>
      embedded != oldWidget.embedded;
}

PreferredSizeWidget? mobileDomainRootAppBar(
  BuildContext context, {
  required Widget title,
  List<Widget>? actions,
}) {
  if (MobileDomainChromeScope.isEmbedded(context)) return null;
  return AppBar(title: title, actions: actions);
}

class MobileDomainEmbeddedToolbar extends StatelessWidget {
  const MobileDomainEmbeddedToolbar({
    super.key,
    required this.children,
    this.padding = const EdgeInsets.fromLTRB(16, 4, 16, 4),
  });

  final List<Widget> children;
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context) {
    if (!MobileDomainChromeScope.isEmbedded(context)) {
      return const SizedBox.shrink();
    }
    if (children.isEmpty) return const SizedBox.shrink();

    return Align(
      alignment: Alignment.centerRight,
      child: Padding(
        padding: padding,
        child: Wrap(
          alignment: WrapAlignment.end,
          crossAxisAlignment: WrapCrossAlignment.center,
          spacing: 8,
          runSpacing: 4,
          children: children,
        ),
      ),
    );
  }
}
