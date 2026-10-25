import { colorsTuple, type MantineThemeOverride } from '@mantine-vue/core'

// ---------------------------------------------------------------------------
// Sofia palette — grey canvas, brown accent, beige detail.
//
// The brand logo is a warm watercolour, but the UI keeps the canvas neutral so
// the brown reads as a deliberate accent rather than a tint over everything.
// Beige survives only as a small detail (reasoning blocks, subtle highlights).
// ---------------------------------------------------------------------------

/** Warm brown — the primary/brand accent. `brown.4` is the hero tone. */
export const brown = colorsTuple([
  '#F1E7DE', // 0
  '#E0C6AC', // 1
  '#CBA484', // 2
  '#B98A62', // 3
  '#A9744F', // 4 — hero
  '#91603F', // 5
  '#784E32', // 6
  '#613F27', // 7
  '#4B311E', // 8
  '#362315', // 9
])

/** Light beige — kept as a *detail* only (reasoning blocks, hairline accents). */
export const sand = colorsTuple([
  '#FBF6EC', '#F5EDDC', '#EFE2CA', '#E4D3AC', '#D6C28F',
  '#C6B178', '#B49D63', '#9C854F', '#7F6B3E', '#635330',
])

/** Neutral grey ramp (replaces Mantine's cool `dark`). 0 = text, 9 = deepest. */
export const neutral = colorsTuple([
  '#E9E9EC', // 0 — primary text on dark
  '#BDBDC2', // 1
  '#94949B', // 2
  '#6E6E76', // 3
  '#4C4C54', // 4
  '#38383F', // 5
  '#2B2B31', // 6
  '#212125', // 7
  '#191A1D', // 8
  '#121215', // 9
])

/** Neutral grey ramp (replaces Mantine's `gray`) for muted text and borders. */
export const slate = colorsTuple([
  '#F2F2F4', '#D8D8DC', '#BBBBC1', '#9A9AA2', '#7B7B84',
  '#606069', '#494951', '#37373E', '#29292F', '#1B1B20',
])

const fontFamily =
  '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif'

const fontFamilyMonospace =
  '"SFMono-Regular", Menlo, Consolas, "Liberation Mono", monospace'

/**
 * Mantine theme override. `dark` and `gray` are redefined as neutral greys and
 * `brown` becomes the primary. `autoContrast` keeps text readable on fills.
 */
export const sofiaTheme: MantineThemeOverride = {
  colors: {
    brown,
    sand,
    dark: neutral,
    gray: slate,
  },
  primaryColor: 'brown',
  primaryShade: { light: 3, dark: 3 },
  autoContrast: true,
  luminanceThreshold: 0.3,
  defaultRadius: 'md',
  fontFamily,
  fontFamilyMonospace,
  headings: {
    fontFamily,
    fontWeight: '600',
  },
  defaultGradient: {
    from: 'brown.3',
    to: 'brown.5',
    deg: 135,
  },
  other: {
    // Soft brown wash used for active rows / selected states.
    primarySoft: 'rgba(169, 116, 79, 0.16)',
  },
}
