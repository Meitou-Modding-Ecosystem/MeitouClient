The JSON is an identical synthetic copy of KenshiOracle `schema/canonical-vectors.json`.
SHA256 of the content with CRLF normalised to LF: `99f4bfee37ee2c953c7dc34954d8d525e5f116564dcfda11c67bb2ab0f8c1484`.
Set `KENSHI_ORACLE_VECTORS` to test the shared original directly. The first 59 cases
are the phase 1 Python/native numeric, string and nullable-state vectors; three
additional cases check scalar key order and additive state 0.2 canonicalisation,
three more check the additive state 0.3 slave fields, four more check the additive
state 0.4 fields (medical, stats, part care, holding and carrying, squad money), and five
more check the additive state 0.5 goal (every priority, a null priority, a null goal).
Regenerate with KenshiOracle `schema/export_canonical_vectors.py`, then copy bytes.
