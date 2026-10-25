#!/bin/sh
set -eu

theme=${SHUTTERSPOOL_LOGIN_THEME:-shutterspool}
case "$theme" in
  shutterspool)
    for resource in theme.properties resources/css/shutterspool.v1.css resources/img/aperture.svg resources/img/shutter-color.png resources/img/favicon.ico resources/fonts/Manrope.ttf resources/fonts/Manrope-OFL.txt; do
      if [ ! -r "/opt/keycloak/themes/shutterspool/login/$resource" ]; then
        printf '%s\n' "Theme resource unavailable: $resource. Install the complete theme before selection, or select the bundled keycloak.v2 fallback." >&2
        exit 1
      fi
      if head -n 1 "/opt/keycloak/themes/shutterspool/login/$resource" | grep -Fqx 'version https://git-lfs.github.com/spec/v1'; then
        printf '%s\n' "Theme resource is a Git LFS pointer: $resource. Run git lfs pull before installing the theme, or select keycloak.v2." >&2
        exit 1
      fi
    done
    ;;
  keycloak.v2) ;;
  *) printf '%s\n' 'Unsupported login theme. Select shutterspool or the bundled keycloak.v2 fallback.' >&2; exit 1 ;;
esac

case "${1:-}" in
  --check) exit 0 ;;
  '') /bin/sh /opt/keycloak/bin/kcadm.sh update realms/shutterspool --no-merge -s "loginTheme=$theme" ;;
  *) printf '%s\n' 'Use --check to validate resources without changing the realm.' >&2; exit 1 ;;
esac
