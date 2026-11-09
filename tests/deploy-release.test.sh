#!/usr/bin/env bash
set -euo pipefail
unset AUTHENTICATION_ENABLED PUBLIC_BASE_URL FAIL_HEALTH FAIL_AUTH AUTH_ROUTE_STATUS FAIL_PREVIOUS_IMAGE
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf -- "$fixture"' EXIT
mkdir -p "$fixture"/{bin,deploy,state,mocks}
cp "$root/deploy/deploy-release.sh" "$fixture/bin/"
export GITHUB_REPOSITORY=KaptenJon/var-plats GITHUB_REF_TYPE=tag GITHUB_REF_NAME=v0.1.0-test
export WEB_IMAGE="ghcr.io/kaptenjon/var-plats-deploy-web@sha256:$(printf 'a%.0s' {1..64})"
export DB_IMAGE="ghcr.io/kaptenjon/var-plats-postgis@sha256:$(printf 'b%.0s' {1..64})"
export EXPECTED_SOURCE_SHA="$(printf 'e%.0s' {1..40})"
old_web="ghcr.io/kaptenjon/var-plats-web@sha256:$(printf 'c%.0s' {1..64})"
old_private_web="ghcr.io/kaptenjon/var-plats-deploy-web@sha256:$(printf 'f%.0s' {1..64})"
old_db="ghcr.io/kaptenjon/var-plats-postgis@sha256:$(printf 'd%.0s' {1..64})"
release_workflow="$root/.github/workflows/release.yml"
for expected in \
    'GOOGLE_CLIENT_ID: ${{ secrets.GOOGLE_CLIENT_ID }}' \
    'GOOGLE_CLIENT_SECRET: ${{ secrets.GOOGLE_CLIENT_SECRET }}' \
    'GITHUB_CLIENT_ID: ${{ secrets.OAUTH_GITHUB_CLIENT_ID }}' \
    'GITHUB_CLIENT_SECRET: ${{ secrets.OAUTH_GITHUB_CLIENT_SECRET }}' \
    'MAILJET_API_KEY: ${{ secrets.MAILJET_API_KEY }}' \
    'MAILJET_API_SECRET: ${{ secrets.MAILJET_API_SECRET }}' \
    'MAILJET_SENDER_EMAIL: ${{ secrets.MAILJET_SENDER_EMAIL }}' \
    'REGISTRY_TOKEN: ${{ secrets.GHCR_PULL_TOKEN }}' \
    'deploy/verify-release-image.sh' \
    'AUTHENTICATION_ENABLED: "true"' \
    'REPORTING_PUBLIC_WRITES: "false"' \
    'ADMIN_GITHUB_ID: "10761546"' \
    'install -m 0644 "$GITHUB_WORKSPACE/deploy/compose.yml"' \
    'install -m 0644 "$GITHUB_WORKSPACE/deploy/pi.override.yml"' \
    'install -m 0755 "$GITHUB_WORKSPACE/deploy/deploy-release.sh"' \
    'deploy/identity-schema.sql' \
    'deploy/pi-identity-role.sql' \
    'AUTHENTICATION_TRUSTED_PROXY_IP'; do
    grep -Fq "$expected" "$release_workflow"
done
! grep -Fq 'deploy/pi-public-write-role.sql' "$release_workflow"
! grep -Eiq 'CREATE TABLE "?reports"?|ALTER TABLE "?reports"?' "$root/deploy/identity-schema.sql"
export TEST_LOG="$fixture/docker.log" TEST_CURL_COUNT="$fixture/curl.count" TEST_GH_LOG="$fixture/gh.log"
cat > "$fixture/mocks/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s | %s | %s\n' "$*" "${WEB_IMAGE:-}" "${DB_IMAGE:-}" >> "$TEST_LOG"
if [[ ${1:-} == image && ${2:-} == inspect ]]; then
    [[ ${FAIL_PREVIOUS_IMAGE:-0} != 1 ]] || exit 1
    printf '%s\n' "${MOCK_IMAGE_REVISION:-$EXPECTED_SOURCE_SHA}"
fi
MOCK
cat > "$fixture/mocks/curl" <<'MOCK'
#!/usr/bin/env bash
count=0
[[ ! -f "$TEST_CURL_COUNT" ]] || read -r count < "$TEST_CURL_COUNT"
echo "$((count + 1))" > "$TEST_CURL_COUNT"
if [[ ${FAIL_HEALTH:-0} == 1 && $count == 0 ]]; then exit 22; fi
if [[ "$*" == *"/login"* ]]; then
    if [[ ${FAIL_AUTH:-0} == 1 ]]; then
        printf '%s' 'Inloggning är inte aktiverad i den här miljön.'
    else
        printf '%s' 'Logga in med en passkey eller säkerhetsnyckel.'
    fi
fi
if [[ "$*" == *"/auth/signin/google"* ]]; then
    printf '%s' "${AUTH_ROUTE_STATUS:-405}"
fi
MOCK
cat > "$fixture/mocks/gh" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$@" > "$TEST_GH_LOG"
MOCK
chmod +x "$fixture/mocks/"*
export PATH="$fixture/mocks:$PATH"
bash "$root/deploy/publish-release.sh" v1.2.3-preview.6
grep -Fxq -- 'v1.2.3-preview.6' "$TEST_GH_LOG"
grep -Fxq -- '--prerelease' "$TEST_GH_LOG"
grep -Fxq -- '--latest=false' "$TEST_GH_LOG"
bash "$root/deploy/publish-release.sh" v1.2.3
! grep -Fxq -- '--prerelease' "$TEST_GH_LOG"
! grep -Fxq -- '--latest=false' "$TEST_GH_LOG"
rm -f "$TEST_GH_LOG"
if bash "$root/deploy/publish-release.sh" 'v1.2.3;invalid'; then
    echo "An invalid release tag was not rejected" >&2; exit 1
