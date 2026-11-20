#!/usr/bin/env bash
set -euo pipefail
unset AUTHENTICATION_ENABLED PUBLIC_BASE_URL FAIL_HEALTH FAIL_AUTH AUTH_ROUTE_STATUS FAIL_PREVIOUS_IMAGE \
    REPORTING_WORKFLOW_ENABLED REPORTING_PUBLIC_WRITES REPORTING_FLAGS_ENABLED REPORTING_ARCHIVE_ENABLED \
    REPORTING_CONFIRMATION_WITHDRAWAL_ENABLED REPORTING_POINTS_ENABLED RATE_LIMITS_ENABLED PHOTOS_ENABLED \
    POINTS_CONFIRMED_ACTION_POINTS POINTS_MAX_GIFT_PER_TRANSFER POINTS_MAX_GIFT_PER_DAY POINTS_RULE_VERSION \
    POINTS_GIFTS_ENABLED MOCK_PREFLIGHT_MISSING FAIL_PREFLIGHT
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
    'REPORTING_WORKFLOW_ENABLED: "true"' \
    'REPORTING_PUBLIC_WRITES: "true"' \
    'REPORTING_FLAGS_ENABLED: "true"' \
    'REPORTING_ARCHIVE_ENABLED: "true"' \
    'REPORTING_CONFIRMATION_WITHDRAWAL_ENABLED: "true"' \
    'REPORTING_POINTS_ENABLED: "true"' \
    'POINTS_CONFIRMED_ACTION_POINTS: "10"' \
    'POINTS_MAX_GIFT_PER_DAY: "30"' \
    'RATE_LIMITS_ENABLED: "true"' \
    'PHOTOS_ENABLED: "true"' \
    'ADMIN_GITHUB_ID: "10761546"' \
    'install -m 0644 "$GITHUB_WORKSPACE/deploy/compose.yml"' \
    'install -m 0644 "$GITHUB_WORKSPACE/deploy/pi.override.yml"' \
    'install -m 0644 "$GITHUB_WORKSPACE/deploy/clamd.conf"' \
    'install -m 0644 "$GITHUB_WORKSPACE/deploy/freshclam.conf"' \
    'install -m 0755 "$GITHUB_WORKSPACE/deploy/deploy-release.sh"' \
    'deploy/identity-schema.sql' \
    'deploy/pi-identity-role.sql' \
    'AUTHENTICATION_TRUSTED_PROXY_IP'; do
    grep -Fq "$expected" "$release_workflow"
done
# Migreringar och grants är manuella steg; releasen får aldrig tillämpa dem.
for manual in pi-public-write-role.sql pi-workflow-migrations.sql pi-workflow-grants.sql; do
    ! grep -Fq "$manual" "$release_workflow" || exit 1
    ! grep -Eq "(<|psql).*$manual" "$root/deploy/deploy-release.sh" || exit 1
done
# En vanlig Compose-körning förblir skrivskyddad.
for flag in AUTHENTICATION_ENABLED REPORTING_WORKFLOW_ENABLED REPORTING_PUBLIC_WRITES REPORTING_FLAGS_ENABLED \
    REPORTING_ARCHIVE_ENABLED REPORTING_CONFIRMATION_WITHDRAWAL_ENABLED REPORTING_POINTS_ENABLED RATE_LIMITS_ENABLED \
    PHOTOS_ENABLED; do
    grep -Fq "\${$flag:-false}" "$root/deploy/compose.yml"
    grep -Fxq "$flag=false" "$root/deploy/.env.example"
done
grep -Eq 'clamav-debian:[0-9.]+@sha256:[a-f0-9]{64}' "$root/deploy/compose.yml"
! grep -Eq '^(TCPSocket|TCPAddr|User) ' "$root/deploy/clamd.conf" || exit 1
! grep -Eiq 'CREATE TABLE "?reports"?|ALTER TABLE "?reports"?' "$root/deploy/identity-schema.sql"
export TEST_LOG="$fixture/docker.log" TEST_CURL_COUNT="$fixture/curl.count" TEST_GH_LOG="$fixture/gh.log"
export TEST_SQL="$fixture/preflight.sql"
cat > "$fixture/mocks/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s | %s | %s\n' "$*" "${WEB_IMAGE:-}" "${DB_IMAGE:-}" >> "$TEST_LOG"
if [[ ${1:-} == image && ${2:-} == inspect ]]; then
    [[ ${FAIL_PREVIOUS_IMAGE:-0} != 1 ]] || exit 1
    printf '%s\n' "${MOCK_IMAGE_REVISION:-$EXPECTED_SOURCE_SHA}"
fi
if [[ "$*" == *"exec -T db psql"* ]]; then
    cat > "$TEST_SQL"
    [[ ${FAIL_PREFLIGHT:-0} != 1 ]] || exit 3
    [[ -z ${MOCK_PREFLIGHT_MISSING:-} ]] || printf '%s\n' "$MOCK_PREFLIGHT_MISSING"
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
    rm -f "$TEST_CURL_COUNT" "$TEST_SQL"
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
# Med alla brytare avstängda rörs databasen och skannern inte.
! grep -Eq 'psql|clamav|varplats_photos' "$TEST_LOG" || exit 1

