#!/usr/bin/env bash
set -euo pipefail

tag=${1:?A version tag is required.}
[[ $tag =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.-]+)?$ ]]

args=(--verify-tag --generate-notes --title "$tag")
if [[ $tag == *-* ]]; then
    args+=(--prerelease --latest=false)
fi
gh release create "$tag" "${args[@]}"
