The JSON is an identical synthetic copy of KenshiOracle `schema/canonical-vectors.json`.
SHA256 of the content with CRLF normalised to LF: `d41b18288b8510e379ff7bcc62212d018ec75fe80edfa045f8c2df2b17a03cad`.
Set `KENSHI_ORACLE_VECTORS` to test the shared original directly. The first 59 cases
are the phase 1 Python/native numeric, string and nullable-state vectors; three
additional cases check scalar key order and additive state 0.2 canonicalisation.
Regenerate with KenshiOracle `schema/export_canonical_vectors.py`, then copy bytes.
