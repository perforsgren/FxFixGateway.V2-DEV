"""
Builds src/FxFixGateway.UI/FIX44_Bloomberg.xml, the QuickFIX/n data dictionary for Bloomberg FXGO Options RFQ sessions,
from the standard FIX 4.4 dictionary (QuickFIXn.FIX4.4's DataDictionary/FIX44.xml) and Bloomberg's FX FIXBook for
Options RFS 4.1.5 (July 2026, BBG dialect 4.0.0 / 4.1.0).

  python build_fix44_bloomberg.py <path to FIX44.xml> <output path>

What it changes in the standard dictionary:
  - QuoteRequest (R), QuoteRequestReject (AG), Quote (S), QuoteCancel (Z), QuoteStatusReport (AI), NewOrderSingle (D) and
    ExecutionReport (8) are replaced by Bloomberg's layouts, and ExecutionAcknowledgement (BN) is added. Every field and
    repeating group of the FIXBook tables is listed, in the FIXBook's order, with the group's first field as its delimiter
    (QuickFIX/n ends a group entry at the first field the group does not list, so a missing field would break the parse).
  - Bloomberg's own fields (and the FIX 5.0 fields the FIXBook uses) are added.
  - Every field of those messages has no list of values: Bloomberg uses values the FIX 4.4 lists do not have (Side 0 =
    two-way, PartyRole 16/64/73/122, PartySubIDType 4025/4046/4047, QuoteRequestRejectReason 10/11/100-102 ...), and
    QuickFIX rejects a value outside a field's list.
Nothing is required in these messages (the sessions do not validate incoming messages; see SessionSettingsBuilder).
"""

import sys
import xml.etree.ElementTree as ET

# Bloomberg's fields (and FIX 5.0 fields) that FIX 4.4 does not define: number -> (name, type).
CUSTOM_FIELDS = {
    1300: ("MarketSegmentID", "STRING"),
    2376: ("PartyRoleQualifier", "INT"),
    2489: ("PackageID", "STRING"),
    2668: ("NoTrdRegPublications", "NUMINGROUP"),
    2669: ("TrdRegPublicationType", "INT"),
    2670: ("TrdRegPublicationReason", "INT"),
    1907: ("NoRegulatoryTradeIDs", "NUMINGROUP"),
    1903: ("RegulatoryTradeID", "STRING"),
    1904: ("RegulatoryTradeIDEvent", "INT"),
    1905: ("RegulatoryTradeIDSource", "STRING"),
    1906: ("RegulatoryTradeIDType", "INT"),
    2411: ("RegulatoryLegRefID", "STRING"),
    1036: ("ExecAckStatus", "CHAR"),
    1908: ("NoAllocRegulatoryTradeIDs", "NUMINGROUP"),
    1909: ("AllocRegulatoryTradeID", "STRING"),
    1910: ("AllocRegulatoryTradeIDSource", "STRING"),
    2891: ("UPICode", "STRING"),
    2893: ("LegUPICode", "STRING"),
    6702: ("InCompetition", "BOOLEAN"),
    9126: ("OptionStrategy", "INT"),
    9124: ("CutOff", "STRING"),
    9034: ("CallPutCurrency", "CURRENCY"),
    9072: ("CallOrPut", "INT"),
    9075: ("Delta", "FLOAT"),
    9018: ("LegNotionalAmount", "FLOAT"),
    9019: ("FXOptionStyle", "INT"),
    6351: ("Position", "INT"),
    9099: ("ForwardRate", "FLOAT"),
    9115: ("CCY1DepositRate", "FLOAT"),
    9118: ("CCY2DepositRate", "FLOAT"),
    9130: ("BarrierStyle", "INT"),
    9131: ("BarrierLevel", "FLOAT"),
    9137: ("BarrierLevel2", "FLOAT"),
    9132: ("BarrierDirection", "INT"),
    9187: ("TouchType", "INT"),
    5235: ("SpotRate", "FLOAT"),
    5830: ("PremiumCurrency", "CURRENCY"),
    5020: ("PremiumDeliveryDate", "LOCALMKTDATE"),
    9015: ("ExecDeltaHedge", "BOOLEAN"),
    9016: ("HedgeTradeType", "INT"),
    6666: ("HedgeDirection", "INT"),
    9679: ("HedgeSide", "INT"),
    9123: ("HedgeAmount", "AMT"),
    9657: ("HedgeRate", "FLOAT"),
    9112: ("HedgeDate", "LOCALMKTDATE"),
    22514: ("HedgeSettlMethod", "CHAR"),
    22656: ("HedgeSettlCurrency", "CURRENCY"),
    22195: ("PremiumDeliveryType", "INT"),
    9102: ("MarketType", "CHAR"),
    22720: ("DerivativeNegotiatedParameterType", "INT"),
    22721: ("DerivativeNegotiatedParameterValue", "FLOAT"),
    22723: ("DerivativeNegotiatedParameterLegRefID", "STRING"),
    9098: ("VolatilityLegBid", "FLOAT"),
    9159: ("VolatilityLegOffer", "FLOAT"),
    10010: ("BidPremium", "FLOAT"),
    10011: ("OfferPremium", "FLOAT"),
    6436: ("BidNetPremium", "FLOAT"),
    6437: ("OfferNetPremium", "FLOAT"),
    22410: ("LegISINProduct", "STRING"),
    22562: ("LegHedgeAmount", "AMT"),
    22563: ("LegHedgeCurrency", "CURRENCY"),
    22677: ("LegExecDeltaHedge", "BOOLEAN"),
    22678: ("LegDeltaHedgeSide", "CHAR"),
    22679: ("LegHedgeRate", "FLOAT"),
    22680: ("LegHedgeTradeType", "INT"),
    22681: ("LegHedgeDate", "LOCALMKTDATE"),
    22861: ("LegHedgeFixingDate", "LOCALMKTDATE"),
    22948: ("LegHedgeSecurityID", "STRING"),
    22949: ("LegHedgeSecurityIDSource", "STRING"),
    22950: ("LegHedgeSettlMethod", "STRING"),
    22951: ("LegHedgeUPICode", "STRING"),
    22968: ("LegHedgeSettlCurrency", "CURRENCY"),
    22292: ("HedgeSecurityID", "STRING"),
    22293: ("HedgeSecurityIDSource", "STRING"),
    22898: ("HedgeUPICode", "STRING"),
    21807: ("LiquidityTakerIsUSPerson", "INT"),
    21833: ("SwapReportingAgency", "STRING"),
    21834: ("BloombergSEFID", "STRING"),
    21835: ("ReportingParty", "INT"),
    20002: ("LegRegulatoryTradeID", "STRING"),
    20003: ("LegRegulatoryTradeIDSource", "STRING"),
    20004: ("RegulatoryHedgeTradeID", "STRING"),
    20005: ("RegulatoryHedgeSource", "STRING"),
    20006: ("HedgeRTN", "STRING"),
    22905: ("LegRTN", "STRING"),
    22425: ("LegAllocRegulatoryTradeID", "STRING"),
    22426: ("LegAllocRegulatoryTradeIDSource", "STRING"),
    22559: ("AllocHedgeQty", "FLOAT"),
    22560: ("AllocHedgePercent", "FLOAT"),
    22561: ("LegAllocPercent", "FLOAT"),
    22569: ("LegAllocHedgeQty", "FLOAT"),
    22570: ("LegAllocHedgePercent", "FLOAT"),
}


