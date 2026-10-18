-- FXOhub RFQs (TP ICAP) in fxvol: requests, legs, current depth per request and an event log.
-- Written by FxFixGateway (FxoRfqService / FxoRfqRepository); read by the pricer's Incoming RFQ blotter.
-- Separate from the Volbroker tables (quote_requests, market_*), which other applications read.

CREATE TABLE IF NOT EXISTS fxvol.fxohub_rfq (
    id                  BIGINT        NOT NULL AUTO_INCREMENT,
    session_key         VARCHAR(64)   NOT NULL,
    quote_req_id        VARCHAR(64)   NOT NULL,            -- 131
    status              VARCHAR(20)   NOT NULL,            -- INCOMING, CANCELLED (later: PICKED_UP, PRICED, TRADED …)
    status_reason       VARCHAR(255)  NULL,
    status_utc          DATETIME(3)   NOT NULL,
    broker_id           VARCHAR(64)   NULL,                -- 7933
    trader_id           VARCHAR(32)   NULL,                -- 9536
    trade_date          VARCHAR(16)   NULL,                -- 75, as sent
    exec_delta_hedge    VARCHAR(4)    NULL,                -- 9015: 0 = no hedge, 1 = hedge
    hedge_trade_type    VARCHAR(4)    NULL,                -- 9016: 0 = none, 1 = spot, 2 = forward
    premium_del         VARCHAR(4)    NULL,                -- 5475: S = spot, F = forward
    symbol              VARCHAR(32)   NULL,                -- 55
    security_type       VARCHAR(16)   NULL,                -- 167
    option_strategy     VARCHAR(4)    NULL,                -- 9126, raw code
    strategy_name       VARCHAR(32)   NULL,                -- 9126, readable
    quote_request_type  VARCHAR(4)    NULL,                -- 303: 1 = manual, 2 = automatic
    quote_type          VARCHAR(4)    NULL,                -- 537: 1 = tradeable
    side                VARCHAR(4)    NULL,                -- 54: 0 = two-way, 1 = buy, 2 = sell
    transact_time_utc   DATETIME(3)   NULL,                -- 60
    valid_until_utc     DATETIME(3)   NULL,                -- 62
    expire_time_utc     DATETIME(3)   NULL,                -- 126
    net_delta           VARCHAR(8)    NULL,                -- 9083
    net_vega            VARCHAR(8)    NULL,                -- 9086
    is_own_request      TINYINT(1)    NOT NULL DEFAULT 0,  -- NoPartyIDs present: our own firm's request
    text                VARCHAR(255)  NULL,                -- 58
    has_market          TINYINT(1)    NOT NULL DEFAULT 0,  -- from the latest depth
    last_traded_price   DECIMAL(18,6) NULL,                -- 6004, from the depth
    depth_updated_utc   DATETIME(3)   NULL,
    raw_payload         LONGTEXT      NOT NULL,
    received_utc        DATETIME(3)   NOT NULL,
    updated_utc         DATETIME(3)   NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_fxohub_rfq (session_key, quote_req_id),
    KEY ix_fxohub_rfq_status (status, received_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS fxvol.fxohub_rfq_leg (
    id                  BIGINT        NOT NULL AUTO_INCREMENT,
    rfq_id              BIGINT        NOT NULL,
    leg_no              INT           NOT NULL,            -- order in the message
    leg_id              VARCHAR(64)   NULL,                -- 7940: a GUID from FXOhub (script 20261018_fxohub_rfq_legid.sql)
    leg_symbol          VARCHAR(32)   NULL,                -- 600
    leg_qty_millions    DECIMAL(18,6) NULL,                -- 687, in millions
    maturity_date       DATE          NULL,                -- 611
    strike_price        DECIMAL(18,8) NULL,                -- 612
    leg_side            VARCHAR(4)    NULL,                -- 624: B = as defined, C = opposite
    tenor_code          VARCHAR(8)    NULL,                -- 6215, raw code
    tenor               VARCHAR(8)    NULL,                -- 6215 as 1W, 1M … (null for an unknown code)
    settl_date          DATE          NULL,                -- 588
    leg_type            VARCHAR(4)    NULL,                -- 6713: 1 = call, 2 = put
    option_style        VARCHAR(4)    NULL,                -- 9019: 2 = European
    cutoff              VARCHAR(32)   NULL,                -- 9125
    pit_type            VARCHAR(4)    NULL,                -- 6007
    delivery_date       DATE          NULL,                -- 743
    leg_delta           DECIMAL(10,4) NULL,                -- 9075, 1–100
    atm_type            VARCHAR(4)    NULL,                -- 6005: 0 = no ATM, 1 = ATM, 2 = ATMF
    leg_currency        VARCHAR(3)    NULL,                -- 556
    currency2           VARCHAR(3)    NULL,                -- 9074
    call_put_currency   VARCHAR(3)    NULL,                -- 9034
    settl_currency      VARCHAR(3)    NULL,                -- 120, premium currency
    PRIMARY KEY (id),
    KEY ix_fxohub_rfq_leg_rfq (rfq_id),
    CONSTRAINT fk_fxohub_rfq_leg_rfq FOREIGN KEY (rfq_id) REFERENCES fxvol.fxohub_rfq (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- The current depth of each request (replaced by every RFQ market depth message, 35=W).
CREATE TABLE IF NOT EXISTS fxvol.fxohub_rfq_quote (
    id                  BIGINT        NOT NULL AUTO_INCREMENT,
    session_key         VARCHAR(64)   NOT NULL,
    quote_req_id        VARCHAR(64)   NOT NULL,
    position            INT           NOT NULL,            -- order in the message, 1 = best
    quote_set_id        VARCHAR(64)   NULL,                -- 302
    quote_id            VARCHAR(64)   NULL,                -- 117
    bid_strategy_vol    DECIMAL(18,6) NULL,                -- 6001
    offer_strategy_vol  DECIMAL(18,6) NULL,                -- 6002
    own_quote           TINYINT(1)    NOT NULL DEFAULT 0,  -- 5331 = Y
    own_quote_requestor TINYINT(1)    NOT NULL DEFAULT 0,  -- 6012 = Y
    received_utc        DATETIME(3)   NOT NULL,
    PRIMARY KEY (id),
    KEY ix_fxohub_rfq_quote_req (session_key, quote_req_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS fxvol.fxohub_rfq_quote_entry (
    id                  BIGINT        NOT NULL AUTO_INCREMENT,
    quote_row_id        BIGINT        NOT NULL,
    md_entry_type       VARCHAR(4)    NULL,                -- 269: 0 = bid, 1 = offer
    leg_id              VARCHAR(64)   NULL,                -- 7940
    price               DECIMAL(18,6) NULL,                -- 270, leg vol
    size                DECIMAL(18,6) NULL,                -- 271
    position_no         INT           NULL,                -- 290
    px_related_to       VARCHAR(8)    NULL,                -- 10502: 6001 / 6002
    leg_side            VARCHAR(4)    NULL,                -- 624
    leg_type            VARCHAR(4)    NULL,                -- 6713
    text                VARCHAR(255)  NULL,                -- 58
    PRIMARY KEY (id),
    KEY ix_fxohub_rfq_quote_entry_row (quote_row_id),
    CONSTRAINT fk_fxohub_rfq_quote_entry_row FOREIGN KEY (quote_row_id) REFERENCES fxvol.fxohub_rfq_quote (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Every RFQ message as an event (audit trail, hit ratio).
CREATE TABLE IF NOT EXISTS fxvol.fxohub_rfq_event (
    id                  BIGINT        NOT NULL AUTO_INCREMENT,
    session_key         VARCHAR(64)   NOT NULL,
    quote_req_id        VARCHAR(64)   NULL,
    msg_type            VARCHAR(4)    NOT NULL,            -- R, AG, W (later S, Z, AI)
    event_type          VARCHAR(32)   NOT NULL,            -- INCOMING, CANCELLED, CANCELLED_UNKNOWN, DEPTH, DEPTH_EXECUTION, INVALID
    detail              VARCHAR(512)  NULL,
    raw_payload         LONGTEXT      NOT NULL,
    received_utc        DATETIME(3)   NOT NULL,
    PRIMARY KEY (id),
    KEY ix_fxohub_rfq_event_req (session_key, quote_req_id),
    KEY ix_fxohub_rfq_event_time (received_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
