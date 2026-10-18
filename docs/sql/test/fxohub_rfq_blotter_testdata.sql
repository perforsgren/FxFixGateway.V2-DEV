-- Test requests for the pricer's Incoming RFQ blotter, as FxFixGateway stores them, in every state of the mockup
-- (incoming, picked up by you / by someone else, priced, traded, expired, withdrawn, rejected), plus a copy of the
-- UAT request of 9 Oct (AUD/CAD, LegID a GUID, ValidUntilTime = TransactTime). Session key 'TEST': the gateway has no
-- such session, so nothing here is ever sent to FXOhub (a price sent from the pricer on a TEST request stays QUEUED and
-- expires). Run 20261018_fxohub_rfq_price.sql and 20261018_fxohub_rfq_legid.sql first. Remove with
-- fxohub_rfq_blotter_testdata_cleanup.sql. "You" is P901PEF (the pricer's Windows user in upper case).

SET @now = UTC_TIMESTAMP(3);
SET @me  = 'P901PEF';

-- 1. Incoming: EURSEK 3M 25D risk reversal, delta hedged, two-way, 20m EUR, 15 min to answer; two quotes in the market.
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, broker_id, exec_delta_hedge, hedge_trade_type, premium_del,
       symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, has_market, raw_payload, received_utc, updated_utc)
VALUES ('TEST', 'TEST-1', 'INCOMING', @now, '1188', '1', '2', 'F', 'EUR/SEK', 'FOR', '4', 'Risk Reversal', '0',
        @now - INTERVAL 6 SECOND, @now + INTERVAL 15 MINUTE, 1, 'test', @now - INTERVAL 6 SECOND, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, leg_side, tenor_code, tenor, leg_type,
       option_style, cutoff, delivery_date, leg_delta, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'Leg1', 'EUR/SEK', 20, CURDATE() + INTERVAL 3 MONTH, 'B', '6', '3M', '1', '2', '1000NYK', CURDATE() + INTERVAL 3 MONTH + INTERVAL 2 DAY, 25, 'EUR', 'SEK', 'EUR', 'EUR'),
 (@r, 2, 'Leg2', 'EUR/SEK', 20, CURDATE() + INTERVAL 3 MONTH, 'C', '6', '3M', '2', '2', '1000NYK', CURDATE() + INTERVAL 3 MONTH + INTERVAL 2 DAY, 25, 'EUR', 'SEK', 'EUR', 'EUR');
INSERT INTO fxvol.fxohub_rfq_quote (session_key, quote_req_id, position, quote_set_id, quote_id, bid_strategy_vol, offer_strategy_vol, own_quote, received_utc) VALUES
 ('TEST', 'TEST-1', 1, 'QuoteSetID_1', 'T101', 0.15, 0.45, 0, @now),
 ('TEST', 'TEST-1', 2, 'QuoteSetID_2', 'T102', 0.10, 0.55, 0, @now);

-- 2. Incoming: EURNOK 1M 10.90 put, asks Buy (Side 1), delta hedged, 15m EUR.
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, broker_id, exec_delta_hedge, hedge_trade_type, premium_del,
       symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, raw_payload, received_utc, updated_utc)
VALUES ('TEST', 'TEST-2', 'INCOMING', @now, '1188', '1', '2', 'F', 'EUR/NOK', 'FOR', '1', 'Single Leg', '1',
        @now - INTERVAL 18 SECOND, @now + INTERVAL 15 MINUTE, 'test', @now - INTERVAL 18 SECOND, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, strike_price, leg_side, tenor_code, tenor, leg_type,
       option_style, cutoff, delivery_date, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'Leg1', 'EUR/NOK', 15, CURDATE() + INTERVAL 1 MONTH, 10.90, 'B', '4', '1M', '2', '2', '1000NYK', CURDATE() + INTERVAL 1 MONTH + INTERVAL 2 DAY, 'EUR', 'NOK', 'EUR', 'EUR');

-- 3. Incoming: a copy of the UAT request of 9 Oct (AUD/CAD single call, strike 1, 1m AUD, no hedge, LegID a GUID,
--    ValidUntilTime = TransactTime: no deadline), its depth a bid of vol 1 for 20 as the W that followed. Dates moved to today.
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, broker_id, trade_date, exec_delta_hedge, hedge_trade_type, premium_del,
       symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, has_market, raw_payload, received_utc, updated_utc)
