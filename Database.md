# fxvol — Databasschema

> Databas: MySQL, schema `fxvol`  
> Används av: FxFixGateway (skrivning) och externa appar t.ex. Active Market Data Book (läsning)

---

## Tabellöversikt

| Tabell | Syfte |
|--------|-------|
| `market_instruments` | Instrumentkatalog — alla kända instrument per session |
| `market_data_snapshots` | En rad per inkommande 35=W (FIX snapshot-meddelande) |
| `market_data_entries` | Detaljrader per MDEntry i ett snapshot (Bid/Ask/Trade) |
| `active_market_book` | Realtidsbok — senaste aktiva Bid/Ask per instrument och position |
| `market_trades` | Registrerade affärer (MDEntryType=2) ur snapshots |
| `quote_requests` | Inkommande RFQ-meddelanden (35=R) — Volbroker |
| `session_heartbeat` | En rad per session: status, senaste heartbeat och senaste inloggning (`logon_utc`) |
| `fxohub_rfq` | FXOhub-RFQ:er (35=R), en rad per fråga med status |
| `fxohub_rfq_leg` | Benen i en FXOhub-RFQ |
| `fxohub_rfq_quote` | Aktuellt djup per FXOhub-RFQ (35=W i RFQ-sessionen), en rad per pris |
| `fxohub_rfq_quote_entry` | Benpriser per pris i djupet |
| `fxohub_rfq_event` | Varje FXOhub-RFQ-meddelande som händelse (revision, hit ratio) |
| `fxohub_rfq_price` | Våra priser på FXOhub-RFQ:er, en rad per version (skrivs av pricern, skickas av gatewayen) |
| `fxohub_rfq_price_leg` | Benen i en prisversion (vol bid/offer, strike, delta) |
| `bloomberg_rfq` | Bloomberg FXGO Options-RFQ:er (35=R), en rad per fråga med status och vår kvittens |
| `bloomberg_rfq_leg` | Benen i en Bloomberg-RFQ |
| `bloomberg_rfq_event` | Varje meddelande på en Bloomberg-session som händelse, våra (35=AI) också |

Referenstabell: `vol_tenor_def` — tillåtna tenorkoder (OD, ON, 1M, 3M, 6M, 1Y …)

---

## Relationsdiagram
market_instruments
↑ (security_id, session_key)
|
market_data_snapshots ──────────────┐
↑ (snapshot_id) │
│ │
market_data_entries active_market_book
market_trades

---

## `fxvol.market_instruments`

Instrumentkatalog. Populeras via SecurityList (35=y) från venue. En rad per `(session_key, security_id)`.

| Kolumn | Typ | FIX-tag | Beskrivning |
|--------|-----|---------|-------------|
| `id` | bigint PK | — | Surrogatnyckel |
| `session_key` | varchar | — | FIX-session (t.ex. `VOLB_FIXHUB_DEV`) |
| `security_id` | varchar | 48 | Venues unika instrument-ID |
| `symbol` | varchar | 55 | Ticker-symbol |
| `currency_pair` | varchar | — | t.ex. `EUR/USD` |
| `product` | int | 460 | 19=Runs (ATM+RR+BF), 20=Specific Interest |
| `security_req_id` | varchar | 320 | Från 35=x-request |
| `tenor` | varchar | 620 | OD/ON/1W/1M/3M… |
| `expiry_date` | datetime | 611 | Options-förfallodatum |
| `cut` | varchar | 598 | NY/TK/LN |
| `strategy` | varchar | 310 | Single Leg/Straddle/RR/BF/Generic Spread |
| `delta` | varchar | 763 | ATM/25D/10D |
| `strike` | varchar | 612 | ATMf/DN/25… |
| `quote_style` | varchar | 691 | VOL/PCT |
| `delta_style` | varchar | 876 | FWD/SPOT |
| `premium_ccy` | varchar | 556 | Premiumvaluta |
| `amount_ccy` | varchar | 318 | Beloppsvaluta |
| `is_subscribed` | bool | — | true = 35=V skickad för detta instrument |
| `discovered_utc` | datetime | — | När instrumentet hittades via 35=y |
| `updated_utc` | datetime | — | Senast uppdaterad |

**Unik nyckel:** `(session_key, security_id)`

---

## `fxvol.market_data_snapshots`

En rad per inkommande 35=W-meddelande. Fungerar som header till `market_data_entries`.

