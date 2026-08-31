# Agent Note: base64url signature tampering must change a full decoded byte

Status: implemented

## Problem

`FenceTests.TamperedCookie_Settles401` flaked ~3-6% of the time: the tampered cookie sometimes
authenticated and the test got a 200 instead of a 401. The fence's HMAC verification was correct
— the TEST was tampering in a bit position the decoder discards.

## Decision

A 43-character base64url encoding of a 32-byte HMAC-SHA256 carries 258 bits (256 payload + 2
spare). The final character contributes only 4 bits to the decoded bytes; its low 2 bits are
discarded. Flipping the last character between two alphabet entries that share the same top-4
bits (e.g. 'A' and 'B', both 0b0000xxxx) decodes to byte-identical output, so the signature still
verifies. The flake hit exactly when the signature's last character was 'A' or 'B' (2 of 64
values). The test now tampers the signature's FIRST character, which contributes a full 6 bits
and always changes the decoded bytes.

## Consequence

40 consecutive suite runs pass with zero failures (previously one failure in ~7-30 runs). The
insight is general: any test that "corrupts" a base64url value by flipping its last character is
not corrupting it at all when the flip stays inside the discarded-bit class.

## Alternatives considered

- Flipping a character in the middle of the signature: equally valid, but the first character is
  the simplest positional fix and always lands in a full-byte position.
- "Fixing" the fence to reject byte-identical signatures: the verification is correct; the
  discarded bits are a property of the encoding, not a weakness.
