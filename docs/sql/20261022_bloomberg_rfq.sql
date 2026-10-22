-- Bloomberg FXGO Options RFQs in fxvol (FX FIXBook for Options RFS 4.1.5): requests, legs and an event log.
-- Written by FxFixGateway (BloombergRfqService / BloombergRfqRepository); read by the pricer's Incoming RFQ blotter later.
-- Character set as the other RFQ tables (fxohub_rfq*, volbroker_rfq*): utf8mb4 with the server's default collation.

CREATE TABLE IF NOT EXISTS fxvol.bloomberg_rfq (
    id                    BIGINT        NOT NULL AUTO_INCREMENT,
    session_key           VARCHAR(64)   NOT NULL,
    quote_req_id          VARCHAR(64)   NOT NULL,            -- 131
    status                VARCHAR(20)   NOT NULL,            -- INCOMING, CANCELLED (later from the pricer: PICKED_UP, PASSED …)
    status_reason         VARCHAR(255)  NULL,
    status_utc            DATETIME(3)   NOT NULL,
    sender_sub_id         VARCHAR(64)   NULL,                -- 50 (header): Bloomberg identifier of the client (FXPV)
    on_behalf_of_sub_id   VARCHAR(64)   NULL,                -- 116 (header): taker side firm, Bloomberg deal code
    deliver_to_comp_id    VARCHAR(64)   NULL,                -- 128 (header): maker side firm, Bloomberg deal code
    market_segment_id     VARCHAR(8)    NULL,                -- 1300: XOFF, BSEF, BMTF, BTFE, BTBS
    quote_type            VARCHAR(4)    NULL,                -- 537: 1 = tradable
    symbol                VARCHAR(16)   NULL,                -- 55 (in 146): CCY1CCY2
    symbol_sfx            VARCHAR(8)    NULL,                -- 65: FX
    security_id           VARCHAR(32)   NULL,                -- 48: CCY1CCY2
    security_id_source    VARCHAR(4)    NULL,                -- 22: 6 = CCY
    account               VARCHAR(64)   NULL,                -- 1: Bloomberg identifier of the client account (FXPV)
    in_competition        VARCHAR(4)    NULL,                -- 6702: Y = several LPs, N = only us
    text                  VARCHAR(255)  NULL,                -- 58
    trade_date            DATE          NULL,                -- 75 (rolls at 17:00 New York)
    side                  VARCHAR(4)    NULL,                -- 54: 0 = two-way, 1 = buy, 2 = sell (CCY1, taker's view)
    option_strategy       VARCHAR(4)    NULL,                -- 9126, raw code
    strategy_name         VARCHAR(40)   NULL,                -- 9126 as the FIXBook names it (section 4.0)
    venue_mic             VARCHAR(8)    NULL,                -- 453 entry with 447 = G: 448 = XOFF, BBSF, BMTF, BTFE
    venue_role            VARCHAR(4)    NULL,                -- its 452: 16 off facility, 64 MTF, 73 executing venue (SEF)
    venue_lei             VARCHAR(20)   NULL,                -- its 523 with 803 = 4025
    taker_deal_code       VARCHAR(16)   NULL,                -- 453 entry with 452 = 13: 448
    taker_firm            VARCHAR(128)  NULL,                --   523 with 803 = 1
    taker_uuid            VARCHAR(32)   NULL,                --   523 with 803 = 2
    taker_trader          VARCHAR(128)  NULL,                --   523 with 803 = 9
    taker_lei             VARCHAR(20)   NULL,                --   523 with 803 = 4025
    maker_deal_code       VARCHAR(16)   NULL,                -- 453 entry with 452 = 1: 448
    maker_firm            VARCHAR(128)  NULL,                --   523 with 803 = 1
    maker_lei             VARCHAR(20)   NULL,                --   523 with 803 = 4025
    spot_rate             DECIMAL(18,8) NULL,                -- 5235
    premium_currency      VARCHAR(3)    NULL,                -- 5830
    premium_delivery_date DATE          NULL,                -- 5020
    premium_delivery_type VARCHAR(4)    NULL,                -- 22195: 1 spot, 2 forward, 3 custom
    exec_delta_hedge      VARCHAR(4)    NULL,                -- 9015: Y / N
    hedge_trade_type      VARCHAR(4)    NULL,                -- 9016: 1 spot, 2 forward, 3 custom
    hedge_direction       VARCHAR(4)    NULL,                -- 6666: 0 two-way, 1 buy, 2 sell (CCY1, taker's view)
    hedge_side            VARCHAR(4)    NULL,                -- 9679: 1 same, 2 opposite
    hedge_amount          DECIMAL(24,6) NULL,                -- 9123, in CCY1
    hedge_rate            DECIMAL(18,8) NULL,                -- 9657
    hedge_date            DATE          NULL,                -- 9112
    hedge_fixing_date     DATE          NULL,                -- 541 (NDF)
    hedge_settl_method    VARCHAR(4)    NULL,                -- 22514: P / C
    hedge_settl_currency  VARCHAR(3)    NULL,                -- 22656
    hedge_security_id     VARCHAR(32)   NULL,                -- 22292: ISIN
    hedge_upi_code        VARCHAR(32)   NULL,                -- 22898
    settl_type            VARCHAR(4)    NULL,                -- 63: 0 physical, 1 cash
    settl_currency        VARCHAR(3)    NULL,                -- 120
    market_type           VARCHAR(4)    NULL,                -- 9102: R regular, N onshore, O offshore (NDF)
    quote_price_type      VARCHAR(4)    NULL,                -- 692: 1 percentage, 2 per unit (pips)
    solve_for_type        VARCHAR(4)    NULL,                -- 22720: 14 strike zero premium, 15 barrier zero premium
    solve_for_leg_ref_id  VARCHAR(64)   NULL,                -- 22723: the leg (654) solved for
    upi_code              VARCHAR(32)   NULL,                -- 2891
    allocation_count      INT           NOT NULL DEFAULT 0,  -- NoAllocs (78); the allocations stay in raw_payload
    parse_problems        TEXT          NULL,                -- values that are not the FIXBook's type, one per line; not stored
    ack_quote_id          VARCHAR(64)   NULL,                -- QuoteID (117) of our acknowledgement (35=AI 297=0)
    ack_sent_utc          DATETIME(3)   NULL,
    raw_payload           LONGTEXT      NOT NULL,
    received_utc          DATETIME(3)   NOT NULL,
    updated_utc           DATETIME(3)   NOT NULL,
    PRIMARY KEY (id),
    UNIQUE KEY uq_bloomberg_rfq (session_key, quote_req_id),
    KEY ix_bloomberg_rfq_status (status, received_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS fxvol.bloomberg_rfq_leg (
    id                       BIGINT        NOT NULL AUTO_INCREMENT,
    rfq_id                   BIGINT        NOT NULL,
    leg_no                   INT           NOT NULL,         -- order in the message
    leg_ref_id               VARCHAR(64)   NULL,             -- 654
    leg_security_id          VARCHAR(32)   NULL,             -- 602: ISIN
    leg_upi_code             VARCHAR(32)   NULL,             -- 2893
    leg_currency             VARCHAR(3)    NULL,             -- 556: notional currency
    leg_isin_product         VARCHAR(32)   NULL,             -- 22410: Vanilla_Option, Barrier_Option, Digital_Option, NDO
    cut_off                  VARCHAR(8)    NULL,             -- 9124: Bloomberg's code (NYK = NY 10:00, NYC = NY 16:59 …)
    expiry_date              DATE          NULL,             -- 432
    delivery_date            DATE          NULL,             -- 743
    call_put_currency        VARCHAR(3)    NULL,             -- 9034
    call_or_put              VARCHAR(4)    NULL,             -- 9072: 1 call, 2 put
    delta                    DECIMAL(12,6) NULL,             -- 9075, in percent
    strike_price             DECIMAL(18,8) NULL,             -- 612 (absent when solved for)
    notional_amount          DECIMAL(24,6) NULL,             -- 9018
    option_style             VARCHAR(4)    NULL,             -- 9019: 1 American, 2 European
    position                 VARCHAR(4)    NULL,             -- 6351: 0 same as the strategy, 1 opposite
    forward_rate             DECIMAL(18,8) NULL,             -- 9099 (forward hedge)
    ccy1_deposit_rate        DECIMAL(12,8) NULL,             -- 9115
    ccy2_deposit_rate        DECIMAL(12,8) NULL,             -- 9118
    barrier_style            VARCHAR(4)    NULL,             -- 9130: 1 in, 2 out
    barrier_level            DECIMAL(18,8) NULL,             -- 9131
    barrier_level2           DECIMAL(18,8) NULL,             -- 9137
    barrier_direction        VARCHAR(4)    NULL,             -- 9132: 1 up, 2 down
    touch_type               VARCHAR(4)    NULL,             -- 9187: 1 pay when hit, 2 pay at expiry
    leg_hedge_amount         DECIMAL(24,6) NULL,             -- 22562 (multi-hedge)
    leg_hedge_currency       VARCHAR(3)    NULL,             -- 22563
    leg_exec_delta_hedge     VARCHAR(4)    NULL,             -- 22677
    leg_delta_hedge_side     VARCHAR(4)    NULL,             -- 22678: 1 buy, 2 sell, B as defined, C opposite
    leg_hedge_rate           DECIMAL(18,8) NULL,             -- 22679
    leg_hedge_trade_type     VARCHAR(4)    NULL,             -- 22680: 1 spot, 2 forward, 3 custom
    leg_hedge_date           DATE          NULL,             -- 22681
    leg_hedge_fixing_date    DATE          NULL,             -- 22861
    leg_hedge_settl_method   VARCHAR(4)    NULL,             -- 22950
    leg_hedge_settl_currency VARCHAR(3)    NULL,             -- 22968
    allocation_count         INT           NOT NULL DEFAULT 0, -- NoLegAllocs (670); the allocations stay in raw_payload
    PRIMARY KEY (id),
    KEY ix_bloomberg_rfq_leg_rfq (rfq_id),
    CONSTRAINT fk_bloomberg_rfq_leg_rfq FOREIGN KEY (rfq_id) REFERENCES fxvol.bloomberg_rfq (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Every message on a Bloomberg session as an event: theirs (R, Z, D …) and ours (AI).
CREATE TABLE IF NOT EXISTS fxvol.bloomberg_rfq_event (
    id                  BIGINT        NOT NULL AUTO_INCREMENT,
    session_key         VARCHAR(64)   NOT NULL,
    quote_req_id        VARCHAR(64)   NULL,
    msg_type            VARCHAR(4)    NOT NULL,              -- R, Z, AI, D, BN …
    event_type          VARCHAR(32)   NOT NULL,              -- INCOMING, ACK_SENT, ACK_FAILED, CANCELLED, CANCELLED_UNKNOWN, INVALID, UNHANDLED
    detail              VARCHAR(1024) NULL,
    raw_payload         LONGTEXT      NOT NULL,
    received_utc        DATETIME(3)   NOT NULL,
    PRIMARY KEY (id),
    KEY ix_bloomberg_rfq_event_req (session_key, quote_req_id),
    KEY ix_bloomberg_rfq_event_time (received_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Check: the three tables, with the same collation as fxohub_rfq and volbroker_rfq_event.
SELECT TABLE_NAME, TABLE_COLLATION
FROM information_schema.TABLES
WHERE TABLE_SCHEMA = 'fxvol'
  AND TABLE_NAME IN ('bloomberg_rfq', 'bloomberg_rfq_leg', 'bloomberg_rfq_event', 'fxohub_rfq', 'volbroker_rfq_event')
ORDER BY TABLE_NAME;
