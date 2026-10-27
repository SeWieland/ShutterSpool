#!/usr/bin/env bash
# Exercise our conflict/refusal/retry decisions with fake remote commands.
set -euo pipefail
script="$(cd "$(dirname "$0")/.." && pwd)/tools/publish-release.sh"
export RELEASE_TEST_STATE RELEASE_COMMIT
RELEASE_TEST_STATE=$(mktemp -d)
RELEASE_COMMIT=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
trap 'rm -rf "$RELEASE_TEST_STATE"' EXIT
mkdir -p "$RELEASE_TEST_STATE/bin" "$RELEASE_TEST_STATE/build" "$RELEASE_TEST_STATE/compose"
image="sha256:$(printf '%064d' 1)"
printf '{"version":"0.2.0","commit":"%s","developmentBuild":false,"prerelease":true,"serverImage":{"reference":"ghcr.io/sewieland/shutterspool:0.2.0","digest":"%s"}}' "$RELEASE_COMMIT" "$image" > "$RELEASE_TEST_STATE/compose/release-manifest.json"
tar -czf "$RELEASE_TEST_STATE/build/shutterspool-compose.tar.gz" -C "$RELEASE_TEST_STATE/compose" release-manifest.json
(cd "$RELEASE_TEST_STATE/build" && sha256sum shutterspool-compose.tar.gz > shutterspool-compose.tar.gz.sha256)
cat > "$RELEASE_TEST_STATE/bin/oras" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
state=$RELEASE_TEST_STATE
if [[ "$1" == resolve ]]; then
  [[ $# == 2 && "$2" == ghcr.io/sewieland/shutterspool:0.2.0 ]]
  if [[ -f "$state/lookup-error" ]]; then cat "$state/lookup-error" >&2; exit 1; fi
  if [[ -f "$state/image" ]]; then cat "$state/image"; exit 0; fi
  echo "Error response from registry: failed to resolve digest: $2: not found" >&2; exit 1
fi
if [[ "$1" == cp && "$2" == --from-oci-layout ]]; then
  [[ $# == 4 && "$3" == server-image.oci.tar@sha256:* && "$4" == ghcr.io/sewieland/shutterspool:0.2.0 ]]
  printf '%s' "${3##*@}" > "$state/image"
  echo image >> "$state/writes"
elif [[ "$1" == cp && "$2" == --registry-config ]]; then
  [[ $# == 6 && "$4" == --to-oci-layout && "$5" == "ghcr.io/sewieland/shutterspool@$(cat "$state/image")" && "$6" == */image:verified ]]
  test "$(cat "$3")" = '{}'
else
  echo 'Unexpected image command' >&2; exit 1
fi
FAKE
cat > "$RELEASE_TEST_STATE/bin/gh" <<'FAKE'
#!/usr/bin/env bash
set -euo pipefail
state=$RELEASE_TEST_STATE
[[ "$1" == release ]]
if [[ "$2" == view ]]; then
  [[ $# == 7 && "$3" == v0.2.0 && "$4" == --repo && "$5" == owner/repo && "$6" == --json ]]
  if [[ -f "$state/release-error" ]]; then cat "$state/release-error" >&2; exit 1; fi
  if [[ -f "$state/release" ]]; then cat "$state/release"; exit 0; fi
  echo 'release not found' >&2; exit 1
fi
case "$2" in
  create)
    [[ " $* " == *' --draft '* && " $* " == *' --prerelease '* ]]
    printf '{"isDraft":true,"isPrerelease":true,"body":"generated notes","assets":[]}' > "$state/release"
    echo draft >> "$state/writes" ;;
  upload)
    shift 3
    while [[ "$1" != --repo ]]; do
      digest="sha256:$(sha256sum "$1" | cut -d ' ' -f 1)"
      jq --arg name "$1" --arg digest "$digest" '.assets += [{name:$name,digest:$digest}]' "$state/release" > "$state/next"
      mv "$state/next" "$state/release"
      echo "$1" >> "$state/writes"
      if [[ -f "$state/upload-error" ]]; then exit 1; fi
      shift
    done ;;
  *) echo 'Unexpected release mutation' >&2; exit 1 ;;
esac
FAKE
chmod +x "$RELEASE_TEST_STATE/bin/"*
export PATH="$RELEASE_TEST_STATE/bin:$PATH"
publish() { bash "$script" "$RELEASE_TEST_STATE/build" owner/repo v0.2.0; }
reset_remote() { rm -f "$RELEASE_TEST_STATE/"{image,release,writes,lookup-error,release-error,upload-error}; }

# Successful publication and unchanged retry perform exactly four writes.
publish
publish
test "$(wc -l < "$RELEASE_TEST_STATE/writes")" -eq 4

# Image and asset conflicts stop before any mutation.
printf 'sha256:other' > "$RELEASE_TEST_STATE/image"
if publish; then exit 1; fi
test "$(wc -l < "$RELEASE_TEST_STATE/writes")" -eq 4
rm "$RELEASE_TEST_STATE/image"
jq '.assets[0].digest="sha256:other"' "$RELEASE_TEST_STATE/release" > "$RELEASE_TEST_STATE/next"
mv "$RELEASE_TEST_STATE/next" "$RELEASE_TEST_STATE/release"
if publish; then exit 1; fi
test "$(wc -l < "$RELEASE_TEST_STATE/writes")" -eq 4

# Lookup failures must not be interpreted as permission to create a version.
for failure in lookup-error release-error; do
  for message in 'access denied' 'connection timed out' 'credential helper not found'; do
    reset_remote
    echo "$message" > "$RELEASE_TEST_STATE/$failure"
    if publish; then exit 1; fi
    test ! -f "$RELEASE_TEST_STATE/writes"
  done
done

# A partial upload retries only the missing asset and preserves edited notes.
reset_remote
touch "$RELEASE_TEST_STATE/upload-error"
if publish; then exit 1; fi
jq '.body="Maintainer edited notes"' "$RELEASE_TEST_STATE/release" > "$RELEASE_TEST_STATE/next"
mv "$RELEASE_TEST_STATE/next" "$RELEASE_TEST_STATE/release"
rm "$RELEASE_TEST_STATE/upload-error"
publish
test "$(wc -l < "$RELEASE_TEST_STATE/writes")" -eq 4
test "$(jq -r .body "$RELEASE_TEST_STATE/release")" = 'Maintainer edited notes'
echo 'Release conflict and retry checks passed.'
