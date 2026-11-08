#!/usr/bin/env bash
set -euo pipefail

tag=${1:?A version tag is required.}
[[ $tag =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.-]+)?$ ]]
[[ $(git cat-file -t "refs/tags/$tag") == tag ]]
image=$(git for-each-ref --format='%(contents:subject)' "refs/tags/$tag")
[[ $image =~ ^ghcr\.io/kaptenjon/var-plats-deploy-web@sha256:[a-f0-9]{64}$ ]]
printf '%s\n' "$image"
