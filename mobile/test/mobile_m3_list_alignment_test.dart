import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/widgets/mobile_m3_list.dart';

void main() {
  testWidgets(
    'MobileM3ListItem centers leading and trailing slots vertically',
    (tester) async {
      await tester.pumpWidget(
        MaterialApp(
          home: Scaffold(
            body: Center(
              child: SizedBox(
                width: 360,
                child: MobileM3ListItem(
                  key: const Key('row'),
                  position: MobileM3ListItemPosition.single,
                  leading: MobileM3LeadingIcon(
                    key: const Key('leading'),
                    icon: Icons.person_outline,
                    backgroundColor: Colors.purple.shade100,
                    foregroundColor: Colors.purple.shade900,
                  ),
                  title: const Text('A longer list row'),
                  supporting: const [
                    Text('First supporting line'),
                    Text('Second supporting line'),
                  ],
                  meta: const Text('Meta line that makes the row taller'),
                  trailing: const Icon(
                    Icons.chevron_right,
                    key: Key('trailing'),
                  ),
                ),
              ),
            ),
          ),
        ),
      );

      final rowCenterY = tester.getCenter(find.byKey(const Key('row'))).dy;
      final leadingCenterY = tester
          .getCenter(find.byKey(const Key('leading')))
          .dy;
      final trailingCenterY = tester
          .getCenter(find.byKey(const Key('trailing')))
          .dy;

      expect((leadingCenterY - rowCenterY).abs(), lessThanOrEqualTo(1));
      expect((trailingCenterY - rowCenterY).abs(), lessThanOrEqualTo(1));
      expect(
        tester.getSize(find.byKey(const Key('trailing'))).height,
        lessThan(48),
      );
    },
  );
}