| Kolumn | Typ | FIX-tag | Beskrivning |
|--------|-----|---------|-------------|
| `id` | bigint PK | — | Surrogatnyckel |
| `session_key` | varchar | — | FIX-session |
| `security_id` | varchar | 48 | Instrument-ID |
| `md_req_id` | varchar | 262 | MarketDataRequest-referens |
| `currency_pair` | varchar | — | Denormaliserat från `market_instruments` |
| `product` | int | — | Denormaliserat från `market_instruments` |
| `tenor` | varchar | 620 | Denormaliserat |
| `cut` | varchar | 598 | Denormaliserat (NY/TK/LN) |
| `strategy` | varchar | 310 | Denormaliserat |
| `delta` | varchar | 763 | Denormaliserat |
| `raw_payload` | longtext | — | Hela FIX-meddelandet som råtext |
| `received_utc` | datetime | — | Mottagingstidpunkt |
| `entry_count` | int | 268 | Antal MDEntry-rader i meddelandet |

---

## `fxvol.market_data_entries`

Detaljrader per MDEntry i ett snapshot. En rad per Bid, Ask eller Trade.

| Kolumn | Typ | FIX-tag | Beskrivning |
|--------|-----|---------|-------------|
| `id` | bigint PK | — | Surrogatnyckel |
| `snapshot_id` | bigint FK | — | → `market_data_snapshots.id` |
| `security_id` | varchar | 48 | Instrument-ID |
| `md_entry_type` | varchar | 269 | 0=Bid, 1=Offer, 2=Trade |
| `price` | decimal | 270 | Volatilitet eller pris |
| `size` | decimal | 271 | Storlek i miljoner |
| `quote_condition` | varchar | 276 | A=Active, I=Inactive, G=Depth, C=Closed |
| `trade_condition` | varchar | 277 | G=Given (bid hit), T=Taken (offer lifted) |
| `position_no` | int | 290 | Position i depth-boken |
| `originator` | varchar | 282 | Ursprungspart |
| `trader_id` | varchar | 9536 | Trader-ID |
| `exec_inst` | varchar | 18 | Execution instructions |
| `scope` | varchar | 546 | Scope |
| `entry_date` | date | 272 | Handelsdatum (för trades) |
| `entry_time` | time | 273 | Handelstid (för trades) |

---

## `fxvol.active_market_book`

Realtidsbok med senaste aktiva priser. Upsert vid varje uppdatering; soft-delete (`is_active=0`) vid tomt marknad (`268=0`). Primär läskälla för externa appar.

| Kolumn | Typ | FIX-tag | Beskrivning |
|--------|-----|---------|-------------|
| `id` | bigint PK | — | Surrogatnyckel |
| `security_id` | varchar | 48 | Instrument-ID |
| `session_key` | varchar | — | FIX-session |
| `currency_pair` | varchar | — | t.ex. `EUR/USD` |
| `md_entry_type` | varchar | 269 | 0=Bid, 1=Ask |
| `position_no` | int | 290 | Position i boken |
| `price` | decimal | 270 | Aktuellt pris/volatilitet |
| `size` | decimal | 271 | Storlek i miljoner |
| `originator` | varchar | 282 | Ursprungspart |
| `trader_id` | varchar | 9536 | Trader-ID |
| `quote_condition` | varchar | 276 | A/G/I/C |
| `is_active` | bool | — | false = soft-deleted |
| `snapshot_id` | bigint FK | — | → `market_data_snapshots.id` |
| `updated_utc` | datetime | — | Senast ändrad |

**Unik nyckel:** `(security_id, session_key, md_entry_type, position_no)`

---

## `fxvol.market_trades`

Registrerade affärer extraherade ur snapshots (MDEntryType=2).

| Kolumn | Typ | FIX-tag | Beskrivning |
|--------|-----|---------|-------------|
| `id` | bigint PK | — | Surrogatnyckel |
| `security_id` | varchar | 48 | Instrument-ID |
| `session_key` | varchar | — | FIX-session |
| `currency_pair` | varchar | — | Denormaliserat |
| `tenor` | varchar | 620 | Denormaliserat |
| `cut` | varchar | 598 | Denormaliserat (NY/TK/LN) |
| `strategy` | varchar | 310 | Denormaliserat |
| `delta` | varchar | 763 | Denormaliserat |
| `price` | decimal | 270 | Affärspris/volatilitet |
| `size` | decimal | 271 | Affärsstorlek |
| `trade_date` | date | 272 | Handelsdatum |
| `trade_time` | time | 273 | Handelstid |
| `trade_condition` | varchar | 277 | G=Given/bid hit, T=Taken/offer lifted |
| `snapshot_id` | bigint FK | — | → `market_data_snapshots.id` |
| `received_utc` | datetime | — | Mottagingstidpunkt |

