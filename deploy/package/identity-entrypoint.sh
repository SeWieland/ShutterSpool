#!/bin/sh
set -eu
umask 077
KC_DB_PASSWORD=$(cat /run/secrets/postgres-password)
: "${KC_DB_PASSWORD:?The database password file is empty}"
export KC_DB_PASSWORD
: "${KC_HOSTNAME:?Set the public HTTPS issuer origin}"
case "$KC_HOSTNAME" in https://*) identity_host=${KC_HOSTNAME#https://} ;; *) echo 'The public identity hostname must use HTTPS.' >&2; exit 1;; esac
case "$identity_host" in ''|*[!a-zA-Z0-9.:-]*) echo 'The public identity hostname must be a bare HTTPS origin.' >&2; exit 1;; esac
if [ -n "${KC_HOSTNAME_ADMIN:-}" ]; then
  case "$KC_HOSTNAME_ADMIN" in https://*) ;; *) echo 'The identity administration hostname must use HTTPS.' >&2; exit 1;; esac
  admin_host=${KC_HOSTNAME_ADMIN#https://}
  case "$admin_host" in ''|*[!a-zA-Z0-9.:-]*) echo 'The identity administration hostname must be a bare HTTPS origin.' >&2; exit 1;; esac
fi
case "${KC_HOSTNAME_BACKCHANNEL_DYNAMIC:-false}" in true|false) ;; *) echo 'KC_HOSTNAME_BACKCHANNEL_DYNAMIC must be true or false.' >&2; exit 1;; esac
if [ "$1" = serve ]; then
  KC_BOOTSTRAP_ADMIN_PASSWORD=$(cat /run/secrets/keycloak-admin-password)
  : "${KC_BOOTSTRAP_ADMIN_PASSWORD:?The identity bootstrap password file is empty}"
  : "${KC_BOOTSTRAP_ADMIN_USERNAME:?Set the Keycloak bootstrap account}"
  export KC_BOOTSTRAP_ADMIN_PASSWORD
  export KC_DB=postgres KC_DB_URL_HOST="${KC_DB_URL_HOST:-postgres}" KC_DB_URL_DATABASE="${KC_DB_URL_DATABASE:-keycloak}" KC_DB_USERNAME="${KC_DB_USERNAME:-postgres}"
  exec /opt/keycloak/bin/kc.sh start --http-enabled=true --hostname-strict=true --proxy-headers=xforwarded --health-enabled=true --log-level=warn,io.quarkus:info
fi
[ "$1" = reconcile ] || exit 1
KC_BOOTSTRAP_ADMIN_PASSWORD=$(cat /run/secrets/keycloak-admin-password)
: "${KC_BOOTSTRAP_ADMIN_PASSWORD:?The identity bootstrap password file is empty}"
: "${KC_BOOTSTRAP_ADMIN_USERNAME:?Set the Keycloak bootstrap account}"
export KC_BOOTSTRAP_ADMIN_PASSWORD
export KEYCLOAK_ADMIN="$KC_BOOTSTRAP_ADMIN_USERNAME" KEYCLOAK_ADMIN_PASSWORD="$KC_BOOTSTRAP_ADMIN_PASSWORD"
SHUTTERSPOOL_ADMIN_PASSWORD=$(cat /run/secrets/administrator-password)
: "${SHUTTERSPOOL_ADMIN_PASSWORD:?The initial administrator password file is empty}"
export SHUTTERSPOOL_ADMIN_PASSWORD
: "${SHUTTERSPOOL_APP_ORIGIN:?Set the public HTTPS application origin}"
case "$SHUTTERSPOOL_APP_ORIGIN" in https://*) ;; *) echo 'The application origin must use HTTPS.' >&2; exit 1;; esac
case "${SHUTTERSPOOL_APP_ORIGIN#https://}" in ''|*[!a-zA-Z0-9.:-]*) echo 'The application origin must be a bare HTTPS origin.' >&2; exit 1;; esac
/bin/sh /opt/shutterspool/reconcile.sh >/dev/null 2>&1 || { echo 'Identity reconciliation failed; Server remains closed. Verify endpoint, account subject and secret custody.' >&2; exit 1; }
client=$(/opt/keycloak/bin/kcadm.sh get clients -r shutterspool -q clientId=shutterspool-web --fields id | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
[ -n "$client" ] || exit 1
/opt/keycloak/bin/kcadm.sh update "clients/$client" -r shutterspool \
  -s "redirectUris=[\"$SHUTTERSPOOL_APP_ORIGIN/auth/callback\",\"$SHUTTERSPOOL_APP_ORIGIN/auth/signout\"]" \
  -s "webOrigins=[\"$SHUTTERSPOOL_APP_ORIGIN\"]" \
  -s "attributes.\"post.logout.redirect.uris\"=$SHUTTERSPOOL_APP_ORIGIN/auth/signout" >/dev/null 2>&1 || { echo 'Identity redirect reconciliation failed.' >&2; exit 1; }
