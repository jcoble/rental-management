import 'package:flutter/material.dart';

class MobileSectionItem<T> {
  const MobileSectionItem({
    required this.value,
    required this.id,
    required this.label,
    required this.icon,
  });

  final T value;
  final String id;
  final String label;
  final IconData icon;
}

/// An explicit Material 3 section picker for peer destinations that do not fit
/// together as directly tappable segments.
class MobileSectionSelector<T> extends StatelessWidget {
  const MobileSectionSelector({
    super.key,
    required this.items,
    required this.selectedValue,
    required this.onSelected,
    required this.tooltip,
    required this.selectorKey,
    required this.itemKeyPrefix,
  });

  final List<MobileSectionItem<T>> items;
  final T selectedValue;
  final ValueChanged<T> onSelected;
  final String tooltip;
  final Key selectorKey;
  final String itemKeyPrefix;

  @override
  Widget build(BuildContext context) {
    final selected = items.firstWhere((item) => item.value == selectedValue);
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 10),
      child: MenuAnchor(
        crossAxisUnconstrained: false,
        menuChildren: [
          for (final item in items)
            MenuItemButton(
              key: ValueKey('$itemKeyPrefix-${item.id}'),
              leadingIcon: Icon(item.icon),
              trailingIcon: item.value == selectedValue
                  ? const Icon(Icons.check_rounded)
                  : null,
              onPressed: () {
                if (item.value != selectedValue) onSelected(item.value);
              },
              child: Text(item.label),
            ),
        ],
        builder: (context, controller, _) => Tooltip(
          message: tooltip,
          child: SizedBox(
            width: double.infinity,
            height: 48,
            child: FilledButton.tonal(
              key: selectorKey,
              onPressed: () {
                controller.isOpen ? controller.close() : controller.open();
              },
              child: Row(
                children: [
                  Icon(selected.icon),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(
                      selected.label,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                  Icon(
                    controller.isOpen
                        ? Icons.keyboard_arrow_up_rounded
                        : Icons.keyboard_arrow_down_rounded,
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