---

## `fxvol.quote_requests`

Inkommande RFQ-förfrågningar (35=R). En rad per leg.

| Kolumn | Typ | FIX-tag | Beskrivning |
|--------|-----|---------|-------------|
| `id` | bigint PK | — | Surrogatnyckel |
| `session_key` | varchar | — | FIX-session |
| `quote_req_id` | varchar | 131 | Unikt RFQ-ID |
| `security_id` | varchar | 48 | Instrument-ID |
| `symbol` | varchar | 55 | Symbol |
| `product` | int | 460 | 20=Specific Interest |
| `quote_style` | varchar | 691 | VOL/PCT |
| `delta_style` | varchar | 876 | SPOT/FWD |
| `strategy_type` | varchar | 310 | Single Leg/Straddle/Strangle/RR/BF/Generic Spread |
| `currency_pair` | varchar | 600 | t.ex. `EUR/USD` |
| `put_or_call` | varchar | 764 | C=Call, P=Put |
| `tenor` | varchar | 620 | Tenor |
| `expiry_date` | varchar | 611 | YYYYMMDD |
| `cut` | varchar | 598 | NY/TOK/LON |
| `strike_price` | decimal | 612 | Strike |
| `leg_order_qty` | decimal | 623 | Nominellt belopp per leg |
| `notional_currency` | varchar | 622 | Valutatecken för nominellt |
| `leg_side` | varchar | 624 | B=Buy, S=Sell |
| `premium_currency` | varchar | 556 | Premiumvaluta |
| `raw_payload` | longtext | — | Hela FIX-meddelandet |
| `received_utc` | datetime | — | När gatewayen tog emot meddelandet (FromApp), UTC — inte när raden skrevs |

---

En fråga sparas först (en rad per ben) och instrumentet registreras och prenumereras efteråt; samtidigt markeras
instrumentet som prenumererat i MarketDataService, så att dess 35=W behålls från frågan och framåt. Frågan hanteras på en
egen tråd (inte tråd-poolen). Loggen skriver per fråga hur lång tid sparandet och prenumerationen tog.
Varje minut stämmer `QuoteRequestResubscribeService` av: instrumenten för de senaste 24 timmarnas frågor (äldre än 2
min) som inte är `is_subscribed` i `market_instruments` registreras och markeras igen (misslyckat vid frågan, eller
omstart sedan). Ett misslyckat försök loggas och görs om nästa minut.

## `fxvol.session_heartbeat`

En rad per session (`GatewayHeartbeatRepository`): Blottern och OptionSuites pricer larmar på den.

| Kolumn | Typ | Beskrivning |
|--------|-----|-------------|
| `session_key` | varchar PK | Sessionen (t.ex. `VOLB_FIXHUB_PROD`) |
| `status` | varchar | `ONLINE` (var 2:a sekund medan inloggad) eller `OFFLINE` (vid utloggning) |
| `beat_utc` | datetime(3) | Senaste heartbeat, databasens klocka |
| `logon_utc` | datetime(3) | Senaste inloggning, gatewayens klocka (samma som `quote_requests.received_utc`); skript `20261026_session_logon.sql` i OptionSuite.V2 |

Volbroker sparar inga RFQ:er över en utloggning (Keith, Volbroker, 26 okt): en fråga som kom före sessionens senaste
inloggning finns inte kvar där, och ett pris på den avvisas (`VolbrokerReconnect`; en fråga som skickats om med samma
QuoteReqID räknas från sin senaste ankomst; minst en sekund före inloggningen, eftersom `received_utc` kan vara i hela
sekunder). Gatewayen skickar ingen Quote eller Quote Cancel på en sådan fråga, och våra levande versioner på den blir
`EXPIRED` ("Volbroker dropped the request when we logged on again at …") med händelsen DROPPED i `volbroker_rfq_event`.
Frågan själv lämnas: pricern visar den med Send avstängd och förklaringen. Inloggningstiden hålls också i minnet
(`ISessionLogonTimes`, från `GatewayHeartbeatService.SessionOnline`).

## Dataflöde (Volbroker)

35=y (SecurityList)
→ market_instruments (upsert)
→ is_subscribed = TRUE efter 35=V skickad

