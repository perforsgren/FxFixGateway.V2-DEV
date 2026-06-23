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
| `quote_requests` | Inkommande RFQ-meddelanden (35=R) |

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