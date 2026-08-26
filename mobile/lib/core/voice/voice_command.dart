/// Voice command model + deep-link parser.
///
/// This is the Dart side of the **Google App Actions** bridge (TSK-26). Google
/// Assistant recognizes a spoken phrase, matches it against a capability we
/// declared in `android/app/src/main/res/xml/shortcuts.xml`, and fires an
/// Android `VIEW` intent carrying a deep link such as:
///
///   `rentalcommand://voice/log-expense?amount=40&category=plumbing&property=123%20Main`
///
/// The native intent is delivered to the app via the `app_links` plugin (see
/// [VoiceLinkService]); [parseVoiceCommand] turns the [Uri] into a typed
/// [VoiceCommand] that the UI can act on. Keeping this layer as plain,
/// dependency-free Dart makes it trivially unit-testable and means the exact
/// same code path runs whether the link came from Assistant, an `adb` test, or
/// a future iOS Siri Shortcut.
library;

/// The set of actions a spoken command can map to.
///
/// Each value corresponds to one `<capability>` in `shortcuts.xml` and one
/// `rentalcommand://voice/<action>` deep link.
enum VoiceAction {
  /// Open the document scan/capture flow (the flagship "computer does the
  /// typing for you" intake).
  scanDocument,

  /// Begin logging an expense. Carries optional [VoiceCommand.amount],
  /// [VoiceCommand.category] and [VoiceCommand.property]. Routes to the capture
  /// flow so voice and tap converge on one code path.
  logExpense,

  /// Show rent that is past due.
  showOverdueRent,

  /// Open the maintenance work-order list. Carries optional
  /// [VoiceCommand.unit].
  openWorkOrders,
}

/// A parsed, typed voice command ready for the UI to handle.
class VoiceCommand {
  const VoiceCommand(
    this.action, {
    this.amount,
    this.category,
    this.property,
    this.unit,
    this.rawUri,
  });

  final VoiceAction action;

  /// Expense amount in dollars, when the command carried one.
  final double? amount;

  /// Free-text expense category/vendor keyword (e.g. "plumbing").
  final String? category;

  /// Free-text property keyword the landlord spoke (e.g. "123 Main").
  final String? property;

  /// Free-text unit keyword (e.g. "4" / "4B").
  final String? unit;

  /// The original deep-link URI, kept for logging/debugging.
  final String? rawUri;

  /// A short, friendly confirmation of what was understood, shown to the
  /// landlord (a non-technical user) so a misheard command is obvious.
  String get understoodSummary {
    switch (action) {
      case VoiceAction.scanDocument:
        return 'Opening document scan…';
      case VoiceAction.logExpense:
        final parts = <String>[
          'Heard:',
          'log',
          if (amount != null) '\$${amount!.toStringAsFixed(2)}',
          if (category != null && category!.isNotEmpty) category!,
          'expense',
          if (property != null && property!.isNotEmpty) 'for $property',
        ];
        return '${parts.join(' ')}. Snap the receipt to confirm.';
      case VoiceAction.showOverdueRent:
        return 'Showing overdue rent…';
      case VoiceAction.openWorkOrders:
        return unit != null && unit!.isNotEmpty
            ? 'Opening repairs for unit $unit…'
            : 'Opening repairs…';
    }
  }

  @override
  String toString() =>
      'VoiceCommand($action, amount: $amount, category: $category, '
      'property: $property, unit: $unit)';
}

/// Parses a deep-link [uri] into a [VoiceCommand], or returns `null` if the URI
/// is not a recognized voice command.
///
/// Accepts two shapes so the same parser serves both the custom scheme we use
/// today and future verified App Links on the production domain:
///
///   * custom scheme — `rentalcommand://voice/<action>?…` (host == `voice`)
///   * https app link — `https://rentalcommand.net/voice/<action>?…`
///
/// Action matching is lenient (case-insensitive, `-`/`_` interchangeable, with
/// natural-language aliases) because the spoken-phrase → action mapping is
/// declared in `shortcuts.xml` and we want small wording drift to still land.
VoiceCommand? parseVoiceCommand(Uri uri) {
  final actionStr = _extractActionSegment(uri);
  if (actionStr == null) return null;

  final action = _actionFromString(actionStr);
  if (action == null) return null;

  final q = uri.queryParameters;
  return VoiceCommand(
    action,
    amount: _parseAmount(q['amount']),
    category: _clean(q['category'] ?? q['vendor']),
    property: _clean(q['property']),
    unit: _clean(q['unit']),
    rawUri: uri.toString(),
  );
}

/// Finds the `<action>` segment that follows `voice` in the URI, supporting
/// both `rentalcommand://voice/<action>` (host == voice) and `…/voice/<action>`
/// (path-based). Falls back to an `?action=` query param.
String? _extractActionSegment(Uri uri) {
  final segments = uri.pathSegments.where((s) => s.isNotEmpty).toList();

  if (uri.host == 'voice' && segments.isNotEmpty) {
    return segments.first;
  }

  final voiceIdx = segments.indexOf('voice');
  if (voiceIdx >= 0 && voiceIdx + 1 < segments.length) {
    return segments[voiceIdx + 1];
  }

  final fromQuery = uri.queryParameters['action'];
  if (fromQuery != null && fromQuery.isNotEmpty) return fromQuery;

  return null;
}

VoiceAction? _actionFromString(String raw) {
  final key = raw.trim().toLowerCase().replaceAll('_', '-');
  switch (key) {
    case 'scan':
    case 'scan-document':
    case 'capture':
    case 'capture-document':
      return VoiceAction.scanDocument;
    case 'log-expense':
    case 'expense':
    case 'add-expense':
    case 'record-expense':
    case 'log-cost':
      return VoiceAction.logExpense;
    case 'overdue-rent':
    case 'overdue':
    case 'late-rent':
    case 'rent-status':
      return VoiceAction.showOverdueRent;
    case 'work-orders':
    case 'work-order':
    case 'maintenance':
    case 'repairs':
      return VoiceAction.openWorkOrders;
    default:
      return null;
  }
}

/// Parses an amount that may arrive as "40", "$40", "40.00", "1,200" or a
/// spelled value Assistant left as text. Returns `null` when not numeric.
double? _parseAmount(String? raw) {
  if (raw == null) return null;
  final cleaned = raw.replaceAll(RegExp(r'[^0-9.]'), '');
  if (cleaned.isEmpty) return null;
  return double.tryParse(cleaned);
}

/// Trims a free-text param and collapses empty strings to `null`.
String? _clean(String? raw) {
  if (raw == null) return null;
  final trimmed = raw.trim();
  return trimmed.isEmpty ? null : trimmed;
}
