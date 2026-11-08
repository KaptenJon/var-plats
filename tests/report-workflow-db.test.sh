#!/usr/bin/env bash
set -euo pipefail

image_id=$(docker build --quiet -f deploy/postgis/Dockerfile deploy/postgis)
container_id=$(docker run --detach \
  --publish 127.0.0.1::5432 \
  --env POSTGRES_USER=postgres \
  --env POSTGRES_PASSWORD=ci-placeholder-only \
  --env POSTGRES_DB=varplats_workflow_test \
  "$image_id")
trap 'docker rm --force --volumes "$container_id" >/dev/null || true' EXIT

ready=false
for attempt in $(seq 1 60); do
  if docker exec "$container_id" pg_isready -q -h 127.0.0.1 -U postgres -d varplats_workflow_test; then
    ready=true
    break
  fi
  sleep 1
done
if [ "$ready" != true ]; then
  docker logs "$container_id"
  exit 1
fi

docker exec "$container_id" psql -U postgres -d varplats_workflow_test -v ON_ERROR_STOP=1 \
  -c 'CREATE EXTENSION postgis;'
binding=$(docker port "$container_id" 5432/tcp)
port=${binding##*:}
[[ "$port" =~ ^[0-9]+$ ]]
export VARPLATS_REPORT_TEST_DB="Host=127.0.0.1;Port=$port;Database=varplats_workflow_test;Username=postgres;Password=ci-placeholder-only"
dotnet test -c Release --no-build
