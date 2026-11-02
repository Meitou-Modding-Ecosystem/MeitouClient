The JSON is an identical synthetic copy of KenshiOracle `schema/canonical-vectors.json`.
SHA256 of the content with CRLF normalised to LF: `cd935563cb97f247ca7021a6bc1215d76bf21c01382e8d544783e4c138447f63`.
Set `KENSHI_ORACLE_VECTORS` to test the shared original directly. The first 59 cases
are the phase 1 Python/native numeric, string and nullable-state vectors; three
additional cases check scalar key order and additive state 0.2 canonicalisation,
three more check the additive state 0.3 slave fields, and four more check the additive
state 0.4 fields (medical, stats, part care, holding and carrying, squad money).
Regenerate with KenshiOracle `schema/export_canonical_vectors.py`, then copy bytes.
