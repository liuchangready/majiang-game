# V1 / V2 wall comparison

## Capture dimensions and evidence

- User reference: `600x345`, ratio `40:23` (`1.73913`).
- V2: [`table-20261112-105853-213-0.png`](../../build/ui_proto/cb6dc8bae44c40f1af10d6d3eab66df6/screenshots/table-20261112-105853-213-0.png), `1200x690`, ratio `40:23`, 866,253 bytes, SHA-256 `B2ED220FDED553F1EF5AAD325DBAD86607A864D8D04A0232100956DDE3133563`.
- V1: [`table-20261112-110031-372-0.png`](../../build/ui_proto/cb6dc8bae44c40f1af10d6d3eab66df6/screenshots/table-20261112-110031-372-0.png), `1280x720`, ratio `16:9`, 981,436 bytes, SHA-256 `2DEC37E0B6B5973A4C403F53EC320DD0C4B93DD03CB3F83D3029D7689AF6B0CE`.
- Both screenshots were taken with C from the same fresh Player package at `build/ui_proto/cb6dc8bae44c40f1af10d6d3eab66df6/`. V1 intentionally retains its original size and view.

## V2 visual result

The opponent walls use one canonical tile body projected from a shared table-local coordinate basis. North stays horizontal; west and east follow the table sides with mirrored orientation and natural near/far size change. The top, side and back faces meet at common projected corners. Side rows join the north row at the corners without the former stepped overlap. V1 and the player’s face-up tiles retain their previous rendering.

The fresh V2 Player screenshot was inspected at full display size. The three walls read as a continuous U-shaped set of thick tiles; the north row’s end sides and both side walls show visible thickness. The silhouette and connectedness are substantially closer to the supplied image than the earlier rotated orange cards.

## Remaining visual gaps

- The tile backs are clean vector shapes with a small center mark; they do not reproduce the reference’s fine texture, rounded material edges, highlights or subtle wear.
- The projection is a deliberately compact UI mesh model; its lighting, shadow softness and depth cues remain simpler than the reference illustration.
- Existing portraits, labels, discard rivers and central HUD are retained prototype art and are not pixel-matched to the reference.

V2 matches the reference aspect ratio. V1 remains `16:9` to preserve its existing direct launch and composition; that is expected compatibility behavior.
