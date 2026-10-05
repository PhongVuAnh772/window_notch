# Windows Notch — AI Development Rules

## ROLE

You are the lead engineer responsible for building a premium
Windows Dynamic Notch application.

You must prioritize:

1. Visual quality
2. Animation quality
3. Native Windows integration
4. Performance
5. Maintainable architecture

Do not optimize for implementation speed at the expense of UX quality.

---

# NON-NEGOTIABLE RULES

## Platform

Use:

- C#
- .NET 8+
- WinUI 3
- Windows App SDK
- Windows Composition API
- Win32 APIs when necessary

Do NOT use:

- Electron
- Flutter
- React
- React Native
- WebView for the main UI

---

# DESIGN

The application should feel:

- macOS-inspired
- premium
- minimal
- dark
- elegant
- glass-like
- spatial
- calm

It must NOT look like:

- Windows Settings
- a widget dashboard
- a website
- a generic rounded rectangle
- a gaming overlay

Do not clone OneNotch.

Use its interaction philosophy as inspiration,
but create an original Windows-native product.

---

# ARCHITECTURE

Never put the entire application into one file.

Separate:

- Window
- State
- Animation
- UI
- Services
- Platform APIs
- Features
- Settings

Each feature must be independently removable.

---

# NOTCH

There must be ONE persistent notch window.

Never implement state transitions by:

1. closing the current window
2. creating another window
3. showing another popup

Instead morph the same visual surface.

Animate:

- width
- height
- position
- corner radius
- opacity
- scale
- content
- shadow

---

# ANIMATION

Animation quality is a first-class feature.

Prefer:

- Microsoft.UI.Composition
- CompositionAnimation
- spring animations
- keyframe animations
- expression animations

Avoid:

- polling animation loops
- Thread.Sleep
- manual frame rendering
- unnecessary timers
- recreating UI trees

Never silently replace a requested spring/compositor animation
with a basic opacity animation.

If a limitation exists, explain it.

---

# PERFORMANCE

The application must be event-driven.

Do NOT continuously poll:

- clipboard
- media
- system state
- mouse position

Prefer:

- Windows events
- Win32 messages
- async callbacks
- native APIs

Idle CPU should be close to zero.

Lazy-load optional features.

---

# DESIGN TOKENS

Never introduce arbitrary design values.

Use the values defined in:

.antigravity/design.md

If a new value is required:

1. explain why
2. add it to the design system
3. use the token

Do not scatter magic numbers.

---

# DEVELOPMENT PROCESS

Before implementing any task:

1. Read AGENTS.md
2. Read relevant files under .antigravity/
3. Inspect the existing implementation
4. Identify affected files
5. Create an implementation plan
6. Implement ONLY the requested task
7. Build the project
8. Run tests/analyzer
9. Review against the specification
10. Fix discovered issues
11. Report what changed

Do not implement future milestones.

Do not rewrite unrelated code.

---

# DEFINITION OF DONE

A task is NOT complete because the code compiles.

It is complete only when:

- implementation matches specification
- build succeeds
- tests pass
- animation behaves correctly
- no obvious visual regressions exist
- architecture remains clean
- no unnecessary polling exists
- no unrelated files were modified
