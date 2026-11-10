#!/usr/bin/env bash
set -euo pipefail
umask 077

root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
[[ ${GITHUB_REPOSITORY:-} == KaptenJon/var-plats ]]
[[ ${GITHUB_REF_TYPE:-} == tag ]]
[[ ${GITHUB_REF_NAME:-} =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.-]+)?$ ]]
[[ ${WEB_IMAGE:-} =~ ^ghcr\.io/kaptenjon/var-plats-deploy-web@sha256:[a-f0-9]{64}$ ]]
[[ ${DB_IMAGE:-} =~ ^ghcr\.io/kaptenjon/var-plats-postgis@sha256:[a-f0-9]{64}$ ]]
mkdir -p "$root/state"
exec 9>"$root/state/deployment.lock"
flock -n 9

config="$root/deploy/.env"
previous_web=$(sed -n 's/^WEB_IMAGE=//p' "$config")
previous_db=$(sed -n 's/^DB_IMAGE=//p' "$config")
if [[ -f "$root/state/database-ready" ]]; then
    [[ $previous_db =~ ^ghcr\.io/kaptenjon/var-plats-postgis@sha256:[a-f0-9]{64}$ ]]
    [[ $previous_web =~ ^ghcr\.io/kaptenjon/(var-plats-web|var-plats-deploy-web)@sha256:[a-f0-9]{64}$ ]]
    if ! docker image inspect "$previous_web" > /dev/null; then
        echo "The previous web image is not available locally for rollback." >&2
        exit 1
    fi
    if [[ $DB_IMAGE != "$previous_db" ]]; then
        echo "Keeping the existing database image; database upgrades require separate review."
    fi
    export DB_IMAGE="$previous_db"
fi
compose=(docker compose --project-name varplats --env-file "$config"
    -f "$root/deploy/compose.yml" -f "$root/deploy/pi.override.yml")
"${compose[@]}" config --quiet
docker pull "$WEB_IMAGE"

if [[ ! -f "$root/state/database-ready" ]]; then
    [[ -z $("${compose[@]}" ps --all -q db) ]] || {
        echo "An existing database requires manual review before initial provisioning." >&2
        exit 1
    }
    docker pull "$DB_IMAGE"
    "${compose[@]}" up -d --no-build db
    db_id=$("${compose[@]}" ps -q db)
    for attempt in $(seq 1 60); do
        [[ $(docker inspect --format '{{.State.Health.Status}}' "$db_id") == healthy ]] && break
        sleep 2
    done
    [[ $(docker inspect --format '{{.State.Health.Status}}' "$db_id") == healthy ]]
    {
        printf "\\\\set runtime_password '%s'\n" "$(cat "$root/deploy/secrets/runtime_password")"
        cat "$root/deploy/pi-runtime-role.sql"
    } | "${compose[@]}" exec -T db psql -U varplats -d varplats -q -v ON_ERROR_STOP=1
    # A fresh named volume otherwise has root ownership, but the web image runs as app.
    docker run --rm --user 0 --cap-drop ALL --cap-add CHOWN --cap-add DAC_OVERRIDE \
        --security-opt no-new-privileges:true --read-only --network none \
        --volume varplats_dataprotection:/keys --entrypoint chown "$WEB_IMAGE" \
        -R 1654:1654 /keys
    temporary=$(mktemp "$root/deploy/.env.XXXXXX")
    sed "s|^DB_IMAGE=.*|DB_IMAGE=$DB_IMAGE|" "$config" > "$temporary"
    mv "$temporary" "$config"
    touch "$root/state/database-ready"
fi

bind_address=$(sed -n 's/^WEB_BIND_ADDRESS=//p' "$config")
port=$(sed -n 's/^WEB_PORT=//p' "$config")
[[ $bind_address =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]
[[ $port =~ ^[0-9]{1,5}$ ]] && ((port > 0 && port < 65536))
health_url="http://$bind_address:$port/health/ready"
web_changed=false
rollback() {
    status=$?
    trap - EXIT
    if ((status != 0)) && [[ $web_changed == true ]]; then
        if [[ $previous_web =~ ^ghcr\.io/kaptenjon/(var-plats-web|var-plats-deploy-web)@sha256:[a-f0-9]{64}$ ]]; then
            echo "Deployment failed; restoring the previous web image." >&2
            export WEB_IMAGE="$previous_web"
            "${compose[@]}" up -d --no-build --no-deps web
            curl --fail --silent --show-error --retry 15 --retry-all-errors --retry-delay 2 \
                --max-time 5 "$health_url" > /dev/null || true
        else
            echo "Initial web deployment failed; stopping only the new web container." >&2
            "${compose[@]}" stop web
        fi
    fi
    exit "$status"
}
trap rollback EXIT
if [[ ${PHOTOS_ENABLED:-false} == true ]]; then
    # Named volumes start root-owned; the app and clamd both run as UID 1654 and require private directories.
    docker run --rm --user 0 --cap-drop ALL --cap-add CHOWN --cap-add DAC_OVERRIDE --cap-add FOWNER \
        --security-opt no-new-privileges:true --read-only --network none \
        --volume varplats_photos:/photos --volume varplats_scanner-socket:/scanner \
        --entrypoint sh "$WEB_IMAGE" -c \
        'chown 1654:1654 /photos /scanner && chmod 0700 /photos /scanner'
    "${compose[@]}" up -d --no-build --wait clamav
fi
web_changed=true
"${compose[@]}" up -d --no-build --no-deps web
curl --fail --silent --show-error --retry 30 --retry-all-errors --retry-delay 2 \
    --max-time 5 "$health_url" > /dev/null
if [[ ${AUTHENTICATION_ENABLED:-false} == true ]]; then
    public_host=${PUBLIC_BASE_URL#*://}
    public_host=${public_host%%/*}
    [[ -n "$public_host" ]] || {
        echo "Authentication smoke test requires PUBLIC_BASE_URL." >&2
        exit 1
    }
    web_url="http://$bind_address:$port"
    login_html=$(curl --fail --silent --show-error --retry 15 --retry-all-errors --retry-delay 2 \
        --max-time 5 -H "Host: $public_host" "$web_url/login")
    if ! grep -Fq "Logga in med en passkey" <<< "$login_html"; then
        echo "Authentication smoke test failed: the login UI is not enabled." >&2
        exit 1
    fi
    auth_route_status=$(curl --silent --output /dev/null --write-out '%{http_code}' \
        --max-time 5 -H "Host: $public_host" "$web_url/auth/signin/google")
    if [[ $auth_route_status != 405 ]]; then
        echo "Authentication smoke test failed: external sign-in route is unavailable." >&2
        exit 1
    fi
fi
temporary=$(mktemp "$root/deploy/.env.XXXXXX")
sed "s|^WEB_IMAGE=.*|WEB_IMAGE=$WEB_IMAGE|" "$config" > "$temporary"
mv "$temporary" "$config"
printf '%s %s\n' "$GITHUB_REF_NAME" "$WEB_IMAGE" > "$root/state/current-release"
web_changed=false
echo "Release $GITHUB_REF_NAME is healthy. No other Compose projects were changed."