VALUES ('TEST', '<TEST><FXO>39c84810-1e9e-47e0-be2f-5e26b1534e1b', 'INCOMING', @now, '1188', DATE_FORMAT(CURDATE(), '%Y%m%d'), '0', '0', 'F',
        'AUD/CAD', 'FOR', '1', 'Single Leg', '0', @now - INTERVAL 25 SECOND, @now - INTERVAL 25 SECOND, 1, 'test (copy of UAT 9 Oct)', @now - INTERVAL 25 SECOND, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, strike_price, leg_side, leg_type,
       option_style, cutoff, pit_type, delivery_date, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'cdfc7701-db48-42bd-a7f9-a48806c3853d', 'AUD/CAD', 1, CURDATE() + INTERVAL 1 MONTH, 1, 'B', '1', '2', '1000NYK', '1',
  CURDATE() + INTERVAL 1 MONTH + INTERVAL 4 DAY, 'AUD', 'CAD', 'AUD', 'AUD');
INSERT INTO fxvol.fxohub_rfq_quote (session_key, quote_req_id, position, quote_set_id, quote_id, bid_strategy_vol, offer_strategy_vol, own_quote, received_utc) VALUES
 ('TEST', '<TEST><FXO>39c84810-1e9e-47e0-be2f-5e26b1534e1b', 1, 'QuoteSetID_1', '187', 1, NULL, 0, @now);

-- 4. Picked up by you, no price yet: USDSEK 1M ATM straddle, delta hedged, two-way, 10m USD (open it in the pricer).
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, picked_up_by, picked_up_utc, broker_id, exec_delta_hedge, hedge_trade_type,
       premium_del, symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, raw_payload, received_utc, updated_utc)
VALUES ('TEST', 'TEST-4', 'PICKED_UP', @now, @me, @now - INTERVAL 20 SECOND, '1188', '1', '2', 'F', 'USD/SEK', 'FOR', '2', 'Straddle', '0',
        @now - INTERVAL 30 SECOND, @now + INTERVAL 15 MINUTE, 'test', @now - INTERVAL 30 SECOND, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, leg_side, tenor_code, tenor, leg_type,
       option_style, cutoff, delivery_date, atm_type, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'Leg1', 'USD/SEK', 10, CURDATE() + INTERVAL 1 MONTH, 'B', '4', '1M', '1', '2', '1000NYK', CURDATE() + INTERVAL 1 MONTH + INTERVAL 2 DAY, '1', 'USD', 'SEK', 'USD', 'USD'),
 (@r, 2, 'Leg2', 'USD/SEK', 10, CURDATE() + INTERVAL 1 MONTH, 'B', '4', '1M', '2', '2', '1000NYK', CURDATE() + INTERVAL 1 MONTH + INTERVAL 2 DAY, '1', 'USD', 'SEK', 'USD', 'USD');

-- 5. Picked up by someone else: EURSEK 6M 11.40 call, asks Sell (Side 2), 10m EUR ("taken").
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, picked_up_by, picked_up_utc, broker_id, exec_delta_hedge, hedge_trade_type,
       premium_del, symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, raw_payload, received_utc, updated_utc)
VALUES ('TEST', 'TEST-5', 'PICKED_UP', @now, 'K.ANDERSSON', @now - INTERVAL 1 MINUTE, '1188', '1', '2', 'F', 'EUR/SEK', 'FOR', '1', 'Single Leg', '2',
        @now - INTERVAL 2 MINUTE, @now + INTERVAL 15 MINUTE, 'test', @now - INTERVAL 2 MINUTE, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, strike_price, leg_side, tenor_code, tenor, leg_type,
       option_style, cutoff, delivery_date, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'Leg1', 'EUR/SEK', 10, CURDATE() + INTERVAL 6 MONTH, 11.40, 'B', '7', '6M', '1', '2', '1000NYK', CURDATE() + INTERVAL 6 MONTH + INTERVAL 2 DAY, 'EUR', 'SEK', 'EUR', 'EUR');

-- 6. Priced by you: EURNOK 6M 10.90 put, two-way, 15m EUR — version 1 sent and acknowledged, off in 10 minutes.
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, picked_up_by, picked_up_utc, broker_id, exec_delta_hedge, hedge_trade_type,
       premium_del, symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, has_market, raw_payload, received_utc, updated_utc)
