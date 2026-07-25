import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/api/api_exception.dart';
import '../../core/api/dio_client.dart';
import '../../core/api/idempotent_mutation.dart';

class TechnicianAssignmentSummary {
  const TechnicianAssignmentSummary({
    required this.id,
    required this.title,
    required this.category,
    required this.status,
    required this.address,
    required this.unit,
    required this.scheduledForUtc,
    required this.scheduledWindowEndUtc,
    required this.updatedAtUtc,
    required this.unreadMessageCount,
  });

  final int id;
  final String title;
  final String category;
  final String status;
  final String address;
  final String? unit;
  final DateTime? scheduledForUtc;
  final DateTime? scheduledWindowEndUtc;
  final DateTime updatedAtUtc;
  final int unreadMessageCount;

  factory TechnicianAssignmentSummary.fromJson(Map<String, dynamic> json) =>
      TechnicianAssignmentSummary(
        id: (json['id'] as num).toInt(),
        title: json['title'] as String? ?? '',
        category: json['category'] as String? ?? '',
        status: json['status'] as String? ?? 'New',
        address: json['address'] as String? ?? '',
        unit: json['unit'] as String?,
        scheduledForUtc: DateTime.tryParse(
          json['scheduledForUtc'] as String? ?? '',
        ),
        scheduledWindowEndUtc: DateTime.tryParse(
          json['scheduledWindowEndUtc'] as String? ?? '',
        ),
        updatedAtUtc:
            DateTime.tryParse(json['updatedAtUtc'] as String? ?? '') ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
        unreadMessageCount: (json['unreadMessageCount'] as num?)?.toInt() ?? 0,
      );
}

class TechnicianAssignmentPage {
  const TechnicianAssignmentPage(
    this.items,
    this.totalCount,
    this.skip,
    this.take,
  );
  final List<TechnicianAssignmentSummary> items;
  final int totalCount;
  final int skip;
  final int take;

  factory TechnicianAssignmentPage.fromJson(Map<String, dynamic> json) {
    final items = (json['items'] as List<dynamic>? ?? const [])
        .whereType<Map<String, dynamic>>()
        .map(TechnicianAssignmentSummary.fromJson)
        .toList();
    return TechnicianAssignmentPage(
      items,
      (json['totalCount'] as num?)?.toInt() ?? items.length,
      (json['skip'] as num?)?.toInt() ?? 0,
      (json['take'] as num?)?.toInt() ?? items.length,
    );
  }
}

class TechnicianWorkEntry {
  const TechnicianWorkEntry({
    required this.id,
    required this.kind,
    this.note,
    this.quantity,
    this.unit,
    this.photoFileId,
    required this.occurredAtUtc,
  });
  final int id;
  final String kind;
  final String? note;
  final double? quantity;
  final String? unit;
  final int? photoFileId;
  final DateTime occurredAtUtc;

  factory TechnicianWorkEntry.fromJson(Map<String, dynamic> json) =>
      TechnicianWorkEntry(
        id: (json['id'] as num).toInt(),
        kind: json['kind'] as String? ?? 'Note',
        note: json['note'] as String?,
        quantity: (json['quantity'] as num?)?.toDouble(),
        unit: json['unit'] as String?,
        photoFileId: (json['photoFileId'] as num?)?.toInt(),
        occurredAtUtc:
            DateTime.tryParse(json['occurredAtUtc'] as String? ?? '') ??
            DateTime.now().toUtc(),
      );
}

class TechnicianMessage {
  const TechnicianMessage({
    required this.id,
    required this.sender,
    required this.body,
    required this.createdAtUtc,
  });
  final int id;
  final String sender;
  final String body;
  final DateTime createdAtUtc;

  factory TechnicianMessage.fromJson(Map<String, dynamic> json) =>
      TechnicianMessage(
        id: (json['id'] as num).toInt(),
        sender: json['sender'] as String? ?? 'Office',
        body: json['body'] as String? ?? '',
        createdAtUtc:
            DateTime.tryParse(json['createdAtUtc'] as String? ?? '') ??
            DateTime.now().toUtc(),
      );
}

class TechnicianAssignmentDetail extends TechnicianAssignmentSummary {
  const TechnicianAssignmentDetail({
    required super.id,
    required super.title,
    required super.category,
    required super.status,
    required super.address,
    required super.unit,
    required super.scheduledForUtc,
    required super.scheduledWindowEndUtc,
    required super.updatedAtUtc,
    required this.description,
    this.accessInstructions,
    this.contactName,
    this.contactPhone,
    this.contactEmail,
    this.conversationId,
    required this.entries,
    required this.messages,
  }) : super(unreadMessageCount: 0);

  final String description;
  final String? accessInstructions;
  final String? contactName;
  final String? contactPhone;
  final String? contactEmail;
  final int? conversationId;
  final List<TechnicianWorkEntry> entries;
  final List<TechnicianMessage> messages;

