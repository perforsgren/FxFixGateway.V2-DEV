-- Our prices on FXOhub RFQs (TP ICAP), one row per version, and what came of them; and the trade on a request.
-- The pricer writes a version (status QUEUED) for a request its trader has picked up; FxFixGateway (FxoQuoteService)
-- sends it as a Quote (35=S) on the RFQ session and writes back what happens. The pricer asks to withdraw by setting
-- WITHDRAW; the gateway sends the Quote Cancel (35=Z).
--
-- Status of a version:
--   QUEUED    written by the pricer, not yet taken by the gateway (the pricer may withdraw it directly: WITHDRAWN)
--   SENDING   taken by the gateway (conditional update from QUEUED), Quote being sent
--   SENT      Quote sent (quote_id, sent_utc)
--   ACCEPTED  Quote Status Report (35=AI) QuoteStatus 0 (ack_utc)
--   REJECTED  AI QuoteStatus 5 / 9, a Business Message Reject, or the gateway could not send it (status_reason)
--   WITHDRAW  the pricer asks to cancel a sent version (withdraw_reason)
--   WITHDRAWN AI QuoteStatus 3 after the Quote Cancel (or withdrawn before it was sent)
--   REPLACED  a newer version of the same request was sent (FXOhub keeps one live quote per side and replaces it)
--   EXPIRED   AI QuoteStatus 7, or its valid_until_utc passed without a trade
--   DONE      traded on this version

CREATE TABLE IF NOT EXISTS fxvol.fxohub_rfq_price (
    id                  BIGINT        NOT NULL AUTO_INCREMENT,
    rfq_id              BIGINT        NOT NULL,            -- fxohub_rfq.id
    version             INT           NOT NULL,            -- 1, 2 … per request
    status              VARCHAR(16)   NOT NULL,
    status_reason       VARCHAR(255)  NULL,
    status_utc          DATETIME(3)   NOT NULL,
    trader              VARCHAR(32)   NOT NULL,            -- the pricer's user; TraderID (9536), first 15 characters
    created_utc         DATETIME(3)   NOT NULL,
    valid_seconds       INT           NULL,                -- the time stipulation chosen (30, 60, 120, 300); null for a clock time
    valid_until_utc     DATETIME(3)   NOT NULL,            -- ValidUntilTime (62)
    spot_ref            DECIMAL(18,8) NOT NULL,            -- LegSpotRate (5235); centre of the spot stipulation
    spot_pips           INT           NOT NULL,            -- spot tolerance in pips (not in the FIX message: the pricer withdraws)
    spot_low            DECIMAL(18,8) NOT NULL,
    spot_high           DECIMAL(18,8) NOT NULL,
    hedge_amount        DECIMAL(20,2) NULL,                -- the delta hedge exchanged with the option (base currency)
    hedge_currency      VARCHAR(3)    NULL,
    hedge_rate          DECIMAL(18,8) NULL,                -- (amount, currency and rate only when the hedge is one trade)
    hedge_side          CHAR(1)       NULL,                -- our side of the hedge if we sell the structure as quoted (B / S)
    hedge_text          VARCHAR(255)  NULL,                -- the hedge as shown, one line per trade
    vol_quote           VARCHAR(64)   NULL,                -- the structure's vol price as the pricer showed it, e.g. 7.850 / 8.250
    premium_text        VARCHAR(128)  NULL,                -- its premium two-way as shown
    premium_currency    VARCHAR(3)    NULL,
    premium_unit        VARCHAR(16)   NULL,                -- "% of USD" or "pips"
    premium_bid         DECIMAL(18,6) NULL,                -- the structure's price if we buy it as quoted (positive)
    premium_offer       DECIMAL(18,6) NULL,                -- … if we sell it
    premium_bid_amount  DECIMAL(20,2) NULL,                -- the cash for those, in premium_currency (positive)
    premium_offer_amount DECIMAL(20,2) NULL,
    quote_id            VARCHAR(64)   NULL,                -- QuoteID (117) sent
    sent_utc            DATETIME(3)   NULL,
    ack_utc             DATETIME(3)   NULL,
    withdraw_reason     VARCHAR(255)  NULL,
    cancel_sent_utc     DATETIME(3)   NULL,                -- when the gateway sent the Quote Cancel (35=Z) for a WITHDRAW
    closed_utc          DATETIME(3)   NULL,                -- when it stopped being live
    PRIMARY KEY (id),
    UNIQUE KEY uq_fxohub_rfq_price (rfq_id, version),
    KEY ix_fxohub_rfq_price_status (status),
    CONSTRAINT fk_fxohub_rfq_price_rfq FOREIGN KEY (rfq_id) REFERENCES fxvol.fxohub_rfq (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- The legs of a version (one per request leg, same leg_no as fxohub_rfq_leg).
CREATE TABLE IF NOT EXISTS fxvol.fxohub_rfq_price_leg (
    price_id            BIGINT        NOT NULL,
    leg_no              INT           NOT NULL,
    bid_vol             DECIMAL(9,4)  NOT NULL,            -- LegBidVolatility (5678), vol points
    offer_vol           DECIMAL(9,4)  NOT NULL,            -- LegOfferVolatility (9159)
    strike              DECIMAL(18,8) NOT NULL,            -- Strike (6354)
    delta_pct           DECIMAL(9,4)  NOT NULL,            -- LegDelta (9075), %
    PRIMARY KEY (price_id, leg_no),
    CONSTRAINT fk_fxohub_rfq_price_leg FOREIGN KEY (price_id) REFERENCES fxvol.fxohub_rfq_price (id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- The trade on a request (status TRADED) and the broker's drop copy (AE) of it in trade_stp.
ALTER TABLE fxvol.fxohub_rfq
    ADD COLUMN traded_utc        DATETIME(3)   NULL AFTER picked_up_utc,
    ADD COLUMN traded_side       CHAR(1)       NULL AFTER traded_utc,       -- B = we bought the structure as quoted, S = we sold it
    ADD COLUMN traded_vol        DECIMAL(9,4)  NULL AFTER traded_side,      -- strategy vol done
    ADD COLUMN traded_price_id   BIGINT        NULL AFTER traded_vol,       -- fxohub_rfq_price.id done
    ADD COLUMN drop_copy_utc     DATETIME(3)   NULL AFTER traded_price_id,
    ADD COLUMN stp_message_in_id BIGINT        NULL AFTER drop_copy_utc;    -- trade_stp.MessageIn.MessageInId of the drop copy
