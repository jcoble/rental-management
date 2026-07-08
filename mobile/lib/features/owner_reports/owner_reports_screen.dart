import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/files/document_opener.dart';
import '../../core/theme/app_tokens.dart';
import '../../core/widgets/mobile_m3_list.dart';
import '../accounting/accounting_repository.dart';
import '../home/mobile_domain_chrome.dart';
import 'owner_reports_repository.dart';

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

// ── Screen ────────────────────────────────────────────────────────────────────

/// Owner reports screen — year selector + owner list.
///
/// Tap an owner to open a detail bottom sheet with per-property financials.
class OwnerReportsScreen extends ConsumerStatefulWidget {
  const OwnerReportsScreen({super.key});

  @override
  ConsumerState<OwnerReportsScreen> createState() => _OwnerReportsScreenState();
}

class _OwnerReportsScreenState extends ConsumerState<OwnerReportsScreen> {
  int _selectedYear = DateTime.now().year;

  /// Year for the accountant packet. Defaults to the previous calendar year,
  /// since that's the tax year landlords usually hand off.
  int _packetYear = DateTime.now().year - 1;

  @override
  void initState() {
    super.initState();
    Future.microtask(
      () => ref.read(ownerSummariesProvider.notifier).load(year: _selectedYear),
    );
  }

  Future<void> _refresh() =>
      ref.read(ownerSummariesProvider.notifier).refresh();

  void _changeYear(int year) {
    setState(() => _selectedYear = year);
    ref.read(ownerSummariesProvider.notifier).load(year: year);
  }

  /// Fetches the year-end packet PDF bytes (authed) and hands them to the OS
  /// viewer via [DocumentOpener] (FileProvider `content://` URI, share fallback).
  Future<void> _openYearEndPacket(int year) async {
    final messenger = ScaffoldMessenger.of(context);
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(SnackBar(content: Text('Preparing your $year packet…')));
    try {
      final bytes = await ref
          .read(accountingRepositoryProvider)
          .yearEndPacketBytes(year);
      await DocumentOpener.openBytes(
        bytes: bytes,
        fileName: 'year-end-$year.pdf',
      );
      messenger.hideCurrentSnackBar();
    } on ApiException catch (e) {
      messenger
        ..hideCurrentSnackBar()
        ..showSnackBar(SnackBar(content: Text(e.message)));
    }
  }

  void _showStatement(OwnerSummary owner) {
    ref
        .read(ownerStatementProvider.notifier)
        .load(owner.ownerId, _selectedYear);
    showModalBottomSheet<void>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      builder: (_) => _OwnerStatementSheet(ownerName: owner.ownerName),
    );
  }

  @override
  Widget build(BuildContext context) {
    final asyncState = ref.watch(ownerSummariesProvider);
    final now = DateTime.now().year;
    final yearSelector = _YearSelector(
      selected: _selectedYear,
      years: List.generate(5, (i) => now - i),
      onChanged: _changeYear,
    );

    return Scaffold(
      appBar: mobileDomainRootAppBar(
        context,
        title: const Text('Owner Reports'),
        actions: [yearSelector, const SizedBox(width: 8)],
      ),
      body: Column(
        children: [
          MobileDomainEmbeddedToolbar(children: [yearSelector]),
          Expanded(
            child: RefreshIndicator(
              onRefresh: _refresh,
              child: asyncState.when(
                loading: () => const Center(child: CircularProgressIndicator()),
                error: (e, _) => _ErrorBody(
                  message: e is ApiException ? e.message : e.toString(),
                  onRetry: () =>
                      ref.read(ownerSummariesProvider.notifier).refresh(),
                ),
                data: (list) {
                  final packetCard = _YearEndPacketCard(
                    year: _packetYear,
                    years: List.generate(5, (i) => now - 1 - i),
                    onYearChanged: (y) => setState(() => _packetYear = y),
                    onOpen: () => _openYearEndPacket(_packetYear),
                  );
                  if (list.isEmpty) {
                    return ListView(
                      physics: const AlwaysScrollableScrollPhysics(),
                      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
                      children: [
                        packetCard,
                        const SizedBox(height: 24),
                        _EmptyBody(year: _selectedYear),
                      ],
                    );
                  }
                  return ListView.separated(
                    physics: const AlwaysScrollableScrollPhysics(),
                    padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
                    itemCount: list.length + 1,
                    separatorBuilder: (context, index) {
                      if (index == 0) return const SizedBox(height: 16);
                      return const MobileM3ListDivider();
                    },
                    itemBuilder: (_, i) {
                      if (i == 0) {
                        return packetCard;
                      }
                      final owner = list[i - 1];
                      return _OwnerSummaryListItem(
                        owner: owner,
                        position: MobileM3ListItemPositionForIndex.forIndex(
                          i - 1,
                          list.length,
                        ),
                        onTap: () => _showStatement(owner),
                      );
                    },
                  );
                },
              ),
            ),
          ),
        ],
      ),
    );
  }
}

