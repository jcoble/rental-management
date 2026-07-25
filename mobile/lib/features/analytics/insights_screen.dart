import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/presentation/date_labels.dart';
import '../home/mobile_domain_chrome.dart';
import 'analytics_repository.dart';

// ── Helpers ───────────────────────────────────────────────────────────────────

String _fmtCurrency(double amount) {
  final isNegative = amount < 0;
  final abs = amount.abs();
  final parts = abs.toStringAsFixed(2).split('.');
  final intPart = parts[0];
  final decPart = parts[1];
  final buf = StringBuffer();
  final len = intPart.length;
  for (var i = 0; i < len; i++) {
    if (i > 0 && (len - i) % 3 == 0) buf.write(',');
    buf.write(intPart[i]);
  }
  return '\$${isNegative ? '-' : ''}$buf.$decPart';
}

String _fmtPct(double pct) => '${pct.toStringAsFixed(1)}%';

// ── Screen ────────────────────────────────────────────────────────────────────

/// Insights screen — portfolio analytics overview.
///
/// Shows KPI cards (occupancy, collection rate, overdue, recurring rent),
/// an income-vs-expense trend chart painted with CustomPainter,
/// a lease-expiry pipeline (30/60/90 days), and open work orders by priority.
class InsightsScreen extends ConsumerStatefulWidget {
  const InsightsScreen({super.key});

  @override
  ConsumerState<InsightsScreen> createState() => _InsightsScreenState();
}

class _InsightsScreenState extends ConsumerState<InsightsScreen> {
  @override
  void initState() {
    super.initState();
    Future.microtask(() => ref.read(analyticsOverviewProvider.notifier).load());
  }

  Future<void> _refresh() =>
      ref.read(analyticsOverviewProvider.notifier).refresh();

  @override
  Widget build(BuildContext context) {
    final asyncState = ref.watch(analyticsOverviewProvider);

    return Scaffold(
      appBar: mobileDomainRootAppBar(context, title: const Text('Insights')),
      body: RefreshIndicator(
        onRefresh: _refresh,
        child: asyncState.when(
          loading: () => const Center(child: CircularProgressIndicator()),
          error: (e, _) => _ErrorBody(
            message: e is ApiException ? e.message : e.toString(),
            onRetry: () => ref.read(analyticsOverviewProvider.notifier).load(),
          ),
          data: (overview) => _OverviewContent(overview: overview),
        ),
      ),
    );
  }
}

// ── Content ───────────────────────────────────────────────────────────────────

class _OverviewContent extends StatelessWidget {
  const _OverviewContent({required this.overview});

  final AnalyticsOverview overview;

  @override
  Widget build(BuildContext context) {
    return CustomScrollView(
      physics: const AlwaysScrollableScrollPhysics(),
      slivers: [
        SliverPadding(
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
          sliver: SliverList(
            delegate: SliverChildListDelegate([
              _KpiGrid(overview: overview),
              const SizedBox(height: 20),
              _SectionHeader(title: 'Income vs Expenses'),
              const SizedBox(height: 8),
              _TrendChart(trend: overview.trend),
              const SizedBox(height: 20),
              _SectionHeader(title: 'Leases Expiring'),
              const SizedBox(height: 8),
              _LeaseExpiryPipeline(overview: overview),
              const SizedBox(height: 20),
              _SectionHeader(title: 'Open Work Orders'),
              const SizedBox(height: 8),
              _WorkOrderPriorityList(workOrders: overview.openWorkOrders),
              const SizedBox(height: 24),
            ]),
          ),
        ),
      ],
    );
  }
}

// ── KPI Grid ──────────────────────────────────────────────────────────────────

class _KpiGrid extends StatelessWidget {
  const _KpiGrid({required this.overview});

  final AnalyticsOverview overview;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Column(
      children: [
        Row(
          children: [
            Expanded(
              child: _KpiCard(
                label: 'Occupancy',
                value: _fmtPct(overview.occupancyRate),
                subtitle:
                    '${overview.occupiedUnits}/${overview.totalUnits} units',
                color: cs.primaryContainer,
                textColor: cs.onPrimaryContainer,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _KpiCard(
                label: 'Collection Rate',
                value: _fmtPct(overview.collectionRate),
                subtitle:
                    '${_fmtCurrency(overview.monthRentCollected)} of ${_fmtCurrency(overview.monthRentScheduled)}',
                color: cs.secondaryContainer,
                textColor: cs.onSecondaryContainer,
              ),
            ),
          ],
        ),
        const SizedBox(height: 10),
        Row(
          children: [
            Expanded(
              child: _KpiCard(
                label: 'Overdue',
                value: _fmtCurrency(overview.overdueAmount),
                subtitle:
                    '${overview.overdueCount} payment${overview.overdueCount == 1 ? '' : 's'}',
                color: cs.errorContainer,
                textColor: cs.onErrorContainer,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _KpiCard(
                label: 'Signed lease rent',
                value: _fmtCurrency(overview.monthlyRecurringRent),
                subtitle: 'currently governing',
                color: cs.tertiaryContainer,
                textColor: cs.onTertiaryContainer,
              ),
            ),
          ],
        ),
      ],
    );
  }
}

class _KpiCard extends StatelessWidget {
  const _KpiCard({
    required this.label,
    required this.value,
    required this.subtitle,
    required this.color,
    required this.textColor,
  });