35=W (MarketDataSnapshot)
→ market_data_snapshots (INSERT, returnerar id)
→ market_data_entries (INSERT per MDEntry)
→ active_market_book (UPSERT per Bid/Ask)
→ market_trades (INSERT om MDEntryType=2)
→ om 268=0: active_market_book soft-delete (is_active=0)

35=R (QuoteRequest)
→ market_instruments (upsert, product=20)
→ quote_requests (INSERT per leg)


---

## Repository-filer

| Repository | Fil |
|------------|-----|
| `MarketDataSnapshotRepository` | `Infrastructure/Persistence/MarketDataSnapshotRepository.cs` |
| `MarketInstrumentRepository` | `Infrastructure/Persistence/MarketInstrumentRepository.cs` |
| `QuoteRequestRepository` | `Infrastructure/Persistence/QuoteRequestRepository.cs` |

Affärslogik: `Application/Services/MarketDataService.cs`, `Application/Services/QuoteRequestService.cs`

---

## Notering: FXOHUB-integration

`market_instruments` och `active_market_book` använder **Volbrokers `security_id` (tag 48)** som nyckel. TPICAP FXOHUB har inget motsvarande opakt ID — instrument identifieras via `(Symbol, SecurityType, SecurityExchange, TenorValue, OptionStrategy)`. 

FXOHUB-data bör därför lagras i **separata tabeller** (t.ex. `fxvol.fxohub_market_book`) med en sammansatt naturlig nyckel, utan att röra befintliga tabeller som konsumeras av externa appar.
---

## FXOhub-RFQ (TP ICAP) — `fxohub_rfq*`

Skript: `docs/sql/20261018_fxohub_rfq.sql` (och `20261018_fxohub_rfq_legid.sql`: LegID är en GUID från FXOhub, 36
tecken, kolumnerna `leg_id` är VARCHAR(64)). Skrivs av `FxoRfqService` / `FxoRfqRepository`; läses av pricerns
Incoming RFQ-blotter. Egna tabeller enligt noteringen ovan: FXOhub har inget `security_id`, en fråga identifieras med
`(session_key, quote_req_id)`.

| Tabell | Nyckel | Innehåll |
|--------|--------|----------|
| `fxohub_rfq` | `(session_key, quote_req_id)` unik | Frågan: status (`INCOMING`, `CANCELLED`; från pricern `PICKED_UP`, `PASSED`; senare `PRICED`, `TRADED` …), vem som tagit den (`picked_up_by`, `picked_up_utc`, skript `20261018_fxohub_rfq_pickup.sql`), BrokerID (7933), TraderID (9536), hedge (9015/9016), premiedag (5475), par (55), strategi (9126, kod och namn), sida (54), tider (60/62/126), NetDelta/NetVega, egen fråga (NoPartyIDs), senaste djup (`has_market`, `last_traded_price`), rått meddelande |
| `fxohub_rfq_leg` | `rfq_id` → `fxohub_rfq.id` | Ett ben per rad: LegID (7940), mängd i miljoner (687), expiry (611), strike (612) eller delta (9075), LegSide (624), löptid (6215, kod och namn), call/put (6713), cut (9125), leverans (743), ATMType (6005), valutor (556, 9074, 9034, 120) |
| `fxohub_rfq_quote` | `(session_key, quote_req_id)` | Aktuellt djup: ett pris per rad i ordning bäst → sämst, strategins vol bid/offer (6001/6002), OwnQuote (5331), OwnQuoteRequestor (6012). Ersätts helt av varje djup-meddelande |
| `fxohub_rfq_quote_entry` | `quote_row_id` → `fxohub_rfq_quote.id` | Benpriser (vol) per pris: typ (269), LegID, pris (270), storlek, MDPxRelatedTo (10502) |
| `fxohub_rfq_event` | — | En rad per meddelande: typ (`INCOMING`, `CANCELLED`, `CANCELLED_UNKNOWN`, `DEPTH`, `DEPTH_EXECUTION`, `INVALID`, `QUOTE_SENT`, `QUOTE_CANCEL_SENT`, `QUOTE_STATUS`, `BUSINESS_REJECT`), detalj och rått meddelande |
| `fxohub_rfq_price` | `(rfq_id, version)` unik; `rfq_id` → `fxohub_rfq.id` | Skript `20261018_fxohub_rfq_price.sql`. En prisversion: status (`QUEUED` → `SENDING` → `SENT` → `ACCEPTED`; `REJECTED`, `WITHDRAW` → `WITHDRAWN`, `REPLACED`, `EXPIRED`, `DONE`), trader (TraderID 9536), tidsstipulation (`valid_seconds` 120/600/3600, `valid_until_utc` = ValidUntilTime 62; båda NULL = GTC, ingen 62 skickas; skript `20261021_fxohub_rfq_price_gtc.sql`), spotstipulation (`spot_ref` = pricerns spot, `spot_pips`, intervallet; skickas inte, FXOhub sätter egen spot ref), delta hedge, vol-pris och premie som pricern visade dem, QuoteID (117), skickad / kvitterad / Quote Cancel skickad, orsak. Skriptet lägger också `traded_*`, `drop_copy_utc` och `stp_message_in_id` på `fxohub_rfq` |
| `fxohub_rfq_price_leg` | `(price_id, leg_no)` | Benets vol bid/offer (5678/9159, det enda som skickas per ben), pricerns strike och delta (sparas, skickas inte) |

