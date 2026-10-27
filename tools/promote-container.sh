#!/usr/bin/env bash
# Called only by the serialized, approved publication job.
set -euo pipefail
source_package=$1 target_package=$2 version=$3 image_digest=$4 bundle_digest=$5
tags=("$version" "bundle-$version")
digests=("$image_digest" "$bundle_digest")
missing=()
error_file=$(mktemp)
trap 'rm -f "$error_file"' EXIT

# Check both tags before writing either; failed lookups are not generally absence.
for i in 0 1; do
    if existing=$(oras resolve "$target_package:${tags[$i]}" 2>"$error_file"); then
        if [[ "$existing" != "${digests[$i]}" ]]; then
            echo "Refusing to replace $target_package:${tags[$i]} with different content." >&2
            exit 1
        fi
    elif grep -Fxq "Error response from registry: failed to resolve digest: $target_package:${tags[$i]}: not found" "$error_file"; then
        missing+=("$i")
    else
        cat "$error_file" >&2
        exit 1
    fi
done

for i in "${missing[@]}"; do
    # An OCI index copy includes its manifests, including Buildx attestations.
    oras cp "$source_package@${digests[$i]}" "$target_package:${tags[$i]}"
done
for i in 0 1; do
    test "$(oras resolve "$target_package:${tags[$i]}")" = "${digests[$i]}"
done