  final String label;
  final String value;
  final String subtitle;
  final Color color;
  final Color textColor;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
      decoration: BoxDecoration(
        color: color,
        borderRadius: BorderRadius.circular(14),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            label,
            style: theme.textTheme.labelSmall?.copyWith(color: textColor),
          ),
          const SizedBox(height: 4),
          Text(
            value,
            style: theme.textTheme.titleMedium?.copyWith(
              fontWeight: FontWeight.w700,
              color: textColor,
            ),
          ),
          const SizedBox(height: 2),
          Text(
            subtitle,
            style: theme.textTheme.bodySmall?.copyWith(
              color: textColor.withValues(alpha: 0.75),
            ),
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
          ),
        ],
      ),
    );
  }
}

// ── Trend Chart ───────────────────────────────────────────────────────────────

class _SectionHeader extends StatelessWidget {
  const _SectionHeader({required this.title});

  final String title;

  @override
  Widget build(BuildContext context) {
    return Text(
      title,
      style: Theme.of(
        context,
      ).textTheme.titleSmall?.copyWith(fontWeight: FontWeight.w700),
    );
  }
}

/// A lightweight custom-painted bar chart — no chart package dependency.
class _TrendChart extends StatelessWidget {
  const _TrendChart({required this.trend});

  final List<TrendPoint> trend;

  @override
  Widget build(BuildContext context) {
    if (trend.isEmpty) {
      return _EmptyCard(message: 'No trend data yet.');
    }
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.fromLTRB(12, 12, 12, 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            SizedBox(
              height: 140,
              child: CustomPaint(
                size: const Size(double.infinity, 140),
                painter: _BarChartPainter(
                  trend: trend,
                  incomeColor: cs.primary,
                  expenseColor: cs.error,
                ),
              ),
            ),
            const SizedBox(height: 8),
            // Legend
            Row(
              children: [
                _LegendDot(color: cs.primary, label: 'Income'),
                const SizedBox(width: 16),
                _LegendDot(color: cs.error, label: 'Expenses'),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _LegendDot extends StatelessWidget {
  const _LegendDot({required this.color, required this.label});

  final Color color;
  final String label;

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 10,
          height: 10,
          decoration: BoxDecoration(color: color, shape: BoxShape.circle),
        ),
        const SizedBox(width: 4),
        Text(
          label,
          style: Theme.of(context).textTheme.labelSmall?.copyWith(
            color: Theme.of(context).colorScheme.onSurfaceVariant,
          ),
        ),
      ],
    );
  }
}

class _BarChartPainter extends CustomPainter {
  _BarChartPainter({
    required this.trend,
    required this.incomeColor,
    required this.expenseColor,
  });

  final List<TrendPoint> trend;
  final Color incomeColor;
  final Color expenseColor;

  @override
  void paint(Canvas canvas, Size size) {
    if (trend.isEmpty) return;

    // Find max value for scaling
    double maxVal = 1;
    for (final p in trend) {
      if (p.income > maxVal) maxVal = p.income;
      if (p.expenses > maxVal) maxVal = p.expenses;
    }

    final n = trend.length;
    const labelHeight = 18.0;
    const gapBetweenGroups = 6.0;
    const barGap = 2.0;
    const chartPadLeft = 4.0;
    const chartPadRight = 4.0;
    final chartHeight = size.height - labelHeight;
    final groupWidth =
        (size.width -
            chartPadLeft -
            chartPadRight -
            gapBetweenGroups * (n - 1)) /
        n;
    final barWidth = (groupWidth - barGap) / 2;

    final incomePaint = Paint()
      ..color = incomeColor.withValues(alpha: 0.85)
      ..style = PaintingStyle.fill;
    final expensePaint = Paint()
      ..color = expenseColor.withValues(alpha: 0.85)
      ..style = PaintingStyle.fill;
    final labelStyle = TextStyle(
      color: expenseColor.withValues(alpha: 0.6),
      fontSize: 9,
    );

    for (var i = 0; i < n; i++) {
      final point = trend[i];
      final groupX = chartPadLeft + i * (groupWidth + gapBetweenGroups);

      // Income bar
      final incomeH = maxVal > 0 ? (point.income / maxVal) * chartHeight : 0.0;
      final incomeRect = RRect.fromRectAndCorners(
        Rect.fromLTWH(groupX, chartHeight - incomeH, barWidth, incomeH),
        topLeft: const Radius.circular(3),
        topRight: const Radius.circular(3),
      );
      canvas.drawRRect(incomeRect, incomePaint);

      // Expense bar
      final expH = maxVal > 0 ? (point.expenses / maxVal) * chartHeight : 0.0;
      final expRect = RRect.fromRectAndCorners(
        Rect.fromLTWH(
          groupX + barWidth + barGap,
          chartHeight - expH,
          barWidth,
          expH,
        ),
        topLeft: const Radius.circular(3),
        topRight: const Radius.circular(3),
      );
      canvas.drawRRect(expRect, expensePaint);

      final label = shortMonthLabel(point.month);
      final tp = TextPainter(
        text: TextSpan(text: label, style: labelStyle),
        textDirection: TextDirection.ltr,
      )..layout(maxWidth: groupWidth);
      tp.paint(
        canvas,
        Offset(groupX + (groupWidth - tp.width) / 2, chartHeight + 2),
      );
    }
  }