def group(counter, *items):
    return ("group", counter, list(items))


# NoPartyIDs: venue (MIC), liquidity taker, liquidity maker; the maker entries we send add PartyRoleQualifier (2376).
PARTIES = group(453, 448, 447, 452, 2376, group(802, 523, 803))

# NoLegAllocs inside a leg (pre-allocation), with the sub-account's nested parties (LEI, prime broker, custodian).
LEG_ALLOCS = group(670, 671, 672, 22425, 22426, group(756, 757, 758, 759), 673, 22561, 22569, 22570)

# A leg of the QuoteRequest and the NewOrderSingle. The leg prices (681/684, 9098/9159, 10010/10011) and regulatory ids
# (20002/20003) are the NewOrderSingle's; 600/624/687/611/588 are standard FIX 4.4 leg fields the FIXBook does not list,
# accepted so that a leg carrying one still parses.
LEG_REQUEST = group(
    555, 654, 602, 603, 2893, 556, 22410, 22562, 22563, 22677, 22678, 22679, 22680, 22681, 22861, 22948, 22949, 22950,
    22951, 22968, 9124, 432, 743, 9034, 9072, 9075, 612, 9018, 9019, 6351, 9099, 9115, 9118, 9130, 9131, 9137, 9132, 9187,
    681, 684, 9098, 9159, 10010, 10011, 20002, 20003, 600, 624, 687, 611, 588, LEG_ALLOCS)

# A leg of our Quote: the leg's reference and our prices.
LEG_QUOTE = group(555, 654, 602, 603, 556, 681, 684, 9098, 9159, 10010, 10011)

# A leg of our ExecutionReport: the trade echoed.
LEG_EXECUTION = group(
    555, 654, 22562, 22563, 22677, 22678, 22679, 22680, 22681, 22861, 22948, 22949, 22950, 22951, 22968, 9124, 432, 743,
    9034, 9072, 9075, 612, 556, 9018, 9019, 6351, 681, 684, 9130, 9131, 9137, 9132, 20002, 20003)

RELATED_SYM = group(146, 55, 65, 48, 22)