VALUES ('TEST', 'TEST-6', 'PICKED_UP', @now, @me, @now - INTERVAL 3 MINUTE, '1188', '1', '2', 'F', 'EUR/NOK', 'FOR', '1', 'Single Leg', '0',
        @now - INTERVAL 4 MINUTE, @now + INTERVAL 15 MINUTE, 1, 'test', @now - INTERVAL 4 MINUTE, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, strike_price, leg_side, tenor_code, tenor, leg_type,
       option_style, cutoff, delivery_date, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'Leg1', 'EUR/NOK', 15, CURDATE() + INTERVAL 6 MONTH, 10.90, 'B', '7', '6M', '2', '2', '1000NYK', CURDATE() + INTERVAL 6 MONTH + INTERVAL 2 DAY, 'EUR', 'NOK', 'EUR', 'EUR');
INSERT INTO fxvol.fxohub_rfq_price (rfq_id, version, status, status_utc, trader, created_utc, valid_seconds, valid_until_utc, spot_ref, spot_pips,
       spot_low, spot_high, hedge_amount, hedge_currency, hedge_rate, hedge_side, hedge_text, vol_quote, premium_text, premium_currency, premium_unit,
       premium_bid, premium_offer, premium_bid_amount, premium_offer_amount, quote_id, sent_utc, ack_utc)
VALUES (@r, 1, 'ACCEPTED', @now - INTERVAL 20 SECOND, @me, @now - INTERVAL 21 SECOND, NULL, @now + INTERVAL 10 MINUTE, 10.63000, 15,
        10.62850, 10.63150, 5400000, 'EUR', 10.63000, 'B', '5.40m EUR @ 10.63000', '6.200 / 6.550', '1.5120 / 1.6050 % of EUR', 'EUR', '% of EUR',
        1.5120, 1.6050, 226800, 240750, 'TEST-6-1', @now - INTERVAL 20 SECOND, @now - INTERVAL 20 SECOND);
INSERT INTO fxvol.fxohub_rfq_price_leg (price_id, leg_no, bid_vol, offer_vol, strike, delta_pct) VALUES (LAST_INSERT_ID(), 1, 6.2, 6.55, 10.90, 36.1);
INSERT INTO fxvol.fxohub_rfq_quote (session_key, quote_req_id, position, quote_set_id, quote_id, bid_strategy_vol, offer_strategy_vol, own_quote, received_utc) VALUES
 ('TEST', 'TEST-6', 1, 'QuoteSetID_1', 'TEST-6-1', 6.20, 6.55, 1, @now),
 ('TEST', 'TEST-6', 2, 'QuoteSetID_2', 'T602', 6.10, 6.70, 0, @now);

-- 7. Traded by you: EURSEK 1Y 25D strangle, two-way, 25m EUR — we sold at 6.450 (our offer); the drop copy came 2 s later.
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, picked_up_by, picked_up_utc, broker_id, exec_delta_hedge, hedge_trade_type,
       premium_del, symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, traded_utc, traded_side, traded_vol,
       drop_copy_utc, raw_payload, received_utc, updated_utc)
VALUES ('TEST', 'TEST-7', 'TRADED', @now - INTERVAL 9 MINUTE, @me, @now - INTERVAL 11 MINUTE, '1188', '1', '2', 'F', 'EUR/SEK', 'FOR', '3', 'Strangle', '0',
        @now - INTERVAL 12 MINUTE, @now - INTERVAL 12 MINUTE, @now - INTERVAL 9 MINUTE, 'S', 6.45, @now - INTERVAL 9 MINUTE + INTERVAL 2 SECOND,
        'test', @now - INTERVAL 12 MINUTE, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, leg_side, leg_type,
       option_style, cutoff, delivery_date, leg_delta, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'Leg1', 'EUR/SEK', 25, CURDATE() + INTERVAL 1 YEAR, 'B', '1', '2', '1000NYK', CURDATE() + INTERVAL 1 YEAR + INTERVAL 2 DAY, 25, 'EUR', 'SEK', 'EUR', 'EUR'),
 (@r, 2, 'Leg2', 'EUR/SEK', 25, CURDATE() + INTERVAL 1 YEAR, 'B', '2', '2', '1000NYK', CURDATE() + INTERVAL 1 YEAR + INTERVAL 2 DAY, 25, 'EUR', 'SEK', 'EUR', 'EUR');
