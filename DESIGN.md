# Vår plats – visuell grund

## Riktning

Praktiskt fältblad med orientering som referens: kartan är arbetsyta, rapporterna
är namngivna observationer bredvid den. Inte en marknadsföringslandning eller
dashboard med påhittad statistik. Användaren godkände att vi väljer en tydlig,
praktisk formgivning. Målscen: boende med mobil i dagsljus.

## System

Vit arbetsyta, skogsgrön accent `#164c3c`, mörk text `#20342c`, sekundär text
`#52655c` och blekgrön information. Tunn neutral avdelning, få ramar, ingen
dekorativ gradient. Systemtypsnitt Segoe UI/system-ui ger tydliga svenska tecken
utan externa fonthämtningar. Rubriker är korta och handlingsnära.

En stor karta och en 390px rapportpanel på bred skärm. Under 850px placeras kartan
före formulär/lista och får en begränsad viewporthöjd. Attribution ska alltid
synas och får aldrig döljas under UI. Karta/lista erbjuder samma rapporter;
koordinatfält gör platsval möjligt utan pekdon på kartan.

## Komponenter och tillstånd

Radzen används för primära knappar, övriga formulär är Blazor-komponenter med
synliga etiketter och servervalidering. Minst 44px formulärkontroller,
tydlig fokusmarkering, skip-länk och dokumentets svenska språk.
Markeringssymbol kompletterar färg. Inga enbart färgbaserade statusar.

Lokal demo och skrivskyddad förhandsversion märks i tydlig informationsrad.
Laddning, tom lista, valideringsfel, sparfel och kartfel har synliga texter.
Rapportlistan kan användas även om kartan inte kan laddas. Reducerad rörelse
respekteras; panorering från listan gör ingen animerad förflyttning.

## Fortsatt formgivning

Behåll kartans prioritet och skilj observerat, dokumenterad åtgärd och bekräftat.
Inga klickpoäng, falska framstegstal eller riskuppmuntrande belöningar.
Verifiera på riktig mobil i solljus och med hjälpmedel före publik lansering.
