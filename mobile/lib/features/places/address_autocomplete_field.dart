import 'dart:async';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'places_repository.dart';

/// Street-address field with optional Google Places autocomplete, mirroring the web
/// AddressAutocomplete.svelte behavior on the SAME server-side proxy (no client key, no SDK).
/// Manual entry always works; the dropdown only appears when the backend returns suggestions.
class AddressAutocompleteField extends ConsumerStatefulWidget {
  const AddressAutocompleteField({
    super.key,
    required this.controller,
    this.onResolved,
    this.hintText = 'Street address',
    this.label,
    this.testKey,
  });

  final TextEditingController controller;
  final void Function(ResolvedAddress address)? onResolved;
  final String hintText;
  final String? label;
  final String? testKey;

  @override
  ConsumerState<AddressAutocompleteField> createState() => _AddressAutocompleteFieldState();
}

class _AddressAutocompleteFieldState extends ConsumerState<AddressAutocompleteField> {
  final _layerLink = LayerLink();
  final _focusNode = FocusNode();
  OverlayEntry? _overlay;
  Timer? _debounce;
  List<PlaceSuggestion> _suggestions = const [];
  String _session = DateTime.now().microsecondsSinceEpoch.toString();
  int _seq = 0;

  @override
  void dispose() {
    _debounce?.cancel();
    _removeOverlay();
    _focusNode.dispose();
    super.dispose();
  }

  void _onChanged(String value) {
    _debounce?.cancel();
    if (value.trim().length < 3) {
      _setSuggestions(const []);
      return;
    }
    _debounce = Timer(const Duration(milliseconds: 250), () => _fetch(value));
  }

  Future<void> _fetch(String query) async {
    final mySeq = ++_seq;
    final results = await ref.read(placesRepositoryProvider).autocomplete(query, _session);
    if (!mounted || mySeq != _seq) return;
    _setSuggestions(results);
  }

  void _setSuggestions(List<PlaceSuggestion> s) {
    setState(() => _suggestions = s);
    if (s.isEmpty) {
      _removeOverlay();
    } else {
      _showOverlay();
    }
  }

  Future<void> _pick(PlaceSuggestion s) async {
    widget.controller.text = s.primary;
    _removeOverlay();
    final resolved = await ref.read(placesRepositoryProvider).details(s.placeId, _session);
    _session = DateTime.now().microsecondsSinceEpoch.toString(); // details closes the billing session
    if (resolved != null && mounted) {
      if (resolved.line1.isNotEmpty) widget.controller.text = resolved.line1;
      widget.onResolved?.call(resolved);
    }
  }

  void _showOverlay() {
    _removeOverlay();
    final overlay = OverlayEntry(
      builder: (context) => Positioned(
        width: MediaQuery.of(context).size.width - 32,
        child: CompositedTransformFollower(
          link: _layerLink,
          showWhenUnlinked: false,
          offset: const Offset(0, 56),
          child: Material(
            elevation: 4,
            borderRadius: BorderRadius.circular(8),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 240),
              child: ListView.builder(
                padding: EdgeInsets.zero,
                shrinkWrap: true,
                itemCount: _suggestions.length,
                itemBuilder: (context, i) {
                  final s = _suggestions[i];
                  return ListTile(
                    dense: true,
                    title: Text(s.primary),
                    subtitle: s.secondary.isEmpty ? null : Text(s.secondary),
                    onTap: () => _pick(s),
                  );
                },
              ),
            ),
          ),
        ),
      ),
    );
    Overlay.of(context).insert(overlay);
    _overlay = overlay;
  }

  void _removeOverlay() {
    _overlay?.remove();
    _overlay = null;
  }

  @override
  Widget build(BuildContext context) {
    return CompositedTransformTarget(
      link: _layerLink,
      child: TextField(
        key: widget.testKey == null ? null : Key(widget.testKey!),
        controller: widget.controller,
        focusNode: _focusNode,
        decoration: InputDecoration(
          labelText: widget.label,
          hintText: widget.hintText,
          border: const OutlineInputBorder(),
        ),
        onChanged: _onChanged,
      ),
    );
  }
}