### Dataflöde (FXOhub RFQ-session)

35=R med 9015/5475 (FXOhubs layout)
→ fxohub_rfq (upsert på session + QuoteReqID; status behålls vid omsändning)
→ fxohub_rfq_leg (ersätts)
→ fxohub_rfq_event (INCOMING)

35=AG (QuoteRequestReject)
→ fxohub_rfq.status = CANCELLED med orsak (658 + 58)
→ fxohub_rfq_event (CANCELLED, eller CANCELLED_UNKNOWN om frågan inte finns)

35=W med 131 (RFQ-djup, inte orderbok)
→ fxohub_rfq_quote + fxohub_rfq_quote_entry (ersätts; tomt vid "No Market Data", 6003=1)
→ fxohub_rfq.has_market / last_traded_price / depth_updated_utc

### Våra priser (FxoQuoteService, var 500 ms)

Pricern skriver en version (`QUEUED`) för en fråga dess trader har tagit (villkorat: bara `PICKED_UP` av traden).
→ tidsstipulationen passerad: aldrig skickad `EXPIRED`; skickad/accepterad `WITHDRAW` med orsak "time ran out" och Quote
  Cancel 10127=1 (TIMEOUT), eftersom FXOhub inte avslutar ett pris vid ValidUntilTime (UAT 21 okt); frågan inte längre
  traderns: `REJECTED`
→ sessionen inloggad: `QUEUED` → `SENDING` (villkorad UPDATE) → Quote 35=S (benen som grupp 555 i specens ordning,
  QuoteID `OS{id}-{version}`) → `SENT` + fxohub_rfq_event QUOTE_SENT; inte inloggad: ligger kvar `QUEUED`
→ pricern ber om `WITHDRAW` (ej skickad: `WITHDRAWN` direkt) → Quote Cancel 35=Z (298=1, 10127=2 när spot lämnat
  intervallet) → `cancel_sent_utc`

35=AI (Quote Status Report, på QuoteID)
→ 0: `ACCEPTED` (+ `ack_utc`; äldre levande versioner av frågan `REPLACED`), 3: `WITHDRAWN` (`EXPIRED` efter vår
  timeout-cancel), 5/9: `REJECTED` (9 efter timeout-cancel: `EXPIRED`),
  7: `EXPIRED`, 10: ingen ändring → fxohub_rfq_event QUOTE_STATUS

35=j med 372 = S/Z (Business Message Reject) → versionen med QuoteID 379 `REJECTED` → BUSINESS_REJECT

Spotstipulationen finns inte i FIX-meddelandet: pricern drar priset när live spot lämnar intervallet. Avslut
(`traded_*` på `fxohub_rfq`) och kopplingen till mäklarens drop copy (`stp_message_in_id`) skrivs inte än: väntar på
FXOhubs svar om hur en match syns och vilket id som kopplar fråga, pris och trade capture.
→ fxohub_rfq_event (DEPTH, eller DEPTH_EXECUTION när MDSource 5177 = 0)

RFQ-sessionen måste använda `FIX44_FXOhub.xml` som data dictionary, så att grupperna (146/555/453 och 296/268) tolkas.

---

## Bloomberg FXGO Options-RFQ — `bloomberg_rfq*`

Skript: `docs/sql/20261022_bloomberg_rfq.sql`. Skrivs av `BloombergRfqService` / `BloombergRfqRepository`; ska läsas av
pricerns Incoming RFQ-blotter senare. Specifikation: Bloombergs FX FIXBook for Options RFS 4.1.5 (FIX 4.4, juli 2026,
märkt DRAFT). En fråga identifieras med `(session_key, quote_req_id)`, som FXOhubs.

