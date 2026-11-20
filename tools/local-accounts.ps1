<#
.SYNOPSIS
  Startar Vår plats lokalt med PostGIS, syntetiska testkonton och alla funktionsbrytare.

.DESCRIPTION
  Bygger PostGIS-imagen från deploy/postgis, startar containern varplats-local-db på
  127.0.0.1:55432 (initskript från deploy/sql, data i volymen varplats-local-db) och kör
  webben med startprofilen "konton" på http://localhost:5180. Webben kör EF-migreringarna
  mot den lokala databasen och skapar testkontona (se DevAccounts.cs). Endast för den egna
  datorn. Databaslösenordet slumpas när containern skapas och läses sedan från containern;
  det sparas aldrig i repot. Skriptet märker databasen som en lokal utvecklingsdatabas,
  vilket webben kräver innan den kör migreringar och skapar testkonton.

.PARAMETER Reset
  Tar bort containern och volymen först, så att databasen börjar tom.

.PARAMETER DatabaseOnly
  Startar bara databasen och skriver ut anslutningssträngen.
#>
param(
    [switch] $Reset,
    [switch] $DatabaseOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$container = 'varplats-local-db'
$volume = 'varplats-local-db'
# Måste matcha DevAccounts.LocalDatabaseMarker.
$marker = 'varplats-lokal-utveckling'

docker info --format '{{.ServerVersion}}' *> $null
if ($LASTEXITCODE -ne 0) { throw 'Docker körs inte. Starta Docker Desktop och försök igen.' }

if ($Reset) {
    docker rm --force $container *> $null
    docker volume rm $volume *> $null
}

$running = docker ps --filter "name=^$container$" --format '{{.Names}}'
if (-not $running) {
    $exists = docker ps --all --filter "name=^$container$" --format '{{.Names}}'
    if ($exists) {
        docker start $container | Out-Null
    }
    else {
        $bytes = [byte[]]::new(24)
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $newPassword = [Convert]::ToHexString($bytes).ToLowerInvariant()
        Write-Host 'Bygger PostGIS-imagen...'
        docker build --quiet --tag varplats-local-postgis (Join-Path $repo 'deploy/postgis') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'PostGIS-imagen kunde inte byggas.' }
        docker run --detach --name $container `
            --publish 127.0.0.1:55432:5432 `
            --env POSTGRES_USER=varplats --env POSTGRES_PASSWORD=$newPassword --env POSTGRES_DB=varplats `
            --volume "${volume}:/var/lib/postgresql" `
            --volume "$(Join-Path $repo 'deploy/sql'):/docker-entrypoint-initdb.d:ro" `
            varplats-local-postgis | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Databascontainern kunde inte startas.' }
    }
}

Write-Host 'Väntar på databasen...'
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    # Initskripten körs mot en tillfällig server som bara lyssnar på unix-socket; fråga via TCP
    # så att svaret kommer från den riktiga servern efter initieringen.
    docker exec $container psql -h 127.0.0.1 -U varplats -d varplats -tAc "SELECT to_regclass('public.reports') IS NOT NULL" 2>$null |
        Where-Object { $_ -eq 't' } | ForEach-Object { $ready = $true }
    if ($ready) { break }
    Start-Sleep -Seconds 2
}
if (-not $ready) { throw 'Databasen blev inte klar. Se: docker logs varplats-local-db' }

$password = docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' $container |
    Where-Object { $_ -like 'POSTGRES_PASSWORD=*' } | ForEach-Object { $_.Substring('POSTGRES_PASSWORD='.Length) }
if (-not $password) { throw 'Databaslösenordet kunde inte läsas från containern.' }
docker exec $container psql -h 127.0.0.1 -U varplats -d varplats -qc "COMMENT ON DATABASE varplats IS '$marker'"
if ($LASTEXITCODE -ne 0) { throw 'Databasen kunde inte märkas som lokal utvecklingsdatabas.' }

$connection = "Host=127.0.0.1;Port=55432;Database=varplats;Username=varplats;Password=$password"
if ($DatabaseOnly) {
    Write-Host "Databasen är klar: $connection"
    return
}

Write-Host 'Startar webben på http://localhost:5180 (Ctrl+C avslutar). Logga in via /login.'
$env:ConnectionStrings__varplats = $connection
dotnet run --project (Join-Path $repo 'src/VarPlats.Web') --launch-profile konton
