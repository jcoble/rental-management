String formatPropertyType(String type) {
  switch (type) {
    case 'SingleFamily':
      return 'Single-family';
    case 'MultiFamily':
      return 'Multi-family';
    case 'Condo':
      return 'Condo';
    case 'Townhome':
      return 'Townhome';
    case 'Apartment':
      return 'Apartment';
    case 'Commercial':
      return 'Commercial';
    default:
      return type.isEmpty ? 'Property' : type;
  }
}

String loanStatusLabel(String status) => switch (status) {
  'PaidOff' => 'Paid off',
  _ => status,
};

String loanPaymentStatusLabel(String status) => switch (status) {
  'Paid' => 'Paid',
  'Scheduled' => 'Scheduled',
  _ => status,
};
