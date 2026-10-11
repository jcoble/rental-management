import 'package:flutter/widgets.dart';
import 'package:material_symbols_icons/symbols.dart';

/// The "Getting started" checklist — the hand-holding to-do list a brand-new
/// (non-technical) landlord works through to go from an empty account to
/// up-and-running. Ported from the web canonical list
/// (`web/src/lib/onboarding/getting-started-tasks.ts`); the labels, plain-English
/// `eli5` copy, and the core-vs-optional split are kept faithful so the two
/// surfaces speak with one voice.
///
/// Rentals are owned by you unless you add another owner or a management company
/// under More details on the rental.
///
/// Each task carries a short label plus one-line `eli5` copy, a
/// [GettingStartedDest] deep-link to the screen that creates the record, and a
/// completion predicate computed purely from data the app already fetches — so a
/// task auto-checks the moment the record exists and there are NO per-task API
/// calls. [GettingStartedSignals] is that small bag of facts.

/// Where tapping a checklist row should take the user. Resolved to a concrete
/// screen by the checklist UI (`getting_started_screen.dart`), which owns the
/// imports — keeping this file free of screen dependencies.
enum GettingStartedDest {
  /// No destination — the task is informational only.
  none,
  properties,
  tenants,
  leases,
  settings,
}

/// The minimal set of facts the checklist needs to auto-check tasks. Every field
/// is derived from a list/settings query the app already runs — the checklist
/// adds no new endpoints. Counts are used (rather than booleans) so a surface can
/// also show "3 properties" style context if it wants.
class GettingStartedSignals {
  const GettingStartedSignals({
    required this.propertyCount,
    required this.unitCount,
    required this.tenantCount,
    required this.leaseCount,
    required this.hasNotificationEmail,
    required this.hasTexting,
    required this.hasAutomations,
  });

  final int propertyCount;

  /// Total units across all properties — `sum(property.unitCount)`, no per-property call.
  final int unitCount;
  final int tenantCount;
  final int leaseCount;

  /// Email delivery for alerts is switched on (a channel preference enables email).
  final bool hasNotificationEmail;

  /// SignalWire / SMS provider credentials are configured.
  final bool hasTexting;

  /// The user configured at least one optional follow-up automation. Rent
  /// charges are not a setting: every active lease drives them automatically
  /// from its chosen rent-tracking start.
  final bool hasAutomations;

  /// Signals with nothing set up yet — used as the value while data is loading
  /// so the checklist renders a consistent "all to-do" baseline (the card hides
  /// itself while loading regardless, so this is never shown half-loaded).
  static const empty = GettingStartedSignals(
    propertyCount: 0,
    unitCount: 0,
    tenantCount: 0,
    leaseCount: 0,
    hasNotificationEmail: false,
    hasTexting: false,
    hasAutomations: false,
  );
}

/// A single checklist task.
class GettingStartedTask {
  const GettingStartedTask({
    required this.key,
    required this.label,
    required this.eli5,
    required this.icon,
    required this.dest,
    required this.core,
    required this.isComplete,
  });

  /// Stable identifier (matches the web task key where one exists).
  final String key;

  /// Short imperative label, e.g. "Add your first property".
  final String label;

  /// One sentence, plain-English: what this is + why it matters. Explain-Like-I'm-5.
  final String eli5;

  /// A `Symbols.*_rounded` glyph for the row.
  final IconData icon;

  /// The mobile screen tapping this row deep-links to (the create flow).
  final GettingStartedDest dest;

  /// Core tasks are the must-do spine and gate "you're all set". The rest are
  /// recommended add-ons that make the system send reminders for you.
  final bool core;

  /// Computes whether the underlying data already exists → task auto-checks.
  final bool Function(GettingStartedSignals s) isComplete;
}

