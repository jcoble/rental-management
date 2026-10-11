const noticeContentCorrectionMessage =
    'Review the notice and remove any unfinished merge fields or internal instructions before sending.';

final RegExp _reservedMergeResidue = RegExp(r'\{[^{}]+\}');

String? noticeContentSafetyIssue({
  required String subject,
  required String body,
}) {
  if (_isUnsafeNoticeText(subject) || _isUnsafeNoticeText(body)) {
    return noticeContentCorrectionMessage;
  }
  return null;
}

bool _isUnsafeNoticeText(String value) =>
    value.toLowerCase().contains('workspace administrator:') ||
    _reservedMergeResidue.hasMatch(value);
