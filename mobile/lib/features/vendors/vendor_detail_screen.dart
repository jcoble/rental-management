import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'rate_vendor_sheet.dart';
import 'star_rating.dart';
import 'vendors_models.dart';
import 'vendors_repository.dart';

/// Vendor detail with a performance scorecard (average rating, rating count,
/// jobs completed, average DONE response time) and a "Rate vendor" action.
class VendorDetailScreen extends ConsumerWidget {
  const VendorDetailScreen({super.key, required this.vendor});

  final Vendor vendor;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final scorecardAsync = ref.watch(vendorScorecardProvider(vendor.id));

    return Scaffold(
      appBar: AppBar(title: Text(vendor.name)),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () => showRateVendorSheet(
          context,
          vendorId: vendor.id,
          vendorName: vendor.name,
        ),
        icon: const Icon(Icons.star_rounded),
        label: const Text('Rate vendor'),
      ),
      body: RefreshIndicator(
        onRefresh: () async =>
            ref.invalidate(vendorScorecardProvider(vendor.id)),
        child: ListView(
          physics: const AlwaysScrollableScrollPhysics(),
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 96),
          children: [
            // ── Header ────────────────────────────────────────────────────
            Row(
              children: [
                Container(
                  padding: const EdgeInsets.all(12),
                  decoration: BoxDecoration(
                    color: cs.tertiary.withValues(alpha: 0.12),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Icon(Icons.handyman_outlined,
                      color: cs.tertiary, size: 26),
                ),
                const SizedBox(width: 14),
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Flexible(
                            child: Text(
                              vendor.name,
                              style: theme.textTheme.titleLarge
                                  ?.copyWith(fontWeight: FontWeight.w700),
                            ),
                          ),
                          if (vendor.preferred) ...[
                            const SizedBox(width: 6),
                            Icon(Icons.star_rounded,
                                size: 18, color: Colors.amber.shade600),
                          ],
                        ],
                      ),
                      Text(
                        vendor.serviceType,
                        style: theme.textTheme.bodyMedium
                            ?.copyWith(color: cs.onSurfaceVariant),
                      ),
                    ],
                  ),
                ),
              ],
            ),
            if (vendor.phone != null || vendor.email != null) ...[
              const SizedBox(height: 14),
              if (vendor.phone != null)
                _ContactRow(
                  icon: Icons.phone_outlined,
                  value: vendor.phone!,
                  cs: cs,
                  theme: theme,
                ),
              if (vendor.email != null)
                _ContactRow(
                  icon: Icons.email_outlined,
                  value: vendor.email!,
                  cs: cs,
                  theme: theme,
                ),
            ],

            const SizedBox(height: 20),
            Text(
              'Scorecard',
              style: theme.textTheme.labelLarge?.copyWith(
                fontWeight: FontWeight.w700,
                color: cs.onSurfaceVariant,
                letterSpacing: 0.5,
              ),
            ),
            const SizedBox(height: 10),
            scorecardAsync.when(
              loading: () => const Padding(
                padding: EdgeInsets.all(24),
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (e, _) => _ScorecardError(
                message: e is ApiException ? e.message : e.toString(),
                onRetry: () =>
                    ref.invalidate(vendorScorecardProvider(vendor.id)),
              ),
              data: (card) => _ScorecardCard(card: card),
            ),
          ],
        ),
      ),
    );
  }
}

class _ScorecardCard extends StatelessWidget {
  const _ScorecardCard({required this.card});

  final VendorScorecard card;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final avg = card.averageRating;

    return Container(
      decoration: BoxDecoration(
        color: cs.surfaceContainerLowest,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: cs.outlineVariant),
      ),
      child: Column(
        children: [
          // Big average rating row.
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 12),
            child: Row(
              children: [
                Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      avg == null ? '—' : avg.toStringAsFixed(1),
                      style: theme.textTheme.displaySmall
                          ?.copyWith(fontWeight: FontWeight.w700),
                    ),
                    if (avg != null)
                      StarRatingDisplay(rating: avg, size: 18)
                    else
                      Text(
                        'Not rated yet',
                        style: theme.textTheme.bodySmall
                            ?.copyWith(color: cs.onSurfaceVariant),
                      ),
                  ],
                ),
                const SizedBox(width: 16),
                Expanded(
                  child: Text(
                    card.ratingCount == 1
                        ? '1 rating'
                        : '${card.ratingCount} ratings',
                    textAlign: TextAlign.right,
                    style: theme.textTheme.bodyMedium
                        ?.copyWith(color: cs.onSurfaceVariant),
                  ),
                ),
              ],
            ),
          ),
          Divider(height: 1, color: cs.outlineVariant),
          _StatRow(
            label: 'Jobs completed',
            value: '${card.jobsCompleted}',
            theme: theme,
            cs: cs,
          ),
          _StatRow(
            label: 'Avg. response time',
            value: card.avgResponseHours == null
                ? 'No data yet'
                : _formatHours(card.avgResponseHours!),
            theme: theme,
            cs: cs,
            isLast: true,
          ),
        ],
      ),
    );
  }

  static String _formatHours(double hours) {
    if (hours < 1) {
      final mins = (hours * 60).round();
      return '$mins min';
    }
    if (hours < 48) {
      return '${hours.toStringAsFixed(1)} hr';
    }
    final days = (hours / 24).toStringAsFixed(1);
    return '$days days';
  }
}

class _StatRow extends StatelessWidget {
  const _StatRow({
    required this.label,
    required this.value,
    required this.theme,
    required this.cs,
    this.isLast = false,
  });

  final String label;
  final String value;
  final ThemeData theme;
  final ColorScheme cs;
  final bool isLast;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      decoration: BoxDecoration(
        border: isLast
            ? null
            : Border(bottom: BorderSide(color: cs.outlineVariant)),
      ),
      child: Row(
        children: [
          Expanded(
            child: Text(
              label,
              style: theme.textTheme.bodyMedium
                  ?.copyWith(color: cs.onSurfaceVariant),
            ),
          ),
          Text(
            value,
            style: theme.textTheme.bodyMedium
                ?.copyWith(fontWeight: FontWeight.w600),
          ),
        ],
      ),
    );
  }
}

class _ContactRow extends StatelessWidget {
  const _ContactRow({
    required this.icon,
    required this.value,
    required this.cs,
    required this.theme,
  });

  final IconData icon;
  final String value;
  final ColorScheme cs;
  final ThemeData theme;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(top: 6),
      child: Row(
        children: [
          Icon(icon, size: 16, color: cs.onSurfaceVariant),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              value,
              style: theme.textTheme.bodyMedium,
            ),
          ),
        ],
      ),
    );
  }
}

class _ScorecardError extends StatelessWidget {
  const _ScorecardError({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(20),
      decoration: BoxDecoration(
        color: cs.surfaceContainerLowest,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: cs.outlineVariant),
      ),
      child: Column(
        children: [
          Icon(Icons.error_outline, size: 32, color: cs.error),
          const SizedBox(height: 8),
          Text(message,
              textAlign: TextAlign.center, style: TextStyle(color: cs.error)),
          const SizedBox(height: 12),
          FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    );
  }
}