/// The mobile getting-started task set. Order = display order.
const List<GettingStartedTask> kGettingStartedTasks = [
  GettingStartedTask(
    key: 'property',
    label: 'Add your first property',
    eli5:
        'A property is one building or address. Standalone homes get their '
        'rental automatically; larger buildings can add each rental.',
    icon: Symbols.home_rounded,
    dest: GettingStartedDest.properties,
    core: true,
    isComplete: _propertyComplete,
  ),
  GettingStartedTask(
    key: 'unit',
    label: 'Confirm your rentals',
    eli5:
        'A rental is what gets leased. A house is one rental; a duplex is two. '
        'Open a property to add more rentals when needed.',
    icon: Symbols.meeting_room_rounded,
    dest: GettingStartedDest.properties,
    core: true,
    isComplete: _unitComplete,
  ),
  GettingStartedTask(
    key: 'tenant',
    label: 'Add your tenants',
    eli5:
        'Tenants are the people who rent from you. Adding their email or phone '
        'lets the app send them reminders.',
    icon: Symbols.group_rounded,
    dest: GettingStartedDest.tenants,
    core: true,
    isComplete: _tenantComplete,
  ),
  GettingStartedTask(
    key: 'lease',
    label: 'Create the first lease',
    eli5:
        'A lease ties a tenant to a rental and sets the rent, dates, and '
        'deposit. This is what drives rent charges and reminders.',
    icon: Symbols.description_rounded,
    dest: GettingStartedDest.leases,
    core: true,
    isComplete: _leaseComplete,
  ),
  GettingStartedTask(
    key: 'notifications',
    label: 'Set where alerts go',
    eli5:
        'Turn on email so rent reminders and your daily briefing reach your '
        'inbox, not just the app.',
    icon: Symbols.notifications_rounded,
    dest: GettingStartedDest.settings,
    core: false,
    isComplete: _notificationsComplete,
  ),
  GettingStartedTask(
    key: 'automations',
    label: 'Configure optional follow-ups',
    eli5:
        'Rent charges already follow every active lease. Choose any extra late '
        'fees, tenant notices, renewal reminders, maintenance reminders, or '
        'briefings you want.',
    icon: Symbols.tune_rounded,
    dest: GettingStartedDest.settings,
    core: false,
    isComplete: _automationsComplete,
  ),
  // NOTE: "Connect texting (SignalWire)" was intentionally removed from the
  // getting-started checklist (A13) — it's the most technical setup in the app
  // (Project ID / Space URL / API Token / a purchased From number) and is
  // optional/advanced, so it doesn't belong in the newcomer checklist. It
  // remains fully available in Settings. The `hasTexting` signal stays on
  // GettingStartedSignals (the settings screen still reads it).
];

// Predicates kept as top-level functions so the task list can stay `const`.
bool _propertyComplete(GettingStartedSignals s) => s.propertyCount > 0;
bool _unitComplete(GettingStartedSignals s) => s.unitCount > 0;
bool _tenantComplete(GettingStartedSignals s) => s.tenantCount > 0;
bool _leaseComplete(GettingStartedSignals s) => s.leaseCount > 0;
bool _notificationsComplete(GettingStartedSignals s) => s.hasNotificationEmail;
bool _automationsComplete(GettingStartedSignals s) => s.hasAutomations;

/// Progress rollup over the task set for a given snapshot of signals.
class GettingStartedProgress {
  const GettingStartedProgress({
    required this.doneCount,
    required this.totalCount,
    required this.coreDoneCount,
    required this.coreTotalCount,
  });

  final int doneCount;
  final int totalCount;
  final int coreDoneCount;
  final int coreTotalCount;

  /// Every core task is satisfied → the spine is complete.
  bool get allCoreDone => coreDoneCount == coreTotalCount;

  /// Every task (core + optional) is satisfied → hide the card entirely.
  bool get allDone => doneCount == totalCount;
}

GettingStartedProgress computeGettingStartedProgress(
  GettingStartedSignals signals,
) {
  var done = 0;
  var coreDone = 0;
  var coreTotal = 0;
  for (final task in kGettingStartedTasks) {
    final isDone = task.isComplete(signals);
    if (isDone) done++;
    if (task.core) {
      coreTotal++;
      if (isDone) coreDone++;
    }
  }
  return GettingStartedProgress(
    doneCount: done,
    totalCount: kGettingStartedTasks.length,
    coreDoneCount: coreDone,
    coreTotalCount: coreTotal,
  );
}
