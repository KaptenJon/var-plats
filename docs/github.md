# GitHub: faktisk automation och öppna kontoinställningar

Repositoryt är publikt. Vid kontroll 2026-11-03 rapporterade GitHub API att
secret scanning och push protection var aktiverade. Dependabot security updates
var först avstängda; de och privat sårbarhetsrapportering aktiverades och
verifierades därefter via API. Inga rulesets fanns. Dokumenterade workflows ger automatiska
**kontroller**, inte mänsklig eller AI-baserad PR-approval.

## Incheckat

CI bygger/testar och kontrollerar formatering samt kör ARM64/Compose/PostGIS-
smoke på GitHub-hostad ARM64-runner. Security workflow kör transitiv NuGet-audit
och CodeQL; det senare behöver GitHubs stöd för den använda SDK-versionen och
kan inte garanteras enbart genom YAML. Dependabot föreslår uppdateringar.

Actions är pinnade till commits som verifierats via upstream GitHub API.
Tokenbehörigheter är minimala per jobb. Fork-PR kör inte med secrets eller
privilegierad token, och `pull_request_target` används inte.
CodeQL-jobbet med security-events:write kör inte fork-PR. Granska fork-diff innan
den kommer in i betrodd branch. CI och PR-jobb använder GitHub-hostade runners.
Release-deployment kan använda en separat ARM64 self-hosted runner med label
`varplats-deploy`, endast efter ägarens obligatoriska approval i `pi-preview`.
Den checkar inte ut repositorykod och får inte användas av PR-jobb.
Se [driftanvisningen](operations.md) för kvarstående risker på en delad värd.

## Copilot-review är inte aktiverat av detta repo

Automatisk Copilot code review beror på kontots plan, tilldelning, policy och
repositoryinställningar. API-kontrollen gav inte bevis för aktiverad review.
`copilot-instructions.md` innehåller reviewvägledning men köper/aktiverar inget.
Ägaren måste kontrollera **Settings → Rules / Copilot** i aktuell GitHub-UI,
eventuell inkluderad kvot och kostnad innan automatisk review väljs.
Ingen betalning eller AI-tjänst har aktiverats. Det finns ingen verifierad
produktfunktion här som ska kallas "GitHub 2.0".

## Rekommenderad ägarkontroll

Aktivera privat sårbarhetsrapportering och Dependabot security updates om
kontot stöder det; verifiera därefter status. Lägg ett skydd/ruleset på main
med godkända CI-kontroller och granskning när första grundrevisionen finns.
Skydda release-taggar så att endast betrodda ansvariga kan skapa dem.
Kontrollera Actions-/Packages-kvoter och offentliga imagevillkor; aktivera inga
betalda runners eller tjänster. Release-workflow startas först av en avsiktlig
versions-tagg på main-historiken. Ingen release taggas automatiskt här.

Det första arbetet pushas utan beställd PR; branch-/registreringsflytt sker först
när koden är säkrad på GitHub. Granska faktiska Actions-resultat efter push.
