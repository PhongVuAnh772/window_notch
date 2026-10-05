# ANIMATION SYSTEM

## Principles

Animations must feel:

- fluid
- physical
- subtle
- responsive
- premium

Never use excessive bounce.

---

## IDLE → HOVER

Duration:

160ms

Animate:

- width
- height
- corner radius
- opacity
- content

Use:

Composition animation

---

## HOVER → EXPANDED

Duration:

300ms

Use:

spring-like motion

Behavior:

1. container expands
2. corner radius morphs
3. content fades in
4. controls appear
5. surface settles

---

## EXPANDED → IDLE

Duration:

240ms

Sequence:

1. secondary content fades
2. controls disappear
3. container contracts
4. radius becomes pill
5. return to idle

---

## CONTENT SWITCH

Duration:

120–180ms

Old content:

fade + translate out

New content:

fade + translate in

Do not instantly replace content.

---

## MOUSE LEAVE

Collapse delay:

250–500ms

If pointer returns during this period:

cancel collapse.