// ── Year Selector ─────────────────────────────────────────────────────────────

class _YearSelector extends StatelessWidget {
  const _YearSelector({
    required this.selected,
    required this.years,
    required this.onChanged,
  });

  final int selected;
  final List<int> years;
  final ValueChanged<int> onChanged;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return DropdownButton<int>(
      value: selected,
      underline: const SizedBox.shrink(),
      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
        color: cs.onSurface,
        fontWeight: FontWeight.w600,
      ),
      items: years
          .map((y) => DropdownMenuItem(value: y, child: Text('$y')))
          .toList(),
      onChanged: (v) {
        if (v != null) onChanged(v);
      },
    );
  }
}

// ── Year-end Packet Card ──────────────────────────────────────────────────────

/// Plain-language affordance to download the accountant packet PDF.
class _YearEndPacketCard extends StatelessWidget {
  const _YearEndPacketCard({
    required this.year,
    required this.years,
    required this.onYearChanged,
    required this.onOpen,
  });

  final int year;
  final List<int> years;
  final ValueChanged<int> onYearChanged;
  final VoidCallback onOpen;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return AnimatedContainer(
      duration: M3Motion.medium2,
      curve: M3Motion.emphasizedDecelerate,
      decoration: BoxDecoration(
        color: cs.surfaceContainerHigh,
        borderRadius: M3Shape.radiusLargeIncreased,
        border: Border.all(color: cs.outlineVariant.withValues(alpha: 0.36)),
      ),
      clipBehavior: Clip.antiAlias,
      child: Material(
        color: Colors.transparent,
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  MobileM3LeadingIcon(
                    icon: Icons.picture_as_pdf_outlined,
                    backgroundColor: cs.primaryContainer,
                    foregroundColor: cs.onPrimaryContainer,
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Text(
                      'Year-end packet (PDF)',
                      style: theme.textTheme.titleSmall?.copyWith(
                        fontWeight: FontWeight.w700,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 6),
              Text(
                'Hand your accountant a clean PDF: Schedule E, P&L, '
                'cash flow, rent roll.',
                style: theme.textTheme.bodySmall?.copyWith(
                  color: cs.onSurfaceVariant,
                ),
              ),
              const SizedBox(height: 14),
              Row(
                children: [
                  Text(
                    'Tax year',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: cs.onSurfaceVariant,
                    ),
                  ),
                  const SizedBox(width: 10),
                  DropdownButton<int>(
                    value: year,
                    underline: const SizedBox.shrink(),
                    style: theme.textTheme.bodyMedium?.copyWith(
                      color: cs.onSurface,
                      fontWeight: FontWeight.w600,
                    ),
                    dropdownColor: cs.surface,
                    items: years
                        .map(
                          (y) => DropdownMenuItem(value: y, child: Text('$y')),
                        )
                        .toList(),
                    onChanged: (v) {
                      if (v != null) onYearChanged(v);
                    },
                  ),
                ],
              ),
              const SizedBox(height: 10),
              SizedBox(
                width: double.infinity,
                child: FilledButton.icon(
                  style: FilledButton.styleFrom(
                    minimumSize: const Size.fromHeight(56),
                    textStyle: theme.textTheme.labelLarge,
                  ),
                  onPressed: onOpen,
                  icon: const Icon(Icons.download_outlined, size: 20),
                  label: const Text('Download packet'),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

// ── Owner Summary List Item ───────────────────────────────────────────────────

class _OwnerSummaryListItem extends StatelessWidget {
  const _OwnerSummaryListItem({
    required this.owner,
    required this.position,
    required this.onTap,
  });

  final OwnerSummary owner;
  final MobileM3ListItemPosition position;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final isPositive = owner.netToOwner >= 0;

    return MobileM3ListItem(
      position: position,
      leading: MobileM3LeadingIcon(
        icon: Icons.person_outline,
        backgroundColor: cs.primaryContainer,
        foregroundColor: cs.onPrimaryContainer,
      ),
      title: Text(
        owner.ownerName,
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
        style: theme.textTheme.titleSmall?.copyWith(
          fontWeight: FontWeight.w700,
        ),
      ),
      supporting: [
        Text(
          'Net to owner',
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
      ],
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          Text(
            _fmtCurrency(owner.netToOwner),
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
              color: isPositive ? null : cs.error,
            ),
          ),
          const SizedBox(width: 8),
          Icon(Icons.chevron_right, color: cs.onSurfaceVariant, size: 18),
        ],
      ),
      onTap: onTap,
    );
  }
}

// ── Owner Statement Bottom Sheet ──────────────────────────────────────────────

class _OwnerStatementSheet extends ConsumerWidget {
  const _OwnerStatementSheet({required this.ownerName});

  final String ownerName;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final asyncState = ref.watch(ownerStatementProvider);
    final theme = Theme.of(context);
    final cs = theme.colorScheme;
    final bottomPadding = MediaQuery.viewInsetsOf(context).bottom;

    return Padding(
      padding: EdgeInsets.fromLTRB(0, 20, 0, 20 + bottomPadding),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          // Header
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    ownerName,
                    style: theme.textTheme.titleLarge?.copyWith(
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                IconButton(
                  icon: const Icon(Icons.close),
                  onPressed: () => Navigator.of(context).pop(),
                ),
              ],
            ),
          ),
          const Divider(height: 16),
          Flexible(
            child: asyncState.when(
              loading: () => const Padding(
                padding: EdgeInsets.all(40),
                child: Center(child: CircularProgressIndicator()),
              ),
              error: (e, _) => Padding(
                padding: const EdgeInsets.all(20),
                child: Text(
                  e is ApiException ? e.message : e.toString(),
                  style: TextStyle(color: cs.error),
                  textAlign: TextAlign.center,
                ),
              ),
              data: (stmt) {
                if (stmt == null) {
                  return const Padding(
                    padding: EdgeInsets.all(20),
                    child: Center(child: CircularProgressIndicator()),
                  );
                }
                return SingleChildScrollView(
                  padding: const EdgeInsets.symmetric(horizontal: 20),
                  child: _StatementBody(statement: stmt),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

class _StatementBody extends StatelessWidget {
  const _StatementBody({required this.statement});

  final OwnerStatement statement;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final cs = theme.colorScheme;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        // Year badge
        Text(
          '${statement.year} Annual Statement',
          style: theme.textTheme.bodySmall?.copyWith(
            color: cs.onSurfaceVariant,
          ),
        ),
        const SizedBox(height: 12),

        // Totals card
        Card(
          color: cs.primaryContainer,
          child: Padding(
            padding: const EdgeInsets.all(14),
            child: Column(
              children: [
                _StatRow(
                  label: 'Total Income',
                  value: _fmtCurrency(statement.totalIncome),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                ),
                _StatRow(
                  label: 'Total Expenses',
                  value: _fmtCurrency(statement.totalExpenses),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                ),
                _StatRow(
                  label: 'Management Fee',
                  value: _fmtCurrency(statement.totalManagementFee),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                ),
                const Divider(height: 12),
                _StatRow(
                  label: 'Net to Owner',
                  value: _fmtCurrency(statement.totalNetToOwner),
                  theme: theme,
                  textColor: cs.onPrimaryContainer,
                  bold: true,
                ),
              ],
            ),
          ),
        ),

        const SizedBox(height: 16),

        // Per-property table
        if (statement.properties.isNotEmpty) ...[
          Text(
            'By Property',
            style: theme.textTheme.titleSmall?.copyWith(
              fontWeight: FontWeight.w700,
            ),
          ),
          const SizedBox(height: 8),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(12),
              child: Table(
                columnWidths: const {
                  0: FlexColumnWidth(2),
                  1: FlexColumnWidth(1.5),
                  2: FlexColumnWidth(1.5),
                  3: FlexColumnWidth(1.5),
                },
                children: [
                  TableRow(
                    children: [
                      _TableHeader(text: 'Property'),
                      _TableHeader(text: 'Income', align: TextAlign.right),
                      _TableHeader(text: 'Expenses', align: TextAlign.right),
                      _TableHeader(text: 'Net', align: TextAlign.right),
                    ],
                  ),
                  ...statement.properties.map(
                    (p) => TableRow(
                      children: [
                        _TableCell(text: p.propertyName),
                        _TableCell(
                          text: _fmtCurrency(p.rentalIncome),
                          align: TextAlign.right,
                        ),
                        _TableCell(
                          text: _fmtCurrency(p.expenses),
                          align: TextAlign.right,
                        ),
                        _TableCell(
                          text: _fmtCurrency(p.netToOwner),
                          align: TextAlign.right,
                          bold: true,
                        ),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),
        ],
        const SizedBox(height: 16),
      ],
    );
  }
}

class _StatRow extends StatelessWidget {
  const _StatRow({
    required this.label,
    required this.value,
    required this.theme,
    required this.textColor,
    this.bold = false,
  });

  final String label;
  final String value;
  final ThemeData theme;
  final Color textColor;
  final bool bold;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 3),
      child: Row(
        children: [
          Expanded(
            child: Text(
              label,
              style: theme.textTheme.bodySmall?.copyWith(
                color: textColor.withValues(alpha: 0.8),
              ),
            ),
          ),
          Text(
            value,
            style: theme.textTheme.bodyMedium?.copyWith(
              color: textColor,
              fontWeight: bold ? FontWeight.w700 : null,
            ),
          ),
        ],
      ),
    );
  }
}

class _TableHeader extends StatelessWidget {
  const _TableHeader({required this.text, this.align = TextAlign.left});

  final String text;
  final TextAlign align;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Text(
        text,
        textAlign: align,
        style: Theme.of(context).textTheme.labelSmall?.copyWith(
          color: Theme.of(context).colorScheme.onSurfaceVariant,
        ),
      ),
    );
  }
}

class _TableCell extends StatelessWidget {
  const _TableCell({
    required this.text,
    this.align = TextAlign.left,
    this.bold = false,
  });

  final String text;
  final TextAlign align;
  final bool bold;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Text(
        text,
        textAlign: align,
        style: Theme.of(context).textTheme.bodySmall?.copyWith(
          fontWeight: bold ? FontWeight.w700 : null,
        ),
        overflow: TextOverflow.ellipsis,
      ),
    );
  }
}

// ── Empty / Error ─────────────────────────────────────────────────────────────

class _EmptyBody extends StatelessWidget {
  const _EmptyBody({required this.year});

  final int year;

  @override
  Widget build(BuildContext context) {
    final cs = Theme.of(context).colorScheme;
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.bar_chart_outlined, size: 48, color: cs.onSurfaceVariant),
          const SizedBox(height: 12),
          Text(
            'No owner data for $year',
            style: Theme.of(
              context,
            ).textTheme.titleMedium?.copyWith(color: cs.onSurfaceVariant),
          ),
          const SizedBox(height: 4),
          Text(
            'Try selecting a different year.',
            style: TextStyle(color: cs.onSurfaceVariant),
          ),
        ],
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
