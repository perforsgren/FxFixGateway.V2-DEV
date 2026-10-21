-- FXOhub quotes: the TraderID (9536) sent with a price version, as the broker sees it. The pricer writes it when it queues
-- the price, from its table fxvol.pricer_trader (OptionSuite.V2: Docs/Sql/20261021_pricer_trader.sql); null = the pricer
-- user (trader) is sent. FXOhub keeps TraderID as text on the quote, not mapped to a user (Graham, TP ICAP, 21 Oct).

ALTER TABLE fxvol.fxohub_rfq_price
    ADD COLUMN trader_name VARCHAR(15) NULL COMMENT 'TraderID (9536) sent, e.g. PFORSGREN; null = trader is sent' AFTER trader;