fi
[[ ! -e "$TEST_GH_LOG" ]]
mkdir "$fixture/repo"
git -C "$fixture/repo" init -q
git -C "$fixture/repo" -c user.name=CI -c user.email=ci@example.invalid \
    commit --allow-empty -qm fixture
git -C "$fixture/repo" -c user.name=CI -c user.email=ci@example.invalid \
    tag -a v1.2.3 -m "$WEB_IMAGE"
[[ $(cd "$fixture/repo" && bash "$root/deploy/release-image.sh" v1.2.3) == "$WEB_IMAGE" ]]
git -C "$fixture/repo" tag v1.2.4
if (cd "$fixture/repo" && bash "$root/deploy/release-image.sh" v1.2.4); then
    echo "A lightweight release tag was not rejected" >&2; exit 1
fi
git -C "$fixture/repo" -c user.name=CI -c user.email=ci@example.invalid \
    tag -a v1.2.5 -m "$old_web"
if (cd "$fixture/repo" && bash "$root/deploy/release-image.sh" v1.2.5); then
    echo "A public image reference was not rejected" >&2; exit 1
fi
bash "$root/deploy/verify-release-image.sh" "$WEB_IMAGE" "$EXPECTED_SOURCE_SHA"
if MOCK_IMAGE_REVISION="$(printf 'f%.0s' {1..40})" \
    bash "$root/deploy/verify-release-image.sh" "$WEB_IMAGE" "$EXPECTED_SOURCE_SHA"; then
    echo "An image from another source revision was not rejected" >&2; exit 1
fi
if bash "$root/deploy/verify-release-image.sh" "$old_web" "$EXPECTED_SOURCE_SHA"; then
    echo "A public image was not rejected" >&2; exit 1
fi
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
if WEB_IMAGE="$old_web" bash "$fixture/bin/deploy-release.sh"; then
    echo "A public release image was not rejected" >&2; exit 1
fi
[[ ! -s "$TEST_LOG" ]]
if FAIL_PREVIOUS_IMAGE=1 bash "$fixture/bin/deploy-release.sh"; then
    echo "A deployment without a locally available rollback image was not rejected" >&2; exit 1
fi
! grep -q 'up -d --no-build --no-deps web' "$TEST_LOG"
: > "$TEST_LOG"

bash "$fixture/bin/deploy-release.sh"
grep -Fx "WEB_IMAGE=$WEB_IMAGE" "$fixture/deploy/.env"
grep -Fx "DB_IMAGE=$old_db" "$fixture/deploy/.env"
grep -F "$GITHUB_REF_NAME $WEB_IMAGE" "$fixture/state/current-release"
[[ $(grep -c 'up -d --no-build --no-deps web' "$TEST_LOG") == 1 ]]
! cut -d '|' -f1 "$TEST_LOG" | grep -E 'down|up .* db|pull .*postgis'
[[ $(stat -c '%a' "$fixture/deploy/.env") == 600 ]]

reset_fixture
AUTHENTICATION_ENABLED=true PUBLIC_BASE_URL=https://larbornhome.asuscomm.com:8010 \
    bash "$fixture/bin/deploy-release.sh"
grep -F "$GITHUB_REF_NAME $WEB_IMAGE" "$fixture/state/current-release"

reset_fixture
if AUTHENTICATION_ENABLED=true PUBLIC_BASE_URL=https://larbornhome.asuscomm.com:8010 FAIL_AUTH=1 \
    bash "$fixture/bin/deploy-release.sh"; then
    echo "Authentication-disabled deployment unexpectedly succeeded" >&2; exit 1
fi
grep -Fx "WEB_IMAGE=$old_web" "$fixture/deploy/.env"
grep -Fx 'previous release' "$fixture/state/current-release"
[[ $(grep -c 'up -d --no-build --no-deps web' "$TEST_LOG") == 2 ]]

reset_fixture
if FAIL_HEALTH=1 bash "$fixture/bin/deploy-release.sh"; then
    echo "Unhealthy deployment unexpectedly succeeded" >&2; exit 1
fi
grep -Fx "WEB_IMAGE=$old_web" "$fixture/deploy/.env"
grep -Fx 'previous release' "$fixture/state/current-release"
[[ $(grep -c 'up -d --no-build --no-deps web' "$TEST_LOG") == 2 ]]
grep -F " | $old_web | $old_db" "$TEST_LOG"
! cut -d '|' -f1 "$TEST_LOG" | grep -E 'down|up .* db|pull .*postgis'

reset_fixture
sed -i "s|^WEB_IMAGE=.*|WEB_IMAGE=$old_private_web|" "$fixture/deploy/.env"
if FAIL_HEALTH=1 bash "$fixture/bin/deploy-release.sh"; then
    echo "Unhealthy deployment unexpectedly succeeded" >&2; exit 1
fi
grep -Fx "WEB_IMAGE=$old_private_web" "$fixture/deploy/.env"
grep -F " | $old_private_web | $old_db" "$TEST_LOG"
echo "Deployment validation, database isolation, and web rollback tests passed."
