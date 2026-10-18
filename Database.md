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
| `fxohub_rfq` | FXOhub-RFQ:er (35=R), en rad per fråga med status |
| `fxohub_rfq_leg` | Benen i en FXOhub-RFQ |
| `fxohub_rfq_quote` | Aktuellt djup per FXOhub-RFQ (35=W i RFQ-sessionen), en rad per pris |
| `fxohub_rfq_quote_entry` | Benpriser per pris i djupet |
| `fxohub_rfq_event` | Varje FXOhub-RFQ-meddelande som händelse (revision, hit ratio) |
| `fxohub_rfq_price` | Våra priser på FXOhub-RFQ:er, en rad per version (skrivs av pricern, skickas av gatewayen) |
| `fxohub_rfq_price_leg` | Benen i en prisversion (vol bid/offer, strike, delta) |

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
| `received_utc` | datetime | — | Mottagingstidpunkt |

---

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

Skript: `docs/sql/20261018_fxohub_rfq.sql`. Skrivs av `FxoRfqService` / `FxoRfqRepository`; läses av pricerns
Incoming RFQ-blotter. Egna tabeller enligt noteringen ovan: FXOhub har inget `security_id`, en fråga identifieras med
`(session_key, quote_req_id)`.

| Tabell | Nyckel | Innehåll |
|--------|--------|----------|
| `fxohub_rfq` | `(session_key, quote_req_id)` unik | Frågan: status (`INCOMING`, `CANCELLED`; från pricern `PICKED_UP`, `PASSED`; senare `PRICED`, `TRADED` …), vem som tagit den (`picked_up_by`, `picked_up_utc`, skript `20261018_fxohub_rfq_pickup.sql`), BrokerID (7933), TraderID (9536), hedge (9015/9016), premiedag (5475), par (55), strategi (9126, kod och namn), sida (54), tider (60/62/126), NetDelta/NetVega, egen fråga (NoPartyIDs), senaste djup (`has_market`, `last_traded_price`), rått meddelande |
| `fxohub_rfq_leg` | `rfq_id` → `fxohub_rfq.id` | Ett ben per rad: LegID (7940), mängd i miljoner (687), expiry (611), strike (612) eller delta (9075), LegSide (624), löptid (6215, kod och namn), call/put (6713), cut (9125), leverans (743), ATMType (6005), valutor (556, 9074, 9034, 120) |
| `fxohub_rfq_quote` | `(session_key, quote_req_id)` | Aktuellt djup: ett pris per rad i ordning bäst → sämst, strategins vol bid/offer (6001/6002), OwnQuote (5331), OwnQuoteRequestor (6012). Ersätts helt av varje djup-meddelande |
| `fxohub_rfq_quote_entry` | `quote_row_id` → `fxohub_rfq_quote.id` | Benpriser (vol) per pris: typ (269), LegID, pris (270), storlek, MDPxRelatedTo (10502) |
| `fxohub_rfq_event` | — | En rad per meddelande: typ (`INCOMING`, `CANCELLED`, `CANCELLED_UNKNOWN`, `DEPTH`, `DEPTH_EXECUTION`, `INVALID`, `QUOTE_SENT`, `QUOTE_CANCEL_SENT`, `QUOTE_STATUS`, `BUSINESS_REJECT`), detalj och rått meddelande |
| `fxohub_rfq_price` | `(rfq_id, version)` unik; `rfq_id` → `fxohub_rfq.id` | Skript `20261018_fxohub_rfq_price.sql`. En prisversion: status (`QUEUED` → `SENDING` → `SENT` → `ACCEPTED`; `REJECTED`, `WITHDRAW` → `WITHDRAWN`, `REPLACED`, `EXPIRED`, `DONE`), trader (TraderID 9536), tidsstipulation (`valid_seconds`, `valid_until_utc` = ValidUntilTime 62), spotstipulation (`spot_ref` = LegSpotRate 5235, `spot_pips`, intervallet), delta hedge, vol-pris och premie som pricern visade dem, QuoteID (117), skickad / kvitterad / Quote Cancel skickad, orsak. Skriptet lägger också `traded_*`, `drop_copy_utc` och `stp_message_in_id` på `fxohub_rfq` |
| `fxohub_rfq_price_leg` | `(price_id, leg_no)` | Benets vol bid/offer (5678/9159), strike (6354), delta (9075) |

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
→ tidsstipulationen passerad: `EXPIRED`; frågan inte längre traderns: `REJECTED`
→ sessionen inloggad: `QUEUED` → `SENDING` (villkorad UPDATE) → Quote 35=S (benen som grupp 555 i specens ordning,
  QuoteID `OS{id}-{version}`) → `SENT` + fxohub_rfq_event QUOTE_SENT; inte inloggad: ligger kvar `QUEUED`
→ pricern ber om `WITHDRAW` (ej skickad: `WITHDRAWN` direkt) → Quote Cancel 35=Z (298=1, 10127=2 när spot lämnat
  intervallet) → `cancel_sent_utc`

35=AI (Quote Status Report, på QuoteID)
→ 0: `ACCEPTED` (+ `ack_utc`; äldre levande versioner av frågan `REPLACED`), 3: `WITHDRAWN`, 5/9: `REJECTED`,
  7: `EXPIRED`, 10: ingen ändring → fxohub_rfq_event QUOTE_STATUS

35=j med 372 = S/Z (Business Message Reject) → versionen med QuoteID 379 `REJECTED` → BUSINESS_REJECT

Spotstipulationen finns inte i FIX-meddelandet: pricern drar priset när live spot lämnar intervallet. Avslut
(`traded_*` på `fxohub_rfq`) och kopplingen till mäklarens drop copy (`stp_message_in_id`) skrivs inte än: väntar på
FXOhubs svar om hur en match syns och vilket id som kopplar fråga, pris och trade capture.
→ fxohub_rfq_event (DEPTH, eller DEPTH_EXECUTION när MDSource 5177 = 0)

RFQ-sessionen måste använda `FIX44_FXOhub.xml` som data dictionary, så att grupperna (146/555/453 och 296/268) tolkas.
