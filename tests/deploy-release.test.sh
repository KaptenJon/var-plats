#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
mkdir -p "$fixture"/{bin,deploy,state,mocks}
cp "$root/deploy/deploy-release.sh" "$fixture/bin/"
export GITHUB_REPOSITORY=KaptenJon/var-plats GITHUB_REF_TYPE=tag GITHUB_REF_NAME=v0.1.0-test
export WEB_IMAGE="ghcr.io/kaptenjon/var-plats-web@sha256:$(printf 'a%.0s' {1..64})"
export DB_IMAGE="ghcr.io/kaptenjon/var-plats-postgis@sha256:$(printf 'b%.0s' {1..64})"
old_web="ghcr.io/kaptenjon/var-plats-web@sha256:$(printf 'c%.0s' {1..64})"
old_db="ghcr.io/kaptenjon/var-plats-postgis@sha256:$(printf 'd%.0s' {1..64})"
export TEST_LOG="$fixture/docker.log" TEST_CURL_COUNT="$fixture/curl.count"
cat > "$fixture/mocks/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s | %s | %s\n' "$*" "${WEB_IMAGE:-}" "${DB_IMAGE:-}" >> "$TEST_LOG"
MOCK
cat > "$fixture/mocks/curl" <<'MOCK'
#!/usr/bin/env bash
count=0
[[ ! -f "$TEST_CURL_COUNT" ]] || read -r count < "$TEST_CURL_COUNT"
echo "$((count + 1))" > "$TEST_CURL_COUNT"
if [[ ${FAIL_HEALTH:-0} == 1 && $count == 0 ]]; then exit 22; fi
MOCK
chmod +x "$fixture/mocks/"*
export PATH="$fixture/mocks:$PATH"
reset_fixture() {
    printf 'WEB_IMAGE=%s\nDB_IMAGE=%s\nWEB_BIND_ADDRESS=127.0.0.1\nWEB_PORT=8010\n' \
        "$old_web" "$old_db" > "$fixture/deploy/.env"
    touch "$fixture/state/database-ready"
    printf 'previous release\n' > "$fixture/state/current-release"
    : > "$TEST_LOG"
    rm -f "$TEST_CURL_COUNT"
}

reset_fixture
if GITHUB_REF_TYPE=branch bash "$fixture/bin/deploy-release.sh"; then
    echo "Branch deployment was not rejected" >&2; exit 1
fi
[[ ! -s "$TEST_LOG" ]]
if WEB_IMAGE=varplats-web:latest bash "$fixture/bin/deploy-release.sh"; then
    echo "Mutable image reference was not rejected" >&2; exit 1
fi
[[ ! -s "$TEST_LOG" ]]

bash "$fixture/bin/deploy-release.sh"
grep -Fx "WEB_IMAGE=$WEB_IMAGE" "$fixture/deploy/.env"
grep -Fx "DB_IMAGE=$old_db" "$fixture/deploy/.env"
grep -F "$GITHUB_REF_NAME $WEB_IMAGE" "$fixture/state/current-release"
[[ $(grep -c 'up -d --no-build --no-deps web' "$TEST_LOG") == 1 ]]
! cut -d '|' -f1 "$TEST_LOG" | grep -E 'down|up .* db|pull .*postgis'
[[ $(stat -c '%a' "$fixture/deploy/.env") == 600 ]]

reset_fixture
if FAIL_HEALTH=1 bash "$fixture/bin/deploy-release.sh"; then
    echo "Unhealthy deployment unexpectedly succeeded" >&2; exit 1
fi
grep -Fx "WEB_IMAGE=$old_web" "$fixture/deploy/.env"
grep -Fx 'previous release' "$fixture/state/current-release"
[[ $(grep -c 'up -d --no-build --no-deps web' "$TEST_LOG") == 2 ]]
grep -F " | $old_web | $old_db" "$TEST_LOG"
! cut -d '|' -f1 "$TEST_LOG" | grep -E 'down|up .* db|pull .*postgis'
echo "Deployment validation, database isolation, and web rollback tests passed."
