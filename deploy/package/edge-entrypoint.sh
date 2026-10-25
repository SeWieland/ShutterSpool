#!/bin/sh
set -eu
export SFTPGO_HTTP__HEADERS__0__VALUE="$(cat /run/secrets/camera-callback-key)"
[ -n "$SFTPGO_HTTP__HEADERS__0__VALUE" ] || exit 1
if [ "${SFTPGO_FTPD__BINDINGS__0__TLS_MODE:-0}" != 0 ]; then
  bundle=/etc/sftpgo/certs/server.pem
  root=/etc/sftpgo/certs/root.pem
  : "${CAMERA_HOSTNAME:?Set the ingest certificate hostname}"
  certificate_failure() {
    echo 'The FTPS certificate is missing or invalid; ingress remains closed.' >&2
    exit 1
  }
  temporary_directory=$(mktemp -d) || certificate_failure
  trap 'rm -rf "$temporary_directory"' EXIT
  leaf="$temporary_directory/leaf.pem"
  public_key="$temporary_directory/public.pem"
  awk '/-----BEGIN CERTIFICATE-----/ { in_certificate = 1 } in_certificate { print } /-----END CERTIFICATE-----/ { exit }' "$bundle" > "$leaf" || certificate_failure
  grep -q -- '-----BEGIN CERTIFICATE-----' "$leaf" || certificate_failure

  root_constraints=$(openssl x509 -in "$root" -noout -ext basicConstraints 2>/dev/null) || certificate_failure
  printf '%s\n' "$root_constraints" | grep -q 'CA:TRUE' || certificate_failure
  subject_alt_name=$(openssl x509 -in "$leaf" -noout -ext subjectAltName 2>/dev/null) || certificate_failure
  printf '%s\n' "$subject_alt_name" | grep -q 'DNS:' || certificate_failure
  enhanced_key_usage=$(openssl x509 -in "$leaf" -noout -ext extendedKeyUsage 2>/dev/null) || certificate_failure
  printf '%s\n' "$enhanced_key_usage" | grep -Eq 'TLS Web Server Authentication|serverAuth' || certificate_failure
  key_usage=$(openssl x509 -in "$leaf" -noout -ext keyUsage 2>/dev/null) || certificate_failure
  printf '%s\n' "$key_usage" | grep -q 'Digital Signature' || certificate_failure

  openssl x509 -in "$leaf" -pubkey -noout > "$public_key" 2>/dev/null || certificate_failure
  public_key_details=$(openssl pkey -pubin -in "$public_key" -text -noout 2>/dev/null) || certificate_failure
  if printf '%s\n' "$public_key_details" | grep -q 'Modulus:'; then
    printf '%s\n' "$key_usage" | grep -q 'Key Encipherment' || certificate_failure
  fi
  certificate_key=$(openssl pkey -pubin -in "$public_key" -outform DER 2>/dev/null | openssl dgst -sha256) || certificate_failure
  private_key=$(openssl pkey -in "$bundle" -pubout -outform DER 2>/dev/null | openssl dgst -sha256) || certificate_failure
  [ "$certificate_key" = "$private_key" ] || certificate_failure
  openssl verify -CAfile "$root" -untrusted "$bundle" -purpose sslserver -verify_hostname "$CAMERA_HOSTNAME" "$leaf" >/dev/null 2>&1 || certificate_failure
fi
exec sftpgo serve --log-file-path /dev/null --log-level error
