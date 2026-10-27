#!/usr/bin/env bash
set -euo pipefail
script="$(cd "$(dirname "$0")/.." && pwd)/tools/promote-container.sh"
export PROMOTION_TEST_STATE
PROMOTION_TEST_STATE=$(mktemp -d)
trap 'rm -rf "$PROMOTION_TEST_STATE"' EXIT
mkdir "$PROMOTION_TEST_STATE/bin"
cat > "$PROMOTION_TEST_STATE/bin/oras" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
state=$PROMOTION_TEST_STATE
case "$1" in
    resolve)
        if [[ -f "$state/error" ]]; then
            cat "$state/error" >&2; exit 1
        fi
        tag=${2##*:}
        if [[ -f "$state/$tag" ]]; then cat "$state/$tag"
        else echo "Error response from registry: failed to resolve digest: $2: not found" >&2; exit 1; fi
        ;;
    cp)
        tag=${3##*:}
        if [[ -f "$state/fail-bundle" && "$tag" == bundle-* ]]; then exit 1; fi
        printf '%s\n' "${2##*@}" > "$state/$tag"
        echo "$tag" >> "$state/writes"
        ;;
    *) exit 1 ;;
esac
FAKE
chmod +x "$PROMOTION_TEST_STATE/bin/oras"
export PATH="$PROMOTION_TEST_STATE/bin:$PATH"
promote() { bash "$script" private public 0.1.0-alpha.1 sha256:image sha256:bundle; }
reset_state() { rm -f "$PROMOTION_TEST_STATE"/{0.1.0-alpha.1,bundle-0.1.0-alpha.1,writes,error,fail-bundle}; }

# New publication and an unchanged retry.
promote
test "$(wc -l < "$PROMOTION_TEST_STATE/writes")" -eq 2
promote
test "$(wc -l < "$PROMOTION_TEST_STATE/writes")" -eq 2

# A conflicting bundle must prevent even the otherwise missing image write.
reset_state
echo sha256:other > "$PROMOTION_TEST_STATE/bundle-0.1.0-alpha.1"
if promote; then exit 1; fi
test ! -e "$PROMOTION_TEST_STATE/writes"

# Permission, connectivity and unrelated lookup errors never authorize writes.
reset_state
for error in 'DENIED: access forbidden' 'connection timed out' 'credential helper not found'; do
    echo "$error" > "$PROMOTION_TEST_STATE/error"
    if promote; then exit 1; fi
    test ! -e "$PROMOTION_TEST_STATE/writes"
done

# An interrupted second copy leaves the first intact; retry writes only the missing bundle.
reset_state
touch "$PROMOTION_TEST_STATE/fail-bundle"
if promote; then exit 1; fi
test "$(cat "$PROMOTION_TEST_STATE/writes")" = 0.1.0-alpha.1
rm "$PROMOTION_TEST_STATE/fail-bundle"
promote
test "$(wc -l < "$PROMOTION_TEST_STATE/writes")" -eq 2
echo 'Container promotion conflict and retry checks passed.'
