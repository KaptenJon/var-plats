#!/usr/bin/env bash
set -euo pipefail

image=${1:?An image reference is required.}
revision=${2:?A source revision is required.}
[[ $image =~ ^ghcr\.io/kaptenjon/var-plats-deploy-web@sha256:[a-f0-9]{64}$ ]]
[[ $revision =~ ^[a-f0-9]{40}$ ]]

docker pull "$image"
actual=$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$image")
if [[ $actual != "$revision" ]]; then
    echo "The private image does not match the reviewed source revision." >&2
    exit 1
fi
