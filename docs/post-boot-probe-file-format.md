# Post-boot probe file format, schema 1

This document is the normative byte contract for the protected Agent probe file. Agent and Controller implementations that create, verify, hash, or compare this file must use the same rules and golden vector below. A platform JSON serializer is not the contract; equivalent JSON encodings are rejected.

The format only identifies the configured file bytes. It does not prove whole-machine restoration, hardware identity, or restoration of any other file or volume.

## Fixed record layout

Every record is exactly 4096 bytes:

| Offset | Length | Meaning |
|---:|---:|---|
| 0 | 8 | ASCII `KRPROBE1` |
| 8 | 4 | Schema `1`, unsigned 32-bit little-endian |
| 12 | 4 | Canonical JSON byte length, unsigned 32-bit little-endian |
| 16 | variable | Canonical JSON bytes, no BOM |
| after JSON | remainder | All zero bytes |

The JSON length must be between 1 and 4080. The header schema must be 1. A different total file size, header, JSON length, or nonzero padding is invalid. The SHA-256 used by the probe ledger covers all 4096 bytes, including the zero padding.

## Record fields and validation

The JSON object has exactly these nine properties, in this order, with no duplicate or unknown properties:

1. `schemaVersion`: JSON integer exactly `1`.
2. `machineId`: nonempty lowercase GUID in 32-character `N` format.
3. `machineKeyFingerprint`: 64 uppercase hexadecimal characters.
4. `policyRevision`: positive signed 64-bit integer.
5. `baselineId`: nonempty text, at most 128 UTF-16 code units, not all whitespace, containing no Unicode control characters.
6. `recoveryGeneration`: same text constraints as `baselineId`.
7. `kind`: exactly `clean` or `dirty`.
8. `generation`: signed 64-bit integer; `clean` requires exactly `0`, and `dirty` requires a value greater than `0`.
9. `seed`: canonical unpadded base64url representing exactly 32 bytes (43 characters from `A-Z`, `a-z`, `0-9`, `-`, `_`).

Baseline and recovery strings must contain valid Unicode scalar sequences. No Unicode normalization is performed; different scalar sequences remain different values. Control characters are invalid field values, including when represented in JSON with an escape such as `\u000A`.

## Canonical JSON bytes

The body is one UTF-8 JSON object with no BOM, no whitespace outside strings, and no trailing data. Property names are the literal ASCII spellings above. Integers use invariant base-10 notation with no leading zero, plus sign, decimal point, or exponent. The JSON bytes are the UTF-8 encoding of the following ordered member sequence, with the string rules below applied:

```text
{"schemaVersion":1,"machineId":"…","machineKeyFingerprint":"…","policyRevision":1,"baselineId":"…","recoveryGeneration":"…","kind":"clean","generation":0,"seed":"…"}
```

String encoding is defined as follows:

- Escape a quotation mark as `\"` and a reverse solidus as `\\`.
- Emit every other permitted Unicode scalar literally as UTF-8. In particular, do not escape `/`, `<`, `>`, `&`, BMP characters, or supplementary characters.
- No control character can occur in a valid field, so control escapes do not occur in a valid record.
- Do not emit `\uXXXX` escapes for otherwise permitted characters. Do not normalize Unicode.

For example, a permitted `baselineId` value containing `quote" backslash\ slash/ <>& 雪 🌱` is encoded in JSON as:

```json
"baselineId":"quote\" backslash\\ slash/ <>& 雪 🌱"
```

`雪` and `🌱` are literal UTF-8 characters in the file. Equivalent JSON such as `\u0022` for the quotation mark, `\u005C` for the reverse solidus, `\/` for the slash, `\u003C` for `<`, `\u96EA` for `雪`, or `\uD83C\uDF31` for `🌱` is invalid. Decoders must parse the bounded JSON, validate the exact property order and field constraints, re-encode it using this contract, and require byte-for-byte equality with the supplied JSON bytes.

## Producer and verifier requirements

The Agent and any future Controller producer must share the same canonical rules and the golden vectors in `services/Agent.Tests/ProbeFileTests.cs`. Do not duplicate a platform-default serializer and assume its escaping behavior matches this format. A compatible implementation must produce the exact header, JSON byte sequence, and zero padding before the full 4096-byte digest is computed.
