#!/bin/sh
set -eu

/bin/sh /opt/shutterspool/select-login-theme.sh --check

if ! printf '%s\n' "$SHUTTERSPOOL_ADMIN_SUBJECT" | grep -Eq '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'; then
  printf '%s\n' 'ShutterSpool administrator subject must be a UUID.' >&2
  exit 1
fi
username=$(printf '%s' "$SHUTTERSPOOL_ADMIN_USERNAME" | LC_ALL=C tr -d '\000-\037\177')
if [ -z "$username" ] || [ "$username" != "$SHUTTERSPOOL_ADMIN_USERNAME" ]; then
  printf '%s\n' 'ShutterSpool administrator username must be nonempty and contain no control characters.' >&2
  exit 1
fi

attempt=0
until /opt/keycloak/bin/kcadm.sh config credentials --server http://keycloak:8080 --realm master --user "$KEYCLOAK_ADMIN" --password "$KEYCLOAK_ADMIN_PASSWORD"; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 30 ] || exit 1
  sleep 2
done

if ! /opt/keycloak/bin/kcadm.sh get realms/shutterspool >/dev/null 2>&1; then
  /opt/keycloak/bin/kcadm.sh create realms -f /opt/shutterspool/shutterspool-realm.json
fi

/bin/sh /opt/shutterspool/select-login-theme.sh

if ! /opt/keycloak/bin/kcadm.sh get roles/shutterspool-admin -r shutterspool >/dev/null 2>&1; then
  /opt/keycloak/bin/kcadm.sh create roles -r shutterspool -s name=shutterspool-admin
fi

client_id() {
  /opt/keycloak/bin/kcadm.sh get clients -r shutterspool -q clientId="$1" | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1
}

reconcile_client() {
  id=$(client_id "$1")
  if [ -n "$id" ]; then
    /opt/keycloak/bin/kcadm.sh update "clients/$id" -r shutterspool -f "/opt/shutterspool/$2"
  else
    /opt/keycloak/bin/kcadm.sh create clients -r shutterspool -f "/opt/shutterspool/$2"
    id=$(client_id "$1")
  fi
  printf '%s' "$id"
}

mapper_id() {
  /opt/keycloak/bin/kcadm.sh get "clients/$1/protocol-mappers/models" -r shutterspool | tr '\n' ' ' | sed -n "s/.*\"id\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\"[[:space:]]*,[[:space:]]*\"name\"[[:space:]]*:[[:space:]]*\"$2\".*/\1/p"
}

reconcile_mapper() {
  id=$(mapper_id "$1" "$2")
  [ -z "$id" ] || /opt/keycloak/bin/kcadm.sh delete "clients/$1/protocol-mappers/models/$id" -r shutterspool
  /opt/keycloak/bin/kcadm.sh create "clients/$1/protocol-mappers/models" -r shutterspool -f "/opt/shutterspool/$3"
}

reconcile_client shutterspool-server shutterspool-server-client.json >/dev/null
web_client=$(reconcile_client shutterspool-web shutterspool-web-client.json)
reconcile_mapper "$web_client" shutterspool-server-audience shutterspool-server-audience-mapper.json
reconcile_mapper "$web_client" shutterspool-roles shutterspool-roles-mapper.json

administrator_id() {
  /opt/keycloak/bin/kcadm.sh get users -r shutterspool -q username="$SHUTTERSPOOL_ADMIN_USERNAME" -q exact=true --fields id | sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p'
}

administrator=$(administrator_id)
new_administrator=false
if [ -z "$administrator" ]; then
  # The normal users endpoint generates its own ID; partial import retains the configured subject.
  username_json=$(printf '%s' "$SHUTTERSPOOL_ADMIN_USERNAME" | sed 's/\\/\\\\/g; s/"/\\"/g')
  /opt/keycloak/bin/kcadm.sh create partialImport -r shutterspool -s ifResourceExists=FAIL \
    -s "users=[{\"id\":\"$SHUTTERSPOOL_ADMIN_SUBJECT\",\"username\":\"$username_json\",\"enabled\":true}]"
  administrator=$(administrator_id)
  new_administrator=true
fi
if [ "$administrator" != "$SHUTTERSPOOL_ADMIN_SUBJECT" ]; then
  printf '%s\n' 'Configured ShutterSpool administrator subject does not match the existing Keycloak account. Correct the deployment subject; the account was preserved.' >&2
  exit 1
fi

# Bootstrap credentials belong only to the account-creation operation. An
# existing passwordless account may be intentionally passwordless; fail closed
# and require an operator to repair an interrupted initial setup explicitly.
if [ "$new_administrator" = true ]; then
  /opt/keycloak/bin/kcadm.sh set-password -r shutterspool --username "$SHUTTERSPOOL_ADMIN_USERNAME" --new-password "$SHUTTERSPOOL_ADMIN_PASSWORD"
else
  credentials=$(/opt/keycloak/bin/kcadm.sh get "users/$administrator/credentials" -r shutterspool --fields type)
  if ! printf '%s' "$credentials" | grep -Eq '"type"[[:space:]]*:[[:space:]]*"password"'; then
    printf '%s\n' 'The existing ShutterSpool administrator has no password credential. Reconcile will not assign the bootstrap password to an existing account; complete credential recovery with a trusted Keycloak administrator, then retry.' >&2
    exit 1
  fi
fi
/opt/keycloak/bin/kcadm.sh add-roles -r shutterspool --uusername "$SHUTTERSPOOL_ADMIN_USERNAME" --rolename shutterspool-admin
