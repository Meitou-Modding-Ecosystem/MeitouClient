The JSON is an identical synthetic copy of KenshiOracle `schema/canonical-vectors.json`.
SHA256 of the content with CRLF normalised to LF: `06a63911f1375ec5d19027c261a1bd066f2a4682c84add7f0e84ce3f434b2170`.
Set `KENSHI_ORACLE_VECTORS` to test the shared original directly. The first 59 cases
are the phase 1 Python/native numeric, string and nullable-state vectors; three
additional cases check scalar key order and additive state 0.2 canonicalisation,
and three more check the additive state 0.3 slave fields.
Regenerate with KenshiOracle `schema/export_canonical_vectors.py`, then copy bytes.
