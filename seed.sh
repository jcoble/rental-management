#!/bin/bash
set -e

API_URL="${API_URL:-http://localhost:5666}"
ORIGIN_HEADER="Origin: http://localhost"

post() {
  curl -s -X POST "$1" -H "Content-Type: application/json" -H "$ORIGIN_HEADER" -d "$2"
}

patch() {
  curl -s -X PATCH "$1" -H "Content-Type: application/json" -H "$ORIGIN_HEADER" -d "$2"
}

post_auth() {
  curl -s -X POST "$1" -H "Content-Type: application/json" -H "$ORIGIN_HEADER" -H "Authorization: Bearer $AUTH_TOKEN" -d "$2"
}

extract_id() {
  echo "$1" | sed -n 's/.*"id":\([0-9]*\).*/\1/p' | head -1
}

extract_token() {
  echo "$1" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p' | head -1
}

PORTFOLIO=$(post "$API_URL/api/portfolios" '{"name":"Downtown Residential Portfolio","description":"Mixed rental portfolio for downtown and nearby neighborhoods","managementCompanyName":"Acme Property Management","timeZone":"America/New_York"}')
PORTFOLIO_ID=$(extract_id "$PORTFOLIO")

OWNER_A=$(post "$API_URL/api/owners" "{\"portfolioId\":$PORTFOLIO_ID,\"name\":\"North Star Holdings\",\"email\":\"owners@northstar.example\",\"phone\":\"(555) 010-1000\"}")
OWNER_A_ID=$(extract_id "$OWNER_A")

OWNER_B=$(post "$API_URL/api/owners" "{\"portfolioId\":$PORTFOLIO_ID,\"name\":\"Elm Street Capital\",\"email\":\"asset@elmstreet.example\",\"phone\":\"(555) 010-2000\"}")
OWNER_B_ID=$(extract_id "$OWNER_B")

PROPERTY_A=$(post "$API_URL/api/properties" "{\"portfolioId\":$PORTFOLIO_ID,\"ownerId\":$OWNER_A_ID,\"name\":\"Maple Gardens\",\"type\":\"MultiFamily\",\"addressLine1\":\"120 Maple Ave\",\"city\":\"Riverton\",\"state\":\"NY\",\"postalCode\":\"10001\"}")
PROPERTY_A_ID=$(extract_id "$PROPERTY_A")

PROPERTY_B=$(post "$API_URL/api/properties" "{\"portfolioId\":$PORTFOLIO_ID,\"ownerId\":$OWNER_B_ID,\"name\":\"Pine View Townhomes\",\"type\":\"Townhome\",\"addressLine1\":\"88 Pine Street\",\"city\":\"Riverton\",\"state\":\"NY\",\"postalCode\":\"10002\"}")
PROPERTY_B_ID=$(extract_id "$PROPERTY_B")

UNIT_A1=$(post "$API_URL/api/properties/$PROPERTY_A_ID/units" '{"unitNumber":"101","bedrooms":2,"bathrooms":1.5,"marketRent":2400,"status":"Occupied"}')
UNIT_A1_ID=$(extract_id "$UNIT_A1")

UNIT_A2=$(post "$API_URL/api/properties/$PROPERTY_A_ID/units" '{"unitNumber":"204","bedrooms":1,"bathrooms":1,"marketRent":1950,"status":"Vacant"}')
UNIT_A2_ID=$(extract_id "$UNIT_A2")

UNIT_B1=$(post "$API_URL/api/properties/$PROPERTY_B_ID/units" '{"unitNumber":"TH-3","bedrooms":3,"bathrooms":2.5,"marketRent":3100,"status":"Occupied"}')
UNIT_B1_ID=$(extract_id "$UNIT_B1")

TENANT_A=$(post "$API_URL/api/tenants" "{\"portfolioId\":$PORTFOLIO_ID,\"firstName\":\"Maya\",\"lastName\":\"Cruz\",\"email\":\"maya.cruz@example.com\",\"phone\":\"(555) 200-3000\"}")
TENANT_A_ID=$(extract_id "$TENANT_A")

TENANT_B=$(post "$API_URL/api/tenants" "{\"portfolioId\":$PORTFOLIO_ID,\"firstName\":\"Jordan\",\"lastName\":\"Lee\",\"email\":\"jordan.lee@example.com\",\"phone\":\"(555) 200-4000\"}")
TENANT_B_ID=$(extract_id "$TENANT_B")

LEASE_A=$(post "$API_URL/api/leases" "{\"portfolioId\":$PORTFOLIO_ID,\"unitId\":$UNIT_A1_ID,\"tenantId\":$TENANT_A_ID,\"status\":\"Active\",\"startDate\":\"2025-08-01\",\"endDate\":\"2026-07-31\",\"monthlyRent\":2400,\"securityDeposit\":2400,\"lateFeeAmount\":125,\"rentDueDay\":1}")
LEASE_A_ID=$(extract_id "$LEASE_A")

LEASE_B=$(post "$API_URL/api/leases" "{\"portfolioId\":$PORTFOLIO_ID,\"unitId\":$UNIT_B1_ID,\"tenantId\":$TENANT_B_ID,\"status\":\"Active\",\"startDate\":\"2025-10-01\",\"endDate\":\"2026-09-30\",\"monthlyRent\":3100,\"securityDeposit\":3100,\"lateFeeAmount\":150,\"rentDueDay\":1}")
LEASE_B_ID=$(extract_id "$LEASE_B")

