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
    rm -f "$work/kcadm.config" "$work/reconcile.sh" "$work/select-login-theme.sh" "$work/failed-set-password" "$work/shutterspool-realm.json" "$work/shutterspool-server-client.json" "$work/shutterspool-web-client.json" "$work/shutterspool-server-audience-mapper.json" "$work/shutterspool-roles-mapper.json"
    rmdir "$work"
}
trap cleanup EXIT
export KEYCLOAK_ADMIN="$KC_BOOTSTRAP_ADMIN_USERNAME"
KEYCLOAK_ADMIN_PASSWORD=$(cat /run/secrets/keycloak-admin-password)
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
kcadm create realms -s realm="$realm" -s enabled=true >/dev/null
created=true
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
credentials=$(kcadm get "users/$actual/credentials" -r "$realm" --fields type)
printf '%s' "$credentials" | grep -Eq '"type"[[:space:]]*:[[:space:]]*"password"' || { printf 'Operator credential recovery was not recognized.\n' >&2; exit 1; }
kcadm create roles -r "$realm" -s name=operator-maintained >/dev/null
kcadm add-roles -r "$realm" --uusername "$SHUTTERSPOOL_ADMIN_USERNAME" --rolename operator-maintained >/dev/null
credentials=$(kcadm get "users/$actual/credentials" -r "$realm" --fields id)
export SHUTTERSPOOL_ADMIN_PASSWORD='synthetic-check-password-2!'
sh "$work/reconcile.sh" >/dev/null
kcadm get "users/$actual/role-mappings/realm" -r "$realm" | grep -q operator-maintained || { printf 'Repeated reconciliation removed an operator grant.\n' >&2; exit 1; }
[ "$(kcadm get "users/$actual/credentials" -r "$realm" --fields id)" = "$credentials" ] || { printf 'Repeated reconciliation replaced the existing password.\n' >&2; exit 1; }
export SHUTTERSPOOL_ADMIN_SUBJECT=6c178745-3db4-47a7-8bb7-17c8e5e89338
export SHUTTERSPOOL_ADMIN_PASSWORD='synthetic-check-password-2!'
if sh "$work/reconcile.sh" >/dev/null 2>&1; then printf 'Mismatched bootstrap subject was accepted.\n' >&2; exit 1; fi
retained=$(kcadm get users -r "$realm" -q username="$SHUTTERSPOOL_ADMIN_USERNAME" -q exact=true --fields id | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
[ "$retained" = "$actual" ] || { printf 'Existing bootstrap identity changed.\n' >&2; exit 1; }
kcadm get "users/$actual/role-mappings/realm" -r "$realm" | grep -q operator-maintained || { printf 'Repeated reconciliation removed an operator grant.\n' >&2; exit 1; }
[ "$(kcadm get "users/$actual/credentials" -r "$realm" --fields id)" = "$credentials" ] || { printf 'Existing bootstrap credentials changed on mismatch.\n' >&2; exit 1; }
printf 'Keycloak check passed: interrupted bootstrap failed closed, operator credential recovery enabled reconciliation, partial username match preserved, repeated reconciliation succeeded, mismatched subject rejected, existing identity and credentials retained.\n'
'@
$probe = $probe.Replace('__NONCE__', [Guid]::NewGuid().ToString('N')).Replace("`r`n", "`n")
$output = $probe | docker exec -i $KeycloakContainer /bin/sh -s 2>&1
if ($LASTEXITCODE -ne 0) {
    $failure = $output | Where-Object { $_ -match '^(Fresh bootstrap|Mismatched bootstrap|Existing bootstrap)|not found|No such file|Permission denied|Syntax error|Illegal option' }
    throw $(if ($failure) { $failure -join ' ' } else { 'Keycloak bootstrap check failed; inspect the controlled container privately.' })
}
$output | Where-Object { $_ -like 'Keycloak check passed:*' }
