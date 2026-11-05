param([Parameter(Mandatory)][string]$KeycloakContainer)
$ErrorActionPreference = 'Stop'

# Exercise the mounted reconciliation script in a uniquely named disposable realm.
# Existing realms, accounts, credentials and application data are not altered.
$probe = @'
set -eu
realm=shutterspool-check-__NONCE__
work=$(mktemp -d /tmp/shutterspool-keycloak-check.XXXXXX)
created=false
kcadm() { /opt/keycloak/bin/kcadm.sh "$@" --config "$work/kcadm.config"; }
cleanup() {
    if [ "$created" = true ]; then kcadm delete "realms/$realm" >/dev/null; fi
    rm -f "$work/kcadm.config" "$work/reconcile.sh" "$work/select-login-theme.sh" "$work/failed-set-password" "$work/shutterspool-realm.json" "$work/shutterspool-user-profile.json" "$work/shutterspool-email-verified-mapper.json" "$work/shutterspool-server-client.json" "$work/shutterspool-web-client.json" "$work/shutterspool-server-audience-mapper.json"
    rmdir "$work"
}
trap cleanup EXIT
export KEYCLOAK_ADMIN="$KC_BOOTSTRAP_ADMIN_USERNAME"
KEYCLOAK_ADMIN_PASSWORD=${KC_BOOTSTRAP_ADMIN_PASSWORD:-}
[ -n "$KEYCLOAK_ADMIN_PASSWORD" ] || KEYCLOAK_ADMIN_PASSWORD=$(cat /run/secrets/keycloak-admin-password)
export KEYCLOAK_ADMIN_PASSWORD
export SHUTTERSPOOL_ADMIN_USERNAME=bootstrap-check
export SHUTTERSPOOL_ADMIN_PASSWORD='synthetic-check-password-1!'
export SHUTTERSPOOL_ADMIN_SUBJECT=6c178745-3db4-47a7-8bb7-17c8e5e89337
cp /opt/shutterspool/shutterspool-*-*.json "$work/"
sed "s/\"shutterspool\"/\"$realm\"/" /opt/shutterspool/shutterspool-realm.json > "$work/shutterspool-realm.json"
printf 'kcadm() { if [ "$1" = set-password ] && [ ! -e "%s/failed-set-password" ]; then : > "%s/failed-set-password"; return 1; fi; /opt/keycloak/bin/kcadm.sh "$@" --config "%s/kcadm.config"; }\n' "$work" "$work" "$work" > "$work/reconcile.sh"
sed -e "s#http://keycloak:8080#http://localhost:8080#g" -e "s#-r shutterspool#-r $realm#g" -e "s#realms/shutterspool#realms/$realm#g" -e "s#/opt/shutterspool/#$work/#g" -e "s#/opt/keycloak/bin/kcadm.sh#kcadm#g" /opt/shutterspool/reconcile.sh >> "$work/reconcile.sh"
printf 'kcadm() { /opt/keycloak/bin/kcadm.sh "$@" --config "%s/kcadm.config"; }\n' "$work" > "$work/select-login-theme.sh"
sed -e "s#realms/shutterspool#realms/$realm#g" -e "s#/bin/sh /opt/keycloak/bin/kcadm.sh#kcadm#g" /opt/shutterspool/select-login-theme.sh >> "$work/select-login-theme.sh"
kcadm config credentials --server http://localhost:8080 --realm master --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null
kcadm create realms -s realm="$realm" -s enabled=true -s registrationAllowed=true -s verifyEmail=false -s resetPasswordAllowed=false -s actionTokenGeneratedByUserLifespan=137 -s actionTokenGeneratedByAdminLifespan=211 -s 'smtpServer={"host":"preserved.invalid","from":"fixture@example.invalid"}' >/dev/null
created=true
kcadm update authentication/required-actions/VERIFY_EMAIL -r "$realm" -s enabled=false
kcadm update authentication/required-actions/UPDATE_PASSWORD -r "$realm" -s enabled=false
kcadm create users -r "$realm" -s username=prefix-bootstrap-check -s enabled=true >/dev/null
if sh "$work/reconcile.sh" >/dev/null 2>&1; then printf 'Injected initial password failure was not observed.\n' >&2; exit 1; fi
actual=$(kcadm get users -r "$realm" -q username="$SHUTTERSPOOL_ADMIN_USERNAME" -q exact=true --fields id | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
[ "$actual" = "$SHUTTERSPOOL_ADMIN_SUBJECT" ] || { printf 'Interrupted bootstrap did not retain the configured subject.\n' >&2; exit 1; }
credentials=$(kcadm get "users/$actual/credentials" -r "$realm" --fields type)
if printf '%s' "$credentials" | grep -Eq '"type"[[:space:]]*:[[:space:]]*"password"'; then printf 'Injected failure unexpectedly left a password credential.\n' >&2; exit 1; fi
if sh "$work/reconcile.sh" >/dev/null 2>&1; then printf 'Passwordless existing account was accepted for automatic recovery.\n' >&2; exit 1; fi
credentials=$(kcadm get "users/$actual/credentials" -r "$realm" --fields type)
if printf '%s' "$credentials" | grep -Eq '"type"[[:space:]]*:[[:space:]]*"password"'; then printf 'Fail-closed reconciliation changed the passwordless account.\n' >&2; exit 1; fi
# Model an operator explicitly repairing interrupted bootstrap through Keycloak.
kcadm set-password -r "$realm" --username "$SHUTTERSPOOL_ADMIN_USERNAME" --new-password "$SHUTTERSPOOL_ADMIN_PASSWORD"
sh "$work/reconcile.sh" >/dev/null
policy=$(kcadm get "realms/$realm")
printf '%s' "$policy" | grep -Eq '"registrationAllowed"[[:space:]]*:[[:space:]]*false' || { printf 'Public registration was not disabled.\n' >&2; exit 1; }
printf '%s' "$policy" | grep -Eq '"verifyEmail"[[:space:]]*:[[:space:]]*true' || { printf 'Realm email verification was not required.\n' >&2; exit 1; }
printf '%s' "$policy" | grep -Eq '"resetPasswordAllowed"[[:space:]]*:[[:space:]]*false' || { printf 'Disabled operator recovery policy changed.\n' >&2; exit 1; }
printf '%s' "$policy" | grep -Eq '"actionTokenGeneratedByUserLifespan"[[:space:]]*:[[:space:]]*137' || { printf 'User recovery lifetime changed.\n' >&2; exit 1; }
printf '%s' "$policy" | grep -Eq '"actionTokenGeneratedByAdminLifespan"[[:space:]]*:[[:space:]]*211' || { printf 'Native administration lifetime changed.\n' >&2; exit 1; }
printf '%s' "$policy" | grep -q preserved.invalid || { printf 'Operator SMTP configuration changed.\n' >&2; exit 1; }
for action in VERIFY_EMAIL UPDATE_PASSWORD; do
    kcadm get "authentication/required-actions/$action" -r "$realm" | grep -Eq '"enabled"[[:space:]]*:[[:space:]]*true' || { printf 'Native setup action was not enabled.\n' >&2; exit 1; }
done
web_client=$(kcadm get clients -r "$realm" -q clientId=shutterspool-web --fields id | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
kcadm get "clients/$web_client/protocol-mappers/models" -r "$realm" | grep -q shutterspool-email-verified || { printf 'Explicit email verification mapper was not installed.\n' >&2; exit 1; }
credentials=$(kcadm get "users/$actual/credentials" -r "$realm" --fields type)
printf '%s' "$credentials" | grep -Eq '"type"[[:space:]]*:[[:space:]]*"password"' || { printf 'Operator credential recovery was not recognized.\n' >&2; exit 1; }
kcadm create roles -r "$realm" -s name=operator-maintained >/dev/null
kcadm add-roles -r "$realm" --uusername "$SHUTTERSPOOL_ADMIN_USERNAME" --rolename operator-maintained >/dev/null
credentials=$(kcadm get "users/$actual/credentials" -r "$realm" --fields id)
export SHUTTERSPOOL_ADMIN_PASSWORD='synthetic-check-password-2!'
kcadm update "realms/$realm" -s resetPasswordAllowed=true >/dev/null
sh "$work/reconcile.sh" >/dev/null
kcadm get "realms/$realm" | grep -Eq '"resetPasswordAllowed"[[:space:]]*:[[:space:]]*true' || { printf 'Enabled operator recovery policy changed.\n' >&2; exit 1; }
kcadm get "users/$actual/role-mappings/realm" -r "$realm" | grep -q operator-maintained || { printf 'Repeated reconciliation removed an operator grant.\n' >&2; exit 1; }
[ "$(kcadm get "users/$actual/credentials" -r "$realm" --fields id)" = "$credentials" ] || { printf 'Repeated reconciliation replaced the existing password.\n' >&2; exit 1; }
export SHUTTERSPOOL_ADMIN_SUBJECT=6c178745-3db4-47a7-8bb7-17c8e5e89338
export SHUTTERSPOOL_ADMIN_PASSWORD='synthetic-check-password-2!'
if sh "$work/reconcile.sh" >/dev/null 2>&1; then printf 'Mismatched bootstrap subject was accepted.\n' >&2; exit 1; fi
retained=$(kcadm get users -r "$realm" -q username="$SHUTTERSPOOL_ADMIN_USERNAME" -q exact=true --fields id | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
[ "$retained" = "$actual" ] || { printf 'Existing bootstrap identity changed.\n' >&2; exit 1; }
kcadm get "users/$actual/role-mappings/realm" -r "$realm" | grep -q operator-maintained || { printf 'Repeated reconciliation removed an operator grant.\n' >&2; exit 1; }
[ "$(kcadm get "users/$actual/credentials" -r "$realm" --fields id)" = "$credentials" ] || { printf 'Existing bootstrap credentials changed on mismatch.\n' >&2; exit 1; }
printf 'Keycloak check passed: native verification/registration policy and actions reconciled, explicit verification mapper installed, SMTP/recovery lifetimes preserved, interrupted bootstrap failed closed, operator credential recovery enabled reconciliation, partial username match preserved, repeated reconciliation succeeded, mismatched subject rejected, existing identity and credentials retained.\n'
'@
$probe = $probe.Replace('__NONCE__', [Guid]::NewGuid().ToString('N')).Replace("`r`n", "`n")
$output = $probe | docker exec -i $KeycloakContainer /bin/sh -s 2>&1
if ($LASTEXITCODE -ne 0) {
    $failure = $output | Where-Object { $_ -match '^(Fresh bootstrap|Mismatched bootstrap|Existing bootstrap)|not found|No such file|Permission denied|Syntax error|Illegal option' }
    throw $(if ($failure) { $failure -join ' ' } else { 'Keycloak bootstrap check failed; inspect the controlled container privately.' })
}
$output | Where-Object { $_ -like 'Keycloak check passed:*' }

# Qualify a newly created realm, including the stricter email setting used by enrollment.
# Its initial Owner has no email address; no SMTP server is configured.
$realm = 'shutterspool-fresh-' + [Guid]::NewGuid().ToString('N')
$freshProbe = @'
set -eu
realm=__REALM__
work=$(mktemp -d /tmp/shutterspool-keycloak-fresh.XXXXXX)
cleanup_realm=false
kcadm() { /opt/keycloak/bin/kcadm.sh "$@" --config "$work/kcadm.config"; }
cleanup() {
    result=$?
    trap - EXIT
    set +e
    cleanup_failed=false
    if [ "$cleanup_realm" = true ]; then
        realms=$(kcadm get realms --fields realm)
        if [ "$?" -ne 0 ]; then cleanup_failed=true
        elif printf '%s' "$realms" | grep -Eq "\"realm\"[[:space:]]*:[[:space:]]*\"$realm\""; then
            kcadm delete "realms/$realm" >/dev/null || cleanup_failed=true
        fi
    fi
    rm -f "$work/kcadm.config" "$work/reconcile.sh" "$work/select-login-theme.sh" "$work/shutterspool-realm.json" "$work/shutterspool-user-profile.json" "$work/shutterspool-server-client.json" "$work/shutterspool-web-client.json" "$work/shutterspool-server-audience-mapper.json" "$work/shutterspool-email-verified-mapper.json" || cleanup_failed=true
    rmdir "$work" || cleanup_failed=true
    if [ "$cleanup_failed" = true ]; then
        printf 'Fresh bootstrap disposable cleanup failed.\n' >&2
        [ "$result" -ne 0 ] || result=1
    fi
    exit "$result"
}
trap cleanup EXIT
export KEYCLOAK_ADMIN="$KC_BOOTSTRAP_ADMIN_USERNAME"
KEYCLOAK_ADMIN_PASSWORD=${KC_BOOTSTRAP_ADMIN_PASSWORD:-}
[ -n "$KEYCLOAK_ADMIN_PASSWORD" ] || KEYCLOAK_ADMIN_PASSWORD=$(cat /run/secrets/keycloak-admin-password)
export KEYCLOAK_ADMIN_PASSWORD
kcadm config credentials --server http://localhost:8080 --realm master --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD" >/dev/null
cleanup_realm=true
export SHUTTERSPOOL_ADMIN_USERNAME=bootstrap-check
export SHUTTERSPOOL_ADMIN_PASSWORD='synthetic-check-password-1!'
export SHUTTERSPOOL_ADMIN_SUBJECT=6c178745-3db4-47a7-8bb7-17c8e5e89337
cp /opt/shutterspool/shutterspool-*-*.json "$work/"
sed "s/\"shutterspool\"/\"$realm\"/" /opt/shutterspool/shutterspool-realm.json > "$work/shutterspool-realm.json"
printf 'kcadm() { /opt/keycloak/bin/kcadm.sh "$@" --config "%s/kcadm.config"; }\n' "$work" > "$work/reconcile.sh"
sed -e "s#http://keycloak:8080#http://localhost:8080#g" -e "s#-r shutterspool#-r $realm#g" -e "s#realms/shutterspool#realms/$realm#g" -e "s#/opt/shutterspool/#$work/#g" -e "s#/opt/keycloak/bin/kcadm.sh#kcadm#g" /opt/shutterspool/reconcile.sh >> "$work/reconcile.sh"
printf 'kcadm() { /opt/keycloak/bin/kcadm.sh "$@" --config "%s/kcadm.config"; }\n' "$work" > "$work/select-login-theme.sh"
sed -e "s#realms/shutterspool#realms/$realm#g" -e "s#/bin/sh /opt/keycloak/bin/kcadm.sh#kcadm#g" /opt/shutterspool/select-login-theme.sh >> "$work/select-login-theme.sh"
sh "$work/reconcile.sh" >/dev/null
kcadm update "realms/$realm" -s verifyEmail=true >/dev/null
kcadm get "realms/$realm" | grep -Eq '"resetPasswordAllowed"[[:space:]]*:[[:space:]]*false' || { printf 'Fresh bootstrap enabled recovery without SMTP.\n' >&2; exit 1; }
smtp=$(kcadm get "realms/$realm" --fields smtpServer)
if printf '%s' "$smtp" | grep -q '"host"'; then printf 'Fresh bootstrap fixture unexpectedly configured SMTP.\n' >&2; exit 1; fi
attributes=$(kcadm get users/profile -r "$realm" --fields attributes)
if printf '%s' "$attributes" | grep -q '"required"'; then printf 'Fresh bootstrap profile unexpectedly required email or names.\n' >&2; exit 1; fi
account=$(kcadm get "users/$SHUTTERSPOOL_ADMIN_SUBJECT" -r "$realm" --fields email,emailVerified)
if printf '%s' "$account" | grep -Eq '"email"[[:space:]]*:[[:space:]]*"[^"]+"|"emailVerified"[[:space:]]*:[[:space:]]*true'; then printf 'Fresh bootstrap invented an email or marked it verified.\n' >&2; exit 1; fi
# Reconciliation must retain operator profile requirements and account controls.
kcadm update users/profile -r "$realm" -s unmanagedAttributePolicy=ADMIN_VIEW >/dev/null
profile=$(kcadm get users/profile -r "$realm" --fields unmanagedAttributePolicy)
kcadm update "users/$SHUTTERSPOOL_ADMIN_SUBJECT" -r "$realm" -s enabled=false -s emailVerified=false -s 'requiredActions=["CONFIGURE_TOTP"]' >/dev/null
account=$(kcadm get "users/$SHUTTERSPOOL_ADMIN_SUBJECT" -r "$realm" --fields enabled,emailVerified,requiredActions)
sh "$work/reconcile.sh" >/dev/null
retained_profile=$(kcadm get users/profile -r "$realm" --fields unmanagedAttributePolicy)
retained_account=$(kcadm get "users/$SHUTTERSPOOL_ADMIN_SUBJECT" -r "$realm" --fields enabled,emailVerified,requiredActions)
[ "$retained_profile" = "$profile" ] || { printf 'Fresh bootstrap repeat changed operator profile policy.\n' >&2; exit 1; }
[ "$retained_account" = "$account" ] || { printf 'Fresh bootstrap repeat changed operator account controls.\n' >&2; exit 1; }
# Windows pipelines append CRLF; keep the trailing carriage return inside a comment.
'@
$freshProbe = $freshProbe.Replace('__REALM__', $realm).Replace("`r`n", "`n")
$output = $freshProbe | docker exec -i $KeycloakContainer /bin/sh -s 2>&1
if ($LASTEXITCODE -ne 0) {
    $failure = $output | Where-Object { $_ -like 'Fresh bootstrap*' }
    throw $(if ($failure) { $failure -join ' ' } else { 'Fresh bootstrap setup or preservation check failed.' })
}
Write-Output 'Keycloak check passed: fresh realm applied the optional email/name profile without SMTP; repeated reconciliation preserved operator profile policy and account enabled/verification/MFA-action controls.'
