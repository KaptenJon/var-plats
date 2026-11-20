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
ingen bilduppladdning i demon, inga poäng. Reload är ingen beständig lagringsgaranti.

Aspire: samma UI → samma domänvalidering → EF Core → PostGIS geography(Point,4326)
med GiST-index. Longitud är X, latitud är Y. Latitud begränsas till ±85 för
Web Mercator-kartan; ingen automatisk precisionsökning eller spårning.
Kartan hämtar rapporterna i sin synliga vy: högst 200, senast rapporterade först,
och listan visar när fler finns så att användaren kan zooma in. Små vyer går via
GiST-indexet med en förtätad och marginalutökad omslutning, eftersom geography-
polygonens kanter är storcirkelbågar; en exakt latitud/longitud-kontroll tar bort
marginalen. Mycket breda vyer och vyer över datumgränsen använder bara den exakta
kontrollen. Läs-API:t tar `south`, `west`, `north` och `east` tillsammans och svarar
med `X-Reports-Truncated`; utan dem, och om kartan inte laddas, visas de senaste 200
rapporterna totalt. Paginering finns inte ännu.

Kartvyn kan delas: adressfältet följer kartan som `?lat=&lon=&z=` och
`?rapport=<id>` öppnar kartan på en publicerad rapport. Värdena tolkas av
`MapView` och ogiltiga värden ger startvyn. Länken innehåller bara en plats som
någon valt på kartan, aldrig besökarens position, och ingen position sparas.

När någon fyller i en ny rapport visar formuläret upp till fem publicerade
rapporter inom 50 meter från vald plats, närmast först, så att dubbletter kan
upptäckas före inskick. Databasen använder `ST_DWithin` på geography-kolumnen,
alltså meter och GiST-index. Väntande inskick visas aldrig. Tipset stoppar inte
inskicket och påverkar inte några grindar.

`Reporting:Demo` och `Reporting:LocalWrites` får bara användas i Development.
All trafik i dessa lägen måste komma från loopback. `Reporting:PublicWrites` är
avstängd som standard och kräver både autentisering och ett uttryckligt
`Reporting:WorkflowEnabled`. Inloggade medlemmars inskick väntar då på
administratörens beslut; borttagning kräver admin. Databasrollen får skriva
först efter separat manuell grant. Aktivera inte funktionen innan lanseringskraven
i SECURITY.md och integritetsutkasten är hanterade.

Den inaktiva EF-migrationen skapar en separat tabell för inskick och kontolänk.
Endast godkända inskick kopieras till den befintliga publika rapporttabellen.
Det gör att äldre rapporter och läs-API:t fungerar utan det nya schemat och
att väntande/avvisade inskick inte blir publika om funktionen stängs av.
Kontoborttagning tar bort länken, inte de sparade rapporterna.

En separat, också inaktiv migration lägger till åtgärdsuppgifter på publicerade
rapporter och bekräftelser från andra konton. Åtgärdstext visas direkt, utan
förhandsgranskning; den beskriver en uppgift, inte ett systemverifierat arbete.
En sammansatt primärnyckel begränsar bekräftelser till en per åtgärd och konto.
Kontoborttagning anonymiserar åtgärder och raderar kontots bekräftelser via
främmande nycklar. Admin kan ta bort åtgärdstext och dess bekräftelser.
Rapportens visade status beräknas vid läsning: ingen åtgärd, obekräftad åtgärd,
eller minst en åtgärd som bekräftats av ett annat konto. När arbetsflödet är
avstängt frågar läsningen inte efter de nya tabellerna och visar den tidigare
lagrade statusen; då fungerar även ett äldre schema. Inga kontonummer visas publikt.

Databasschemat ligger i versionsmärkt init-SQL. Det skapas inte automatiskt av
EF vid varje uppstart. Senare schemaändringar kräver separat granskad migration,
backup, återställningsprov och kompatibilitetsplan. Init-script är inte migrationer.
Åtgärdsmigrationen och nya databasbehörigheter tillämpas inte automatiskt vid release.

Anmälan av innehåll är en separat avstängd skiva med egen migration
(`ContentFlags`) och egen inställning, `Reporting:FlagsEnabled`, som kräver
arbetsflödet. Behöriga konton kan anmäla en publicerad rapport eller åtgärdstext
med en anledning och en frivillig kommentar. Admin ser en kö grupperad per objekt,
utan anmälarens konto, och kan avfärda anmälningarna eller ta bort innehållet
med de befintliga borttagningskommandona. Unikt index, dygnsgräns per konto och
kaskadradering hålls i databasen.

