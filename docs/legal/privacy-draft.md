# Integritet – EJ LANSERINGSKLART UTKAST

**Inte en färdig GDPR-information eller ett samtycke. Ingen publik datainsamling
ska öppnas utifrån detta utkast.** Personuppgiftsansvarig och kontakt är inte utsedda.

## Nuvarande grund

Lokal demo använder syntetiska rapporter och tillfälligt kretsminne.
Databasläge kan lagra rapporttext, koordinater och tidpunkt; använd bara syntetiska
uppgifter. Server-/infrastrukturloggar kan innehålla IP och tekniska feluppgifter.
Ingen analys, reklam, bilduppladdning eller automatisk enhetspositionering finns.

Kartan laddar tiles direkt från OSMF: IP, webbläsaruppgifter, referer och begärda
kartutsnitt lämnas till tjänsten. Se [OSMF:s policy](https://osmfoundation.org/wiki/Privacy_Policy).
Webbläsarens vanliga HTTP-cache gäller; vår serviceworker lagrar inga tiles
eller rapporter. Blazor använder en nödvändig anslutning. Installerat PWA-skal
ger inte offlinekartor eller offlinepublicering.

## Planerade behandlingar måste specificeras

Ändamål och rättslig grund ska anges separat för konto, rapporter, moderering,
bedrägeriskydd, poäng, organisationsbevakning, tekniska loggar och kommunikation.
Samtycke är inte en universallösning. Uppgifter kan avse både användaren och
tredje person. Koordinater och text kan indirekt identifiera människor.

Mottagare, biträden, avtal, datalagringsregion, tredjelandsöverföring och
eventuella skyddsåtgärder ska inventeras. Organisatoriska kunders roller och
eventuellt gemensamt ansvar måste utredas. Offentlighet är en särskild risk.

## Bilder och dataminimering

Före uppladdning krävs borttagning av EXIF/GPS, kontroll av ansikten,
registreringsnummer och andra personuppgifter, filkontroll och moderering.
Varna innan publicering, minimera precision när det är lämpligt och erbjud
snabb döljning. Ingen känslig information ska efterfrågas.

## Lagring, radering och rättigheter

Retention för varje datatyp är **EJ BESLUTAD**. Bestäm korta motiverade tider,
automatisk radering, loggretention och hur backupkopior åldras ut. Konto-
radering måste hantera innehåll, bekräftelser, poäng, sökindex, notifieringar
och mottagare; pseudonymisering är inte automatiskt anonymisering.

Rätt till information, tillgång, rättelse, radering, begränsning, invändning
och dataportabilitet där tillämpligt ska kunna utövas via säker kontaktväg.
Identitet ska verifieras proportionerligt. Klagomål kan lämnas till IMY
(Integritetsskyddsmyndigheten). Slutlig information måste ange kontakt,
handläggning och vilka rättigheter som faktiskt gäller för respektive behandling.

## Kvarstående lanseringsbeslut

Register över behandlingar, laglig grund, berättigat-intresse-bedömning där relevant,
barn/åldersgräns, risk-/DPIA-bedömning, personuppgiftsincidentprocess och
72-timmarsbedömning, biträden/överföringar, kontakt, retention och raderingsprov.
Granska även lagring i terminalutrustning och behovet av val för icke-nödvändig
teknik innan sådan läggs till.