INSERT INTO fxvol.fxohub_rfq_price (rfq_id, version, status, status_utc, trader, created_utc, valid_seconds, valid_until_utc, spot_ref, spot_pips,
       spot_low, spot_high, hedge_text, vol_quote, premium_text, premium_currency, premium_unit, premium_bid, premium_offer, premium_bid_amount,
       premium_offer_amount, quote_id, sent_utc, ack_utc, closed_utc)
VALUES (@r, 1, 'DONE', @now - INTERVAL 9 MINUTE, @me, @now - INTERVAL 10 MINUTE, 120, @now - INTERVAL 8 MINUTE, 11.13600, 15, 11.13450, 11.13750,
        NULL, '6.100 / 6.450', '3.8200 / 4.0410 % of EUR', 'EUR', '% of EUR', 3.8200, 4.0410, 955000, 1010250,
        'TEST-7-1', @now - INTERVAL 10 MINUTE, @now - INTERVAL 10 MINUTE, @now - INTERVAL 9 MINUTE);
SET @p = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_price_leg (price_id, leg_no, bid_vol, offer_vol, strike, delta_pct) VALUES
 (@p, 1, 6.1, 6.45, 11.71, 25), (@p, 2, 6.1, 6.45, 10.64, 25);
UPDATE fxvol.fxohub_rfq SET traded_price_id = @p WHERE id = @r;

-- 8. Expired: EURUSD 2M 1.1200 call, asks Buy, 30m EUR — our 7.150 ran out after 60 s, not done.
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, picked_up_by, picked_up_utc, broker_id, exec_delta_hedge, premium_del,
       symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, raw_payload, received_utc, updated_utc)
VALUES ('TEST', 'TEST-8', 'PICKED_UP', @now - INTERVAL 14 MINUTE, @me, @now - INTERVAL 14 MINUTE, '1188', '0', 'F', 'EUR/USD', 'FOR', '1', 'Single Leg', '1',
        @now - INTERVAL 15 MINUTE, @now - INTERVAL 12 MINUTE, 'test', @now - INTERVAL 15 MINUTE, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, strike_price, leg_side, tenor_code, tenor, leg_type,
       option_style, cutoff, delivery_date, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'Leg1', 'EUR/USD', 30, CURDATE() + INTERVAL 2 MONTH, 1.12, 'B', '5', '2M', '1', '2', '1000NYK', CURDATE() + INTERVAL 2 MONTH + INTERVAL 2 DAY, 'EUR', 'USD', 'EUR', 'EUR');
INSERT INTO fxvol.fxohub_rfq_price (rfq_id, version, status, status_reason, status_utc, trader, created_utc, valid_seconds, valid_until_utc, spot_ref, spot_pips,
       spot_low, spot_high, vol_quote, quote_id, sent_utc, ack_utc, closed_utc)
VALUES (@r, 1, 'EXPIRED', 'its time ran out', @now - INTERVAL 13 MINUTE, @me, @now - INTERVAL 14 MINUTE, 60, @now - INTERVAL 13 MINUTE, 1.16300, 15,
        1.16150, 1.16450, '7.150', 'TEST-8-1', @now - INTERVAL 14 MINUTE, @now - INTERVAL 14 MINUTE, @now - INTERVAL 13 MINUTE);
INSERT INTO fxvol.fxohub_rfq_price_leg (price_id, leg_no, bid_vol, offer_vol, strike, delta_pct) VALUES (LAST_INSERT_ID(), 1, 7.15, 7.15, 1.12, 61.3);

-- 9. Withdrawn by someone else: USDSEK 3M 10.40 put, two-way, 10m USD — spot left the range.
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, picked_up_by, picked_up_utc, broker_id, exec_delta_hedge, hedge_trade_type,
       premium_del, symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, raw_payload, received_utc, updated_utc)
VALUES ('TEST', 'TEST-9', 'PICKED_UP', @now - INTERVAL 21 MINUTE, 'K.ANDERSSON', @now - INTERVAL 21 MINUTE, '1188', '1', '2', 'F', 'USD/SEK', 'FOR', '1',
        'Single Leg', '0', @now - INTERVAL 22 MINUTE, @now - INTERVAL 19 MINUTE, 'test', @now - INTERVAL 22 MINUTE, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, strike_price, leg_side, tenor_code, tenor, leg_type,
       option_style, cutoff, delivery_date, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'Leg1', 'USD/SEK', 10, CURDATE() + INTERVAL 3 MONTH, 10.40, 'B', '6', '3M', '2', '2', '1000NYK', CURDATE() + INTERVAL 3 MONTH + INTERVAL 2 DAY, 'USD', 'SEK', 'USD', 'USD');
