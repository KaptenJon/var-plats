# Projektregler för kodassistenter

Läs README och SECURITY. Detta är en modulär monolit, inte mikrotjänster.
Svenskt användargränssnitt. Ändra inte de avsiktliga spärrarna mot publika
skrivningar utan autentisering/auktorisering. Inga riktiga personuppgifter.

Verifiera med solutionens build/test och håll SDK/paket/lockfiler kompatibla.
OSM-attribution och referer ska bevaras. Förhämta eller offlinecachelagra aldrig
OSM-tiles. Organisationsbevakning är inte officiell felanmälan.
Projektets egen kod omfattas av MIT enligt `LICENSE`; detta löser inte
tredjepartskomponenternas licens- eller källkodsvillkor. Välj inte kodlicens åt
ägaren; juridiska dokument är inte lanseringsklara.

Vid review: prioritera behörighet, personuppgifter, geografi/koordinatordning,
validering, riskfyllda frivilliginsatser, databasändringar och fork-PR-säkerhet.
Denna fil instruerar tillgängliga assistenter; den aktiverar inte automatiskt
Copilot-review eller någon kontoberoende funktion.