MESSAGES = [
    ("QuoteRequest", "R", [
        131, 1300, 537, RELATED_SYM, 1, PARTIES, 6702, 58, 75, 54, 9126, LEG_REQUEST, 5235, 5830, 5020, 9015, 9016,
        6666, 9679, 9123, 22292, 22293, 22898, 9657, 9112, 541, 22514, 22656, 63, 120, 22195, 9102, 692, 22720, 22723,
        21807, 21833, 21834, 21835, 2891, group(78, 79, 22559, 22560)]),
    ("QuoteRequestReject", "AG", [131, 658, 55, 65, 48, 22, 58]),
    ("Quote", "S", [
        117, 131, PARTIES, 537, 60, 54, 58, 55, 65, 48, 22, LEG_QUOTE, 188, 190, 22720, 22721, 22723, 631, 692, 5830,
        6436, 6437, 62, 63, 120]),
    ("QuoteCancel", "Z", [131, 117, 58, 298]),
    ("QuoteStatusReport", "AI", [131, 117, 297, 58]),
    ("NewOrderSingle", "D", [
        117, 131, 1300, RELATED_SYM, 11, group(78, 79, 22559, 22560, group(1908, 1909, 1910)), 75, 1, PARTIES, 60, 40,
        54, 9126, LEG_REQUEST, 631, 6436, 6437, 5830, 5020, 9015, 9016, 6666, 9679, 9123, 22292, 22293, 22898, 9657,
        9112, 541, 22514, 22656, 63, 120, 9102, 22195, 20004, 20005, 21807, 21833, 21834, 21835, 2489, 692, 22720,
        22721, 22723, group(2668, 2669, 2670), group(768, 769, 770), 828, 2891, 5235]),
    ("ExecutionReport", "8", [
        11, 17, 37, 117, PARTIES, 150, 103, 60, 29, 58, 1, 75, 54, 55, 9126, LEG_EXECUTION, 6436, 6437, 5830, 5020,
        9015, 9016, 6666, 9679, 9123, 9657, 9112, 22514, 22656, 63, 120, 20004, 20005]),
    ("ExecutionAcknowledgement", "BN", [
        11, 37, 17, 20006, 22898, 1036, group(1907, 1903, 1905, 1904, 1906, 2411, 2893, 22905),
        group(2668, 2669, 2670), group(768, 769, 770)]),
]


def main(source, target):
    tree = ET.parse(source)
    root = tree.getroot()
    fields_el = root.find("fields")
    messages_el = root.find("messages")

    by_number = {int(f.get("number")): f for f in fields_el.findall("field")}
    names = {f.get("name"): int(f.get("number")) for f in fields_el.findall("field")}

    for number, (name, ftype) in sorted(CUSTOM_FIELDS.items()):
        if number in by_number:
            raise SystemExit(f"Field {number} is already in FIX 4.4 as {by_number[number].get('name')}: drop it from CUSTOM_FIELDS")
        if name in names:
            raise SystemExit(f"Name {name} is already FIX 4.4 field {names[name]}: rename {number}")
        el = ET.SubElement(fields_el, "field", {"number": str(number), "name": name, "type": ftype})
        by_number[number] = el
        names[name] = number

    used = set()

    def name_of(number):
        if number not in by_number:
            raise SystemExit(f"Field {number} is neither in FIX 4.4 nor in CUSTOM_FIELDS")
        used.add(number)
        return by_number[number].get("name")

    def add_items(parent, items):
        for item in items:
            if isinstance(item, tuple):
                _, counter, sub = item
                g = ET.SubElement(parent, "group", {"name": name_of(counter), "required": "N"})
                add_items(g, sub)
            else:
                ET.SubElement(parent, "field", {"name": name_of(item), "required": "N"})

    replaced = {msgtype for _, msgtype, _ in MESSAGES}
    for m in list(messages_el.findall("message")):
        if m.get("msgtype") in replaced:
            messages_el.remove(m)
    for name, msgtype, items in MESSAGES:
        m = ET.SubElement(messages_el, "message", {"name": name, "msgtype": msgtype, "msgcat": "app"})
        add_items(m, items)

    for number in used:
        for value in list(by_number[number].findall("value")):
            by_number[number].remove(value)

    root.insert(0, ET.Comment(
        " Bloomberg FXGO Options RFQ (FX FIXBook for Options RFS 4.1.5). Generated by "
        "docs/dictionaries/build_fix44_bloomberg.py from QuickFIXn.FIX4.4's FIX44.xml; edit the script, not this file. "))
    ET.indent(tree, space="  ")
    tree.write(target, encoding="utf-8", xml_declaration=True)
    print(f"Wrote {target}: {len(MESSAGES)} messages, {len(CUSTOM_FIELDS)} fields added, {len(used)} fields without value lists")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    main(sys.argv[1], sys.argv[2])