  factory TechnicianAssignmentDetail.fromJson(Map<String, dynamic> json) =>
      TechnicianAssignmentDetail(
        id: (json['id'] as num).toInt(),
        title: json['title'] as String? ?? '',
        category: json['category'] as String? ?? '',
        status: json['status'] as String? ?? 'New',
        address: json['address'] as String? ?? '',
        unit: json['unit'] as String?,
        scheduledForUtc: DateTime.tryParse(
          json['scheduledForUtc'] as String? ?? '',
        ),
        scheduledWindowEndUtc: DateTime.tryParse(
          json['scheduledWindowEndUtc'] as String? ?? '',
        ),
        updatedAtUtc:
            DateTime.tryParse(json['updatedAtUtc'] as String? ?? '') ??
            DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
        description: json['description'] as String? ?? '',
        accessInstructions: json['accessInstructions'] as String?,
        contactName: json['contactName'] as String?,
        contactPhone: json['contactPhone'] as String?,
        contactEmail: json['contactEmail'] as String?,
        conversationId: (json['conversationId'] as num?)?.toInt(),
        entries: (json['entries'] as List<dynamic>? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(TechnicianWorkEntry.fromJson)
            .toList(),
        messages: (json['messages'] as List<dynamic>? ?? const [])
            .whereType<Map<String, dynamic>>()
            .map(TechnicianMessage.fromJson)
            .toList(),
      );
}

class TechnicianRepository {
  TechnicianRepository(this._dio);
  final Dio _dio;

  Future<TechnicianAssignmentPage> list({
    String area = 'assignments',
    String? search,
    String? status,
    int skip = 0,
    int take = 20,
  }) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/technician/$area',
        queryParameters: {
          'search': search,
          'status': status,
          'openOnly': area == 'assignments',
          'skip': skip,
          'take': take,
        }..removeWhere((_, value) => value == null || value == ''),
      );
      return TechnicianAssignmentPage.fromJson(response.data ?? const {});
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<TechnicianAssignmentDetail> detail(int id) async {
    try {
      final response = await _dio.get<Map<String, dynamic>>(
        '/technician/assignments/$id',
      );
      return TechnicianAssignmentDetail.fromJson(response.data ?? const {});
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<void> update(
    int id,
    DateTime expectedUpdatedAtUtc, {
    String? status,
    String? note,
  }) => IdempotentMutation.run('technician-update-$id-$status-$note', (
    key,
  ) async {
    try {
      await _dio.patch<void>(
        '/technician/assignments/$id',
        data: {
          'expectedUpdatedAtUtc': expectedUpdatedAtUtc
              .toUtc()
              .toIso8601String(),
          'status': status,
          'technicianNote': note,
        }..removeWhere((_, value) => value == null || value == ''),
        options: Options(headers: {'Idempotency-Key': key}),
      );
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  });

  Future<void> addEntry(
    int id, {
    required String kind,
    String? note,
    double? quantity,
    String? unit,
    int? photoFileId,
  }) => IdempotentMutation.run(
    'technician-entry-$id-$kind-$note-$quantity-$photoFileId',
    (key) async {
      try {
        await _dio.post<void>(
          '/technician/assignments/$id/entries',
          data: {
            'kind': kind,
            'note': note,
            'quantity': quantity,
            'unit': unit,
            'photoFileId': photoFileId,
          }..removeWhere((_, value) => value == null || value == ''),
          options: Options(headers: {'Idempotency-Key': key}),
        );
      } on DioException catch (error) {
        throw ApiException.fromDioException(error);
      }
    },
  );

  Future<int> uploadPhoto(int id, File file) async {
    try {
      final response = await _dio.post<Map<String, dynamic>>(
        '/documents',
        data: FormData.fromMap({
          'file': await MultipartFile.fromFile(file.path),
          'entityType': 'WorkOrder',
          'entityId': id,
          'category': 'Technician photo',
          'clientOperationId': DateTime.now().microsecondsSinceEpoch.toString(),
        }),
      );
      return (response.data?['id'] as num).toInt();
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<Uint8List> downloadPhoto(int fileId) async {
    try {
      final response = await _dio.get<List<int>>(
        '/documents/$fileId/file',
        options: Options(responseType: ResponseType.bytes),
      );
      return Uint8List.fromList(response.data ?? const []);
    } on DioException catch (error) {
      throw ApiException.fromDioException(error);
    }
  }

  Future<void> sendMessage(int id, String body) =>
      IdempotentMutation.run('technician-message-$id-$body', (key) async {
        try {
          await _dio.post<void>(
            '/technician/assignments/$id/messages',
            data: {'body': body},
            options: Options(headers: {'Idempotency-Key': key}),
          );
        } on DioException catch (error) {
          throw ApiException.fromDioException(error);
        }
      });

  Future<void> markConversationRead(int id) =>
      IdempotentMutation.run('technician-conversation-read-$id', (key) async {
        try {
          await _dio.post<void>(
            '/technician/assignments/$id/conversation/read',
            options: Options(headers: {'Idempotency-Key': key}),
          );
        } on DioException catch (error) {
          throw ApiException.fromDioException(error);
        }
      });
}

final technicianRepositoryProvider = Provider<TechnicianRepository>(
  (ref) => TechnicianRepository(ref.watch(dioProvider)),
);
