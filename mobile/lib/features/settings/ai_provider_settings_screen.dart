import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import 'ai_provider_repository.dart';

class AiProviderSettingsScreen extends ConsumerStatefulWidget {
  const AiProviderSettingsScreen({super.key});

  @override
  ConsumerState<AiProviderSettingsScreen> createState() =>
      _AiProviderSettingsScreenState();
}

class _AiProviderSettingsScreenState
    extends ConsumerState<AiProviderSettingsScreen> {
  final _modelController = TextEditingController(text: 'gpt-4o');
  final _keyController = TextEditingController();
  late Future<AiProviderStatus> _status;
  AiProvider _provider = AiProvider.openai;
  String? _testedSignature;
  bool _working = false;

  AiProviderRepository get _repository =>
      ref.read(aiProviderRepositoryProvider);

  String get _signature =>
      '${_provider.name}:${_modelController.text.trim()}:${_keyController.text.trim()}';

  @override
  void initState() {
    super.initState();
    _status = _repository.getStatus();
    _modelController.addListener(_clearTest);
    _keyController.addListener(_clearTest);
  }

  @override
  void dispose() {
    _modelController
      ..removeListener(_clearTest)
      ..dispose();
    _keyController
      ..removeListener(_clearTest)
      ..dispose();
    super.dispose();
  }

  void _clearTest() {
    if (_testedSignature != null) setState(() => _testedSignature = null);
  }

  void _selectProvider(AiProvider? provider) {
    if (provider == null || provider == _provider) return;
    setState(() {
      _provider = provider;
      _modelController.text = provider == AiProvider.openai
          ? 'gpt-4o'
          : 'claude-3-5-sonnet-latest';
      _keyController.clear();
      _testedSignature = null;
    });
  }

  Future<void> _test() async {
    setState(() => _working = true);
    try {
      final result = await _repository.test(
        provider: _provider,
        modelId: _modelController.text,
        apiKey: _keyController.text,
      );
      if (!mounted) return;
      if (!result.succeeded) {
        _show(result.error ?? 'The provider rejected this credential.');
        return;
      }
      setState(() => _testedSignature = _signature);
      _show('Credential verified. You can now save it.');
    } on ApiException catch (error) {
      if (mounted) _show(error.message);
    } finally {
      if (mounted) setState(() => _working = false);
    }
  }

  Future<void> _save(bool configured) async {
    if (_testedSignature != _signature) return;
    setState(() => _working = true);
    try {
      final next = configured
          ? await _repository.rotate(
              provider: _provider,
              modelId: _modelController.text,
              apiKey: _keyController.text,
            )
          : await _repository.activate(
              provider: _provider,
              modelId: _modelController.text,
              apiKey: _keyController.text,
            );
      if (!mounted) return;
      _keyController.clear();
      setState(() {
        _status = Future.value(next);
        _testedSignature = null;
      });
      _show('Workspace AI provider saved.');
    } on ApiException catch (error) {
      if (mounted) _show(error.message);
    } finally {
      if (mounted) setState(() => _working = false);
    }
  }

  Future<void> _remove() async {
    setState(() => _working = true);
    try {
      await _repository.remove();
      if (!mounted) return;
      _keyController.clear();
      setState(() {
        _status = Future.value(
          const AiProviderStatus(
            configured: false,
            provider: null,
            modelId: null,
            lastTestedAtUtc: null,
            updatedAtUtc: null,
          ),
        );
        _testedSignature = null;
      });
      _show('Workspace AI credential removed.');
    } on ApiException catch (error) {
      if (mounted) _show(error.message);
    } finally {
      if (mounted) setState(() => _working = false);
    }
  }

  void _show(String message) => ScaffoldMessenger.of(
    context,
  ).showSnackBar(SnackBar(content: Text(message)));

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('AI provider')),
    body: FutureBuilder<AiProviderStatus>(
      future: _status,
      builder: (context, snapshot) {
        if (snapshot.connectionState == ConnectionState.waiting) {
          return const Center(child: CircularProgressIndicator());
        }
        if (snapshot.hasError || snapshot.data == null) {
          return Center(
            child: FilledButton.tonal(
              onPressed: () =>
                  setState(() => _status = _repository.getStatus()),
              child: const Text('Retry'),
            ),
          );
        }
        final status = snapshot.data!;
        return ListView(
          padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
          children: [
            Card(
              child: ListTile(
                title: Text(
                  status.configured ? 'Configured' : 'Not configured',
                ),
                subtitle: Text(
                  status.configured
                      ? '${status.provider!.name} · ${status.modelId}'
                      : 'Scan / Add stops with a clear missing-key message. '
                            'It never uses a shared Rental Command key.',
                ),
              ),
            ),
            const SizedBox(height: 16),
            DropdownButtonFormField<AiProvider>(
              initialValue: _provider,
              decoration: const InputDecoration(labelText: 'Provider'),
              items: const [
                DropdownMenuItem(
                  value: AiProvider.openai,
                  child: Text('OpenAI'),
                ),
                DropdownMenuItem(
                  value: AiProvider.anthropic,
                  child: Text('Anthropic'),
                ),
              ],
              onChanged: _working ? null : _selectProvider,
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _modelController,
              enabled: !_working,
              decoration: const InputDecoration(labelText: 'Model'),
            ),
            const SizedBox(height: 12),
            TextField(
              controller: _keyController,
              enabled: !_working,
              obscureText: true,
              autocorrect: false,
              enableSuggestions: false,
              decoration: InputDecoration(
                labelText: 'API key',
                hintText: status.configured
                    ? 'Enter a replacement key'
                    : 'Paste a key',
                helperText: 'Encrypted at rest and write-only after save.',
              ),
            ),
            const SizedBox(height: 16),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                OutlinedButton(
                  onPressed:
                      _working ||
                          _modelController.text.trim().isEmpty ||
                          _keyController.text.trim().isEmpty
                      ? null
                      : _test,
                  child: const Text('Test credential'),
                ),
                FilledButton(
                  onPressed: _working || _testedSignature != _signature
                      ? null
                      : () => _save(status.configured),
                  child: Text(
                    status.configured
                        ? 'Rotate credential'
                        : 'Activate provider',
                  ),
                ),
                if (status.configured)
                  FilledButton.tonal(
                    onPressed: _working ? null : _remove,
                    child: const Text('Remove provider'),
                  ),
              ],
            ),
          ],
        );
      },
    ),
  );
}