  @override
  bool shouldRepaint(_BarChartPainter old) =>
      old.trend != trend ||
      old.incomeColor != incomeColor ||
      old.expenseColor != expenseColor;
}

// ── Lease Expiry Pipeline ─────────────────────────────────────────────────────

class _LeaseExpiryPipeline extends StatelessWidget {
  const _LeaseExpiryPipeline({required this.overview});

  final AnalyticsOverview overview;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Row(
          children: [
            Expanded(
              child: _ExpiryBucket(
                label: '30 days',
                count: overview.leasesExpiring30,
                color: cs.errorContainer,
                textColor: cs.onErrorContainer,
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ExpiryBucket(
                label: '60 days',
                count: overview.leasesExpiring60,
                color: cs.secondaryContainer,
                textColor: cs.onSecondaryContainer,
              ),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: _ExpiryBucket(
                label: '90 days',
                count: overview.leasesExpiring90,
                color: cs.primaryContainer,
                textColor: cs.onPrimaryContainer,
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ExpiryBucket extends StatelessWidget {
  const _ExpiryBucket({
    required this.label,
    required this.count,
    required this.color,
    required this.textColor,
  });

  final String label;
  final int count;
  final Color color;
  final Color textColor;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.symmetric(vertical: 12),
      decoration: BoxDecoration(
        color: color,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Column(
        children: [
          Text(
            '$count',
            style: theme.textTheme.headlineSmall?.copyWith(
              fontWeight: FontWeight.w700,
              color: textColor,
            ),
          ),
          Text(
            label,
            style: theme.textTheme.labelSmall?.copyWith(color: textColor),
            textAlign: TextAlign.center,
          ),
        ],
      ),
    );
  }
}

// ── Work Orders by Priority ───────────────────────────────────────────────────

class _WorkOrderPriorityList extends StatelessWidget {
  const _WorkOrderPriorityList({required this.workOrders});

  final List<WorkOrderPriorityStat> workOrders;

  static Color _priorityColor(String priority, ColorScheme cs) {
    switch (priority.toLowerCase()) {
      case 'urgent':
      case 'emergency':
        return cs.error;
      case 'high':
        return cs.errorContainer;
      case 'medium':
      case 'normal':
        return cs.secondaryContainer;
      default:
        return cs.surfaceContainerHighest;
    }
  }

  @override
  Widget build(BuildContext context) {
    if (workOrders.isEmpty) {
      return _EmptyCard(message: 'No open work orders.');
    }
    final cs = Theme.of(context).colorScheme;
    final theme = Theme.of(context);
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          children: workOrders.map((wo) {
            return Padding(
              padding: const EdgeInsets.symmetric(vertical: 5),
              child: Row(
                children: [
                  Container(
                    width: 10,
                    height: 10,
                    decoration: BoxDecoration(
                      color: _priorityColor(wo.priority, cs),
                      shape: BoxShape.circle,
                    ),
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(wo.priority, style: theme.textTheme.bodyMedium),
                  ),
                  Container(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 10,
                      vertical: 3,
                    ),
                    decoration: BoxDecoration(
                      color: cs.surfaceContainerHighest,
                      borderRadius: BorderRadius.circular(20),
                    ),
                    child: Text(
                      '${wo.count}',
                      style: theme.textTheme.labelMedium?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                ],
              ),
            );
          }).toList(),
        ),
      ),
    );
  }
}

// ── Shared helpers ────────────────────────────────────────────────────────────

class _EmptyCard extends StatelessWidget {
  const _EmptyCard({required this.message});

  final String message;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(20),
        child: Center(
          child: Text(
            message,
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
          ),
        ),
      ),
    );
  }
}

class _ErrorBody extends StatelessWidget {
  const _ErrorBody({required this.message, required this.onRetry});

  final String message;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Padding(
        padding: const EdgeInsets.all(24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.error_outline, size: 40, color: cs.error),
            const SizedBox(height: 12),
            Text(
              message,
              textAlign: TextAlign.center,
              style: TextStyle(color: cs.error),
            ),
            const SizedBox(height: 16),
            FilledButton.tonal(onPressed: onRetry, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}