all_flags=(AUTHENTICATION_ENABLED=true PUBLIC_BASE_URL=https://larbornhome.asuscomm.com:8010
    REPORTING_WORKFLOW_ENABLED=true REPORTING_PUBLIC_WRITES=true REPORTING_FLAGS_ENABLED=true
    REPORTING_ARCHIVE_ENABLED=true REPORTING_CONFIRMATION_WITHDRAWAL_ENABLED=true REPORTING_POINTS_ENABLED=true
    RATE_LIMITS_ENABLED=true PHOTOS_ENABLED=true POINTS_CONFIRMED_ACTION_POINTS=10 POINTS_RULE_VERSION=poang-v1)

reset_fixture
if env REPORTING_PUBLIC_WRITES=yes bash "$fixture/bin/deploy-release.sh"; then
    echo "An invalid flag value was not rejected" >&2; exit 1
fi
[[ ! -s "$TEST_LOG" ]]
for invalid in POINTS_MAX_GIFT_PER_DAY=0 POINTS_CONFIRMED_ACTION_POINTS=abc 'POINTS_RULE_VERSION=a b' \
    POINTS_GIFTS_ENABLED=ja; do
    if env "$invalid" bash "$fixture/bin/deploy-release.sh" 2> /dev/null; then
        echo "An invalid points setting was not rejected: $invalid" >&2; exit 1
    fi
done
[[ ! -s "$TEST_LOG" ]]

reset_fixture
env "${all_flags[@]}" bash "$fixture/bin/deploy-release.sh"
grep -F "$GITHUB_REF_NAME $WEB_IMAGE" "$fixture/state/current-release"
grep -Fq 'BEGIN READ ONLY;' "$TEST_SQL"
for migration in 20261108154722_ReportReviewWorkflow 20261108162158_ReportActionsAndConfirmations \
    20261108164953_ReportPhotos 20261119061342_ContentFlags 20261119222822_ReportArchive \
    20261120080000_ConfirmationWithdrawals 20261120090000_Points; do
    grep -Fq "('$migration')" "$TEST_SQL"
done
for grant in "('reports','INSERT')" "('report_submissions','UPDATE')" "('report_photos','INSERT')" \
    "('content_flags','UPDATE')" "('report_archives','DELETE')" "('report_archive_events','INSERT')" \
    "('report_action_confirmations','DELETE')" "('report_action_confirmation_withdrawals','INSERT')" \
    "('points_events','INSERT')" "('action_hearts','DELETE')"; do
    grep -Fq "$grant" "$TEST_SQL"
done
# Händelseboken får aldrig kräva UPDATE eller DELETE.
for grant in "('points_events','UPDATE')" "('points_events','DELETE')"; do
    ! grep -Fq "$grant" "$TEST_SQL" || exit 1
done
! grep -Eiq '^[[:space:]]*(INSERT|UPDATE|DELETE|GRANT|CREATE|ALTER|DROP)[[:space:]]' "$TEST_SQL" || exit 1
grep -Fq 'varplats_photos:/photos' "$TEST_LOG"
grep -Fq -- '--profile photos up -d --no-build --wait --wait-timeout 900 clamav' "$TEST_LOG"
# Förkontroll och skanner kommer före bytet av webbcontainer.
preflight_line=$(grep -n 'exec -T db psql' "$TEST_LOG" | cut -d: -f1)
clamav_line=$(grep -n -- '--profile photos up' "$TEST_LOG" | cut -d: -f1)
web_line=$(grep -n 'up -d --no-build --no-deps web' "$TEST_LOG" | cut -d: -f1)
((preflight_line < clamav_line && clamav_line < web_line))

reset_fixture
if env "${all_flags[@]}" MOCK_PREFLIGHT_MISSING='migrering 20261119222822_ReportArchive' \
    bash "$fixture/bin/deploy-release.sh" 2> "$fixture/stderr"; then
    echo "A deployment with missing migrations was not rejected" >&2; exit 1
fi
grep -Fq 'kräver manuella databassteg som saknas' "$fixture/stderr"
grep -Fq '  - migrering 20261119222822_ReportArchive' "$fixture/stderr"
grep -Fx "WEB_IMAGE=$old_web" "$fixture/deploy/.env"
grep -Fx 'previous release' "$fixture/state/current-release"
! grep -Eq 'up -d --no-build --no-deps web|clamav' "$TEST_LOG" || exit 1

reset_fixture
if env "${all_flags[@]}" FAIL_PREFLIGHT=1 bash "$fixture/bin/deploy-release.sh" 2> "$fixture/stderr"; then
    echo "A failed database preflight was not rejected" >&2; exit 1
fi
grep -Fq 'Förkontrollen av databasen misslyckades' "$fixture/stderr"
! grep -q 'up -d --no-build --no-deps web' "$TEST_LOG" || exit 1

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
echo "Deployment validation, feature preflight, database isolation, and web rollback tests passed."