post "$API_URL/api/payments" "{\"portfolioId\":$PORTFOLIO_ID,\"leaseId\":$LEASE_A_ID,\"type\":\"Rent\",\"status\":\"Paid\",\"amount\":2400,\"dueDate\":\"2026-02-01\",\"paidDate\":\"2026-02-01\",\"method\":\"ACH\"}" >/dev/null
post "$API_URL/api/payments" "{\"portfolioId\":$PORTFOLIO_ID,\"leaseId\":$LEASE_B_ID,\"type\":\"Rent\",\"status\":\"Late\",\"amount\":3100,\"dueDate\":\"2026-02-01\"}" >/dev/null

VENDOR=$(post "$API_URL/api/vendors" "{\"portfolioId\":$PORTFOLIO_ID,\"name\":\"FastFlow Plumbing\",\"serviceType\":\"Plumbing\",\"email\":\"dispatch@fastflow.example\",\"phone\":\"(555) 700-9000\",\"is1099Eligible\":true,\"w9OnFile\":true,\"preferred\":true}")
VENDOR_ID=$(extract_id "$VENDOR")

WORK_ORDER=$(post "$API_URL/api/work-orders" "{\"portfolioId\":$PORTFOLIO_ID,\"propertyId\":$PROPERTY_A_ID,\"unitId\":$UNIT_A1_ID,\"tenantId\":$TENANT_A_ID,\"vendorId\":$VENDOR_ID,\"title\":\"Kitchen sink leak\",\"description\":\"Water leak under sink cabinet\",\"category\":\"Plumbing\",\"priority\":\"High\"}")
WORK_ORDER_ID=$(extract_id "$WORK_ORDER")

post "$API_URL/api/expenses" "{\"portfolioId\":$PORTFOLIO_ID,\"propertyId\":$PROPERTY_A_ID,\"vendorId\":$VENDOR_ID,\"workOrderId\":$WORK_ORDER_ID,\"category\":\"Repairs\",\"description\":\"Sink leak repair\",\"status\":\"Approved\",\"amount\":420,\"incurredAt\":\"2026-02-05\"}" >/dev/null

post "$API_URL/api/appointments" "{\"portfolioId\":$PORTFOLIO_ID,\"propertyId\":$PROPERTY_A_ID,\"unitId\":$UNIT_A2_ID,\"title\":\"Showing - Prospect Alex Kim\",\"type\":\"Showing\",\"status\":\"Scheduled\",\"scheduledStart\":\"2026-02-10T15:00:00Z\",\"prospectName\":\"Alex Kim\",\"prospectEmail\":\"alex.kim@example.com\",\"assignedTo\":\"Leasing Team\"}" >/dev/null

post "$API_URL/api/inspections" "{\"portfolioId\":$PORTFOLIO_ID,\"propertyId\":$PROPERTY_A_ID,\"unitId\":$UNIT_A1_ID,\"type\":\"Routine\",\"status\":\"Scheduled\",\"scheduledFor\":\"2026-02-15T14:00:00Z\",\"notes\":\"Quarterly smoke detector + plumbing check\"}" >/dev/null

# Seed role-based access users
post "$API_URL/api/auth/users" "{\"portfolioId\":$PORTFOLIO_ID,\"email\":\"admin@rental.local\",\"displayName\":\"Admin User\",\"password\":\"Admin123!\",\"role\":\"Admin\"}" >/dev/null

ADMIN_LOGIN=$(post "$API_URL/api/auth/login" "{\"portfolioId\":$PORTFOLIO_ID,\"email\":\"admin@rental.local\",\"password\":\"Admin123!\"}")
AUTH_TOKEN=$(extract_token "$ADMIN_LOGIN")
if [ -z "$AUTH_TOKEN" ]; then
  echo "Failed to obtain admin auth token during seed."
  exit 1
fi

post_auth "$API_URL/api/auth/users" "{\"portfolioId\":$PORTFOLIO_ID,\"email\":\"manager@rental.local\",\"displayName\":\"Manager User\",\"password\":\"Manager123!\",\"role\":\"Manager\"}" >/dev/null
post_auth "$API_URL/api/auth/users" "{\"portfolioId\":$PORTFOLIO_ID,\"email\":\"agent@rental.local\",\"displayName\":\"Leasing Agent\",\"password\":\"Agent123!\",\"role\":\"Agent\"}" >/dev/null
post_auth "$API_URL/api/auth/users" "{\"portfolioId\":$PORTFOLIO_ID,\"email\":\"owner@rental.local\",\"displayName\":\"Owner Portal User\",\"password\":\"Owner123!\",\"role\":\"Owner\",\"ownerId\":$OWNER_A_ID}" >/dev/null
post_auth "$API_URL/api/auth/users" "{\"portfolioId\":$PORTFOLIO_ID,\"email\":\"tenant@rental.local\",\"displayName\":\"Tenant Portal User\",\"password\":\"Tenant123!\",\"role\":\"Tenant\",\"tenantId\":$TENANT_A_ID}" >/dev/null

echo "Seed complete. Portfolio ID: $PORTFOLIO_ID"
echo "Demo users: admin@rental.local / manager@rental.local / agent@rental.local / owner@rental.local / tenant@rental.local"
echo "Default passwords: Admin123! Manager123! Agent123! Owner123! Tenant123!"
