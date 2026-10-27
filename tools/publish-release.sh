#!/usr/bin/env bash
# Publish only this run's retained image and package; never replace a version.
set -euo pipefail
directory=$1 repository=$2 tag=$3
version=${tag#v}
package=ghcr.io/sewieland/shutterspool
archive=shutterspool-compose.tar.gz
files=("$archive" "$archive.sha256")
cd "$directory"
sha256sum --check "$archive.sha256"
manifest=$(tar -xOzf "$archive" release-manifest.json)
image=$(jq -er '.serverImage.digest' <<< "$manifest")
prerelease=$(jq -r '.prerelease' <<< "$manifest")
jq -e --arg version "$version" --arg commit "$RELEASE_COMMIT" --arg reference "$package:$version" \
  '.version == $version and .commit == $commit and .developmentBuild == false and .serverImage.reference == $reference' <<< "$manifest" > /dev/null
[[ "$image" =~ ^sha256:[0-9a-f]{64}$ && "$prerelease" =~ ^(true|false)$ ]]

error_file=$(mktemp)
anonymous=$(mktemp -d)
trap 'rm -f "$error_file"; rm -rf "$anonymous"' EXIT
existing=''
if existing=$(oras resolve "$package:$version" 2>"$error_file"); then
  test "$existing" = "$image" || { echo 'Container version conflicts; refusing replacement.' >&2; exit 1; }
elif ! grep -Fxq "Error response from registry: failed to resolve digest: $package:$version: not found" "$error_file"; then
  cat "$error_file" >&2; exit 1
fi
release=null
if release=$(gh release view "$tag" --repo "$repository" --json isDraft,isPrerelease,assets 2>"$error_file"); then
  test "$(jq -r '.isPrerelease' <<< "$release")" = "$prerelease"
elif grep -Fxq 'release not found' "$error_file"; then
  release=null
else
  cat "$error_file" >&2; exit 1
fi
missing=()
for file in "${files[@]}"; do
  remote=$(jq -r --arg name "$file" '.assets[]? | select(.name == $name) | .digest // "unknown"' <<< "$release")
  if [[ -z "$remote" ]]; then
    missing+=("$file")
  elif [[ "$remote" != "sha256:$(sha256sum "$file" | cut -d ' ' -f 1)" ]]; then
    echo "Release asset $file conflicts or has no digest; refusing replacement." >&2; exit 1
  fi
done
if [[ "$release" != null && "$(jq -r '.isDraft' <<< "$release")" != true && ( ${#missing[@]} -gt 0 || -z "$existing" ) ]]; then
  echo 'Published release is incomplete; inspect it manually.' >&2; exit 1
fi
if [[ -z "$existing" ]]; then
  oras cp --from-oci-layout "server-image.oci.tar@$image" "$package:$version"
fi
test "$(oras resolve "$package:$version")" = "$image"
printf '{}' > "$anonymous/config.json"
oras cp --registry-config "$anonymous/config.json" --to-oci-layout "$package@$image" "$anonymous/image:verified"
if [[ "$release" == null ]]; then
  flags=()
  if [[ "$prerelease" == true ]]; then flags+=(--prerelease); fi
  gh release create "$tag" --repo "$repository" --verify-tag --draft --generate-notes "${flags[@]}"
fi
if (( ${#missing[@]} )); then
  gh release upload "$tag" "${missing[@]}" --repo "$repository"
fi
release=$(gh release view "$tag" --repo "$repository" --json assets)
for file in "${files[@]}"; do
  test "$(jq -r --arg name "$file" '.assets[]? | select(.name == $name) | .digest' <<< "$release")" = "sha256:$(sha256sum "$file" | cut -d ' ' -f 1)"
done
printf 'Verified container %s:%s@%s and release assets. Review the draft notes before publishing.\n' "$package" "$version" "$image"