Arkivering är också en separat avstängd skiva med egen migration
(`ReportArchive`) och egen inställning, `Reporting:ArchiveEnabled`, som kräver
arbetsflödet. Admin kan arkivera en publicerad rapport som åtgärdad, inaktuell,
dubblett eller annat, med en frivillig publik kommentar, och återställa den.
Tillståndet ligger i `report_archives` och historiken i `report_archive_events`,
så den publika rapporttabellen ändras inte. Läsningar för karta, lista, API och
närliggande tips utesluter arkiverade rapporter bara när inställningen är på.

Återtagande av bekräftelser är en separat avstängd skiva med egen migration
(`ConfirmationWithdrawals`) och egen inställning,
`Reporting:ConfirmationWithdrawalEnabled`, som kräver arbetsflödet. Det konto som
bekräftat kan ta tillbaka sin bekräftelse från rapportsidan efter ett
bekräftelsesteg. Raden flyttas i en sats från `report_action_confirmations` till
historiken `report_action_confirmation_withdrawals`, så statusberäkningen, som
bara läser aktiva bekräftelser, inte behöver ändras. Omprövning efter
återtagande nekas: en ny bekräftelse från samma konto skulle låta statusen växla
fram och tillbaka och göra historiken svårare att tolka. Den som ångrar sig kan
i stället anmäla innehållet eller be administratören ta bort åtgärden. Arkiverade
rapporter nekar återtaganden när arkivering är på. Antalet återtaganden visas
utan konto; kontoborttagning anonymiserar historiken. Delade `?rapport=`-länkar
påverkas inte: kartmarkören får den omräknade statusen och sidan läses som förut.

Skrivgränser är en egen avstängd skiva utan migration, `Reporting:RateLimits`.
Blazor-komponenterna anropar kommandoklasserna i processen, så HTTP-middleware
ser inte skrivningarna. `WriteRateLimiter` är därför en singleton i
kommandolagret med fast tidsfönster per konto-id och kommandotyp
(`Report`, `Action`, `Confirmation`, `Flag`, `Photo`) och använder
`TimeProvider` så att fönster kan provas utan att vänta. Den anropas efter
behörighets- och valideringskontroll men före lagring; en bilduppladdning tar
båda sina tillstånd eller inget. Läs-API:t får en separat policy i ASP.NET
Core:s rate limiter per klientadress. Tillståndet ligger i minnet och delas inte
mellan processer, vilket räcker för en instans på Pi.

Den separat avstängda bildskivan tar emot högst en bild samtidigt med ett nytt
inskick eller en ny åtgärd. Skanning och metadatafri omkodning sker innan
texten sparas; filen placeras atomärt och bildmetadata och förälder sparas i
samma databastransaktion.
Rapportbildens åtkomst följer granskningsstatus, ägare och adminbehörighet;
åtgärdsbilden följer den publicerade åtgärden. Bildposten pekar på föräldern,
inte på kontot. Filer ligger i en privat katalog utanför webroot, aldrig i
statiska assets. Antals-/bytekvot räknar även kvarvarande övergivna filer och
använder ett databaslås för samtidiga skrivningar. Raderade föräldrar spärrar
åtkomst direkt; fysisk städning försöks och kan upprepas. Kod och migration
körs inte i förhandsversionen och kräver separat säkerhets- och driftsgranskning.

## Nästa moduler och regler

1. **Moderering:** driftsättning av granskningskö och anmälningar (kod finns,
   avstängd bakom `Reporting:FlagsEnabled`), överklaganden,
   begränsningar och återställning. Skrivgränser finns bakom
   `Reporting:RateLimits:Enabled`; avvägning av gränser för drift återstår.
2. **Rapporter:** arkivering finns bakom `Reporting:ArchiveEnabled`; sammanslagning
   av dubbletter och säker
   driftsättning av den avstängda bildpipelinens kontroller och moderering.
3. **Åtgärder:** den avstängda grundskivan sparar beskrivningar på publicerade
   rapporter; bevis, missbruksflöde och en säker lansering återstår.
4. **Bekräftelser:** grundskivan tillåter ett annat konto och förbjuder
   självbekräftelse; återtagande med historik finns bakom
   `Reporting:ConfirmationWithdrawalEnabled`. Skydd mot samordnad manipulation
   och tvister återstår.
5. **Poäng:** spårbar regelversion och händelsebok från godkända bekräftelser.
   Ingen belöning för riskfyllda insatser, dubbla konton eller rent antal klick.
6. **Organisationer:** verifierade organisationer, geografiska områden och
   behörighetsstyrd bevakning/notifiering; inte automatisk officiell felanmälan.
   Avtal, abonnemang och betalning är senare separata beslut.

En modul får införas när ett konkret flöde behöver den, inte som tomt lagerverk.
Skala först när mätningar visar behov. Blazor Server behöver beständig anslutning
och minne per krets: prova realistisk samtidig last på den faktiska Pi:n.
