# V1 / V2 wall comparison

- Reference image: 600×345 (1.739:1).
- Captured V2 image: [`table-20261112-103226-491-0.png`](../../build/ui_proto/a899b75ca4d9483195a38ef7466edbc1/screenshots/table-20261112-103226-491-0.png), 1200×690 (same aspect ratio), 864,950 bytes, SHA-256 `42D14C31109C2A1D9FE20DEFA98988A86FE4362AF3402F43FD6F48B3D8A1FE4D`.
- Captured V1 image: [`table-20261112-103323-469-0.png`](../../build/ui_proto/a899b75ca4d9483195a38ef7466edbc1/screenshots/table-20261112-103323-469-0.png), 1280×720, 981,436 bytes, SHA-256 `2DEC37E0B6B5973A4C403F53EC320DD0C4B93DD03CB3F83D3029D7689AF6B0CE`.

## Visual result

V2 now uses a continuous dark teal back face for each opponent tile. A shared mesh adds a light top plane, a darker side plane, narrow inset back detailing and a soft offset shadow. The north wall reads as a horizontal row. West and east rows follow the table sides, mirror each other, and grow toward the near ends. The face-up player hand and the existing river/center composition are unchanged. At the captured size, all three walls read as connected thick tile rows rather than separate orange cards.

## Remaining gaps

- The wall rows end before the seat plates and do not turn through the corners; the current composition leaves those breaks open.
- The back design is flat vector color with a small diamond and thin border; it does not reproduce the reference's fine tile texture, rounded edges or material reflections.
- Side-wall perspective is a fixed 2D taper and screen-plane extrusion, not a camera projection; the top row remains constant size.
- Existing portraits, labels, rivers and central HUD are retained prototype art and are not pixel-matched to the reference.

The PNGs are from the fresh Windows Player in `build/ui_proto/a899b75ca4d9483195a38ef7466edbc1/`. C created one image in V2 and one in V1. The V1 screenshot still shows the original orange rotated-card walls, confirming the old view remains separate.
