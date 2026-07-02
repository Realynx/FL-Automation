# FL Automate — shared UI design tokens

Canonical design system for BOTH the marketing site (`marketing/web`) and the flagship product UI
(Avalonia/Skia — see [[ui-framework-avalonia]]). Extracted from the marketing **hero** (2026-07-01) so the
product matches the marketing look. When either surface changes the theme, update this file + the other.

Aesthetic: **dark-first, electric gradient-mesh** — deep ink base, violet→magenta→cyan gradients, soft aurora
glows, subtle glassmorphism, tight display type. Dark, modern, energetic.

## Palette (hex)
**Brand — electric violet** (primary; gradients + glows)
`50 #f4f2ff · 100 #ece7ff · 200 #d8ccff · 300 #bca5ff · 400 #9d74ff · 500 #8b5cf6 · 600 #7c3aed · 700 #6d28d9 · 800 #581c9e · 900 #481a85 · 950 #2c0f5e`

**Accent — signal cyan**
`200 #a5f3fc · 300 #67e8f9 · 400 #22d3ee · 500 #06b6d4 · 600 #0891b2`

**Magenta / fuchsia** (gradient mid) — `400 #e879f9 · 500 #d946ef`

**Ink — dark surfaces / text**
`50 #eceef6 · 100 #cdcfdd · 200 #a6a8c0 (body) · 300 #8a8caa (muted) · 400 #777a9e (meta) · 500 #323247 · 600 #232333 (borders) · 700 #191925 · 800 #111119 · 900 #0b0b12 (elevated) · 950 #06060c (base bg)`

Hero usage: bg `ink-950`, body text `ink-200`, headline white + gradient, badge sparkle `accent-300`.

## Gradients & glows
- **Brand gradient** (buttons, chat bubbles, UI accents): `linear-gradient(110deg, #8b5cf6, #7c3aed 42%, #d946ef)`
- **Headline text-gradient**: `linear-gradient(110deg, #bca5ff, #e879f9 45%, #67e8f9)` (background-clip: text)
- **Aurora blobs** (radial glows, animated drift 16s; use RadialGradient, not blur):
  - violet `rgba(124,58,237,0.30)` 736px top-center · magenta `rgba(217,70,239,0.24)` 480px top-right · cyan `rgba(6,182,212,0.18)` 448px mid-left
- **Mixer meter**: `linear-gradient(to top, #22d3ee, #8b5cf6, #d946ef)`
- **Button primary shadow/glow**: `0 10px 40px -12px rgba(124,58,237,0.8)`
- **Card/mockup shadow**: `0 40px 120px -30px rgba(124,58,237,0.55)`
- **Note/playhead glows**: `0 0 12px rgba(139,92,246,.55)` / `rgba(34,211,238,.5)` / `rgba(217,70,239,.5)`; playhead `0 0 10px rgba(103,232,249,.9)`

## Typography (Google Fonts)
- **Headings:** Space Grotesk (500/600/700). **Body:** Inter (400/500/600/700).
- Headline: Space Grotesk 700, 36→60→72px, line-height 1.05, letter-spacing −0.02em.
- Body: Inter 400, 18→20px, line-height 1.625, color `ink-200`.
- Badge: Inter 500, 12px, tracking +0.1em. Buttons: Inter 500, 16px, tracking −0.015em.
- All headings letter-spacing −0.02em; focus outline 2px `accent-400`; selection `brand-500`@40%.

## Shape & depth
- Radii: pill `9999px`; window/card `16px`; panel `12px`; note/grid cells `3px`.
- Borders: default `ink-600 #232333`; panels/buttons/badges/inputs `1px solid rgba(255,255,255,0.10)` (white/10).
- Glass: white/5 fill + backdrop-blur. Grid pattern: 56×56px, 1px lines `color-mix(ink-400 22%, transparent)`, 60% opacity, bottom fade.
- Motion: entrance `animate-rise` 0.7s cubic-bezier(0.16,1,0.3,1) staggered; aurora 16s; playhead 5s linear; button hover lift 2px + brightness 110%. Respect reduced-motion.

## Avalonia (Skia) mapping
- Colors → `SolidColorBrush` resources (hex above). Gradients → `LinearGradientBrush` (110° = start (0,0)→ end via angle) / aurora → `RadialGradientBrush`. Glows/shadows → `DropShadowEffect` / `BoxShadow`.
- Headline gradient text → gradient brush on the `TextBlock.Foreground` (Avalonia supports gradient foreground). Glass → semi-transparent fill + `BlurEffect`/acrylic. Grid → tiled `DrawingBrush`.
- Fonts: bundle Space Grotesk + Inter as app assets (don't rely on system). Theme = a dark `FluentTheme` override or custom ControlThemes keyed off these tokens.

Sources: `marketing/web/src/index.css`, `.../components/sections/Hero.tsx`, `.../ui/Button.tsx`, `.../visuals/ProductMockup.tsx`, `.../index.html`.