Sessionen: `fix_connection_config.VenueCode = 'BLOOMBERG'` (då går alla dess meddelanden till Bloomberg-tjänsten före de
andra venues fall i `QuickFixApplication.FromApp`; Bloombergs 35=R har 9015 och hade annars tagits av FXOhub-kontrollen),
`DataDictionaryFile = 'FIX44_Bloomberg.xml'` (grupperna 146, 453/802, 555/670/756, 78; byggd av
`docs/dictionaries/build_fix44_bloomberg.py` ur QuickFIXn:s FIX44.xml) och `ResetSeqNumOnLogon = 0` (se nedan).

| Tabell | Nyckel | Innehåll |
|--------|--------|----------|
| `bloomberg_rfq` | `(session_key, quote_req_id)` unik | Frågan: status (`INCOMING`, `CANCELLED`; senare från pricern), header 50/116/128, MarketSegmentID (1300), instrumentet (55/65/48/22 i 146), konto (1), InCompetition (6702), handelsdag (75), sida (54), strategi (9126, kod och namn), parterna (venue: MIC, roll, LEI; taker: deal code, firma, UUID, trader, LEI; maker: deal code, firma, LEI), spot (5235), premie (5830, 5020, 22195), hedgen (9015, 9016, 6666, 9679, 9123, 9657, 9112, 541, 22514, 22656, 22292, 22898), avveckling (63, 120, 9102), QuotePriceType (692), solve-for (22720, 22723), UPI (2891), antal allokeringar (78), värden som inte gick att läsa (`parse_problems`), vår kvittens (`ack_quote_id`, `ack_sent_utc`) och rått meddelande |
| `bloomberg_rfq_leg` | `rfq_id` → `bloomberg_rfq.id` | Ett ben per rad: LegRefID (654), ISIN (602), UPI (2893), valuta (556), produkt (22410), cut (9124, Bloombergs kod: NYK = NY 10:00, NYC = NY 16:59), expiry (432), leverans (743), call/put (9034, 9072), delta (9075), strike (612, saknas vid solve-for), belopp (9018), stil (9019), Position (6351), forward (9099), räntor (9115/9118), barriär (9130–9132, 9137, 9187), hedge per ben (22562–22968) och antal allokeringar (670) |
| `bloomberg_rfq_event` | — | En rad per meddelande: typ (`INCOMING`, `ACK_SENT`, `ACK_FAILED`, `CANCELLED`, `CANCELLED_UNKNOWN`, `INVALID`, `UNHANDLED`), detalj och rått meddelande |

### Dataflöde (steg 1: ta emot och kvittera)

35=R (QuoteRequest)
→ bloomberg_rfq (upsert på session + QuoteReqID; status och kvittens behålls vid omsändning) + bloomberg_rfq_leg (ersätts)
→ bloomberg_rfq_event (INCOMING)
→ 35=AI (QuoteStatusReport, 131 + 117 `BBR{id}` + 297=0 "accepted for pricing") när frågan sparats, inte är `CANCELLED`
  och inte redan kvitterats → `ack_quote_id` / `ack_sent_utc` + event ACK_SENT (ACK_FAILED om den inte gick att skicka).
  En fråga som inte gick att spara kvitteras inte.

35=Z (QuoteCancel) med 298=2 (Bloombergs: kunden avbröt eller avböjde)
→ bloomberg_rfq.status = CANCELLED med orsak ("Cancelled by Bloomberg" + 58) → event CANCELLED (CANCELLED_UNKNOWN om
  frågan inte finns). Annan 298: event INVALID, status oförändrad.

Övriga (35=D New Order Single, 35=BN …): event UNHANDLED och ett fel i loggen; inget skickas tillbaka. Prissättning
(35=S), pass (35=AG) och avslut (35=D → 35=8) kommer i senare steg.

---

## `fix_config_dev.fix_connection_config.ResetSeqNumOnLogon`

Skript: `docs/sql/20261022_fix_connection_reset_seqnum.sql` (kör det innan en gateway med kolumnen startas: sessionerna
läses med den). `TINYINT(1) NOT NULL DEFAULT 1`: 1 = sekvensnumren nollställs vid inloggning, utloggning och nedkoppling
(141=Y i vår Logon), som alla sessioner hittills; 0 = de behålls, så en lucka skickas om från filbutiken (QuickFIX
nollställer ändå vid en ny sessionsperiod, StartTime). Sätts bara i databasen (`SessionRepository.SaveAsync` skriver den
inte, som SSL-tunnelns kolumner).
