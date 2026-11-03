# Arkitektur och roadmap

## Modulär monolit

En driftsatt ASP.NET Core-process innehåller Blazor Server, Radzen och ett litet
läs-API. `VarPlats.Core` innehåller rapportmodell och validering,
`VarPlats.Web` UI/API och EF-lagringsadapter. `ServiceDefaults` samlar hälsa och
OpenTelemetry. AppHost orkestrerar lokalt, inte som en extra produktionskomponent.
PostgreSQL/PostGIS är en infrastrukturberoende databas, inte en mikrotjänst.

UI-komponenter anropar rapportlagrets gränssnitt direkt i samma process, utan ett
onödigt internt HTTP-hopp. Ett framtida MAUI Blazor Hybrid-gränssnitt kan dela
domän/komponenter och använda ett autentiserat API; det byggs inte nu.

## Nuvarande vertikala skiva

Lokal demo: välj koordinat → validera titel/beskrivning/kategori/plats → skapa
rapport i kretsens minne → uppdatera lista och karta. Ingen publik skrivroute,
ingen bilduppladdning, inga poäng. Reload är ingen beständig lagringsgaranti.

Aspire: samma UI → samma domänvalidering → EF Core → PostGIS geography(Point,4326)
med GiST-index. Longitud är X, latitud är Y. Latitud begränsas till ±85 för
Web Mercator-kartan; ingen automatisk precisionsökning eller spårning.
Läsningen tar de senaste 200 rapporterna; detta är en tydlig prototypbegränsning,
inte geografisk sökning eller paginering. Dessa krävs före skalning.

`Reporting:Demo` och `Reporting:LocalWrites` får bara användas i Development.
All trafik i dessa lägen måste komma från loopback. `DatabaseReportStore`
nekar dessutom skrivning om flaggan inte är aktiverad. Produktionskonfiguration
måste lämna båda avstängda. Ingen proxy får göra detta till en publik demo.

Databasschemat ligger i versionsmärkt init-SQL. Det skapas inte automatiskt av
EF vid varje uppstart. Senare schemaändringar kräver separat granskad migration,
backup, återställningsprov och kompatibilitetsplan. Init-script är inte migrationer.

## Nästa moduler och regler

1. **Identitet och moderering:** konto, roller, verifiering, ägarskap, begränsningar,
   rapportera missbruk och granskningskö före offentliga skrivningar.
2. **Rapporter:** statusövergångar, geografisk sökning, dubbletter, bildpipeline
   med typ-/storlekskontroll, skanning, EXIF-rensning och åtkomstkontroll.
3. **Åtgärder:** separat dokumentation knuten till rapport, beskrivning och bevis,
   inte en påstådd lösning bara för att rapportören trycker på en knapp.
4. **Bekräftelser:** andra användares oberoende bekräftelser; förbjud självbekräftelse,
   begränsa manipulation, hantera återtagna bevis och tvister.
5. **Poäng:** spårbar regelversion och händelsebok från godkända bekräftelser.
   Ingen belöning för riskfyllda insatser, dubbla konton eller rent antal klick.
6. **Organisationer:** verifierade organisationer, geografiska områden och
   behörighetsstyrd bevakning/notifiering; inte automatisk officiell felanmälan.
   Avtal, abonnemang och betalning är senare separata beslut.

En modul får införas när ett konkret flöde behöver den, inte som tomt lagerverk.
Skala först när mätningar visar behov. Blazor Server behöver beständig anslutning
och minne per krets: prova realistisk samtidig last på den faktiska Pi:n.