INSERT INTO fxvol.fxohub_rfq_price (rfq_id, version, status, status_utc, trader, created_utc, valid_seconds, valid_until_utc, spot_ref, spot_pips,
       spot_low, spot_high, vol_quote, quote_id, sent_utc, ack_utc, withdraw_reason, cancel_sent_utc, closed_utc)
VALUES (@r, 1, 'WITHDRAWN', @now - INTERVAL 20 MINUTE, 'K.ANDERSSON', @now - INTERVAL 21 MINUTE, 120, @now - INTERVAL 19 MINUTE, 10.45200, 15,
        10.45050, 10.45350, '8.100 / 8.450', 'TEST-9-1', @now - INTERVAL 21 MINUTE, @now - INTERVAL 21 MINUTE,
        'spot left 10.45200 ±15 (live 10.45410)', @now - INTERVAL 20 MINUTE, @now - INTERVAL 20 MINUTE);
INSERT INTO fxvol.fxohub_rfq_price_leg (price_id, leg_no, bid_vol, offer_vol, strike, delta_pct) VALUES (LAST_INSERT_ID(), 1, 8.1, 8.45, 10.40, 41.0);

-- 10. Rejected: EURSEK 2Y 10D risk reversal, two-way, 50m EUR — our price was rejected with a reason.
INSERT INTO fxvol.fxohub_rfq (session_key, quote_req_id, status, status_utc, picked_up_by, picked_up_utc, broker_id, exec_delta_hedge, hedge_trade_type,
       premium_del, symbol, security_type, option_strategy, strategy_name, side, transact_time_utc, valid_until_utc, raw_payload, received_utc, updated_utc)
VALUES ('TEST', 'TEST-10', 'PICKED_UP', @now - INTERVAL 30 MINUTE, @me, @now - INTERVAL 30 MINUTE, '1188', '1', '2', 'F', 'EUR/SEK', 'FOR', '4',
        'Risk Reversal', '0', @now - INTERVAL 31 MINUTE, @now - INTERVAL 28 MINUTE, 'test', @now - INTERVAL 31 MINUTE, @now);
SET @r = LAST_INSERT_ID();
INSERT INTO fxvol.fxohub_rfq_leg (rfq_id, leg_no, leg_id, leg_symbol, leg_qty_millions, maturity_date, leg_side, tenor_code, tenor, leg_type,
       option_style, cutoff, delivery_date, leg_delta, leg_currency, currency2, call_put_currency, settl_currency) VALUES
 (@r, 1, 'Leg1', 'EUR/SEK', 50, CURDATE() + INTERVAL 2 YEAR, 'B', '9', '2Y', '1', '2', '1000NYK', CURDATE() + INTERVAL 2 YEAR + INTERVAL 2 DAY, 10, 'EUR', 'SEK', 'EUR', 'EUR'),
 (@r, 2, 'Leg2', 'EUR/SEK', 50, CURDATE() + INTERVAL 2 YEAR, 'C', '9', '2Y', '2', '2', '1000NYK', CURDATE() + INTERVAL 2 YEAR + INTERVAL 2 DAY, 10, 'EUR', 'SEK', 'EUR', 'EUR');
INSERT INTO fxvol.fxohub_rfq_price (rfq_id, version, status, status_reason, status_utc, trader, created_utc, valid_seconds, valid_until_utc, spot_ref, spot_pips,
       spot_low, spot_high, vol_quote, closed_utc)
VALUES (@r, 1, 'REJECTED', 'smile not built: EURSEK 2Y', @now - INTERVAL 30 MINUTE, @me, @now - INTERVAL 30 MINUTE, 60, @now - INTERVAL 29 MINUTE,
        11.13600, 15, 11.13450, 11.13750, '0.300 / 0.700', @now - INTERVAL 30 MINUTE);
INSERT INTO fxvol.fxohub_rfq_price_leg (price_id, leg_no, bid_vol, offer_vol, strike, delta_pct) VALUES
 (LAST_INSERT_ID(), 1, 7.2, 7.6, 12.40, 10), (LAST_INSERT_ID(), 2, 6.9, 7.3, 10.10, 10);
