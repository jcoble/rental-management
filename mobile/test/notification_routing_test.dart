import 'package:flutter_test/flutter_test.dart';
import 'package:rental_command/core/push/notification_routing.dart';

void main() {
  test('section roots used by mobile IA are allowed notification targets', () {
    expect(resolveNotificationRoute('/rentals'), '/rentals');
    expect(resolveNotificationRoute('/owners'), '/owners');
    expect(resolveNotificationRoute('/units'), '/units');
    expect(resolveNotificationRoute('/money'), '/money');
    expect(resolveNotificationRoute('/work'), '/work');
    expect(resolveNotificationRoute('/inbox'), '/inbox');
    expect(resolveNotificationRoute('/notifications'), '/notifications');
  });

  test('unit command center action urls keep tab query state', () {
    expect(
      resolveNotificationRoute('/units/42?tab=lease'),
      '/units/42?tab=lease',
    );
    expect(
      resolveNotificationRoute('/units/42?tab=maintenance'),
      '/units/42?tab=maintenance',
    );
  });

  test('canonical tenant-ledger entry routes keep both exact ids', () {
    expect(
      resolveNotificationRoute('/tenant-accounts/42/entries/8'),
      '/tenant-accounts/42/entries/8',
    );
  });

  test('server query action urls normalize to mobile detail routes', () {
    expect(
      resolveNotificationRoute('/messages?conversationId=42'),
      '/messages/42',
    );
    expect(
      resolveNotificationRoute('/work-orders?workOrderId=17'),
      '/work-orders/17',
    );
    expect(resolveNotificationRoute('/payments?paymentId=8'), '/notifications');
  });

  test('unknown notification targets fall back to notifications inbox', () {
    expect(resolveNotificationRoute(null), '/notifications');
    expect(resolveNotificationRoute(''), '/notifications');
    expect(resolveNotificationRoute('/admin'), '/notifications');
    expect(
      resolveNotificationRoute('/messages?conversationId=0'),
      '/notifications',
    );
  });
}
