---
name: Auto Suggest a Purchase (ASAP)
description: Automated material suggestion management for public libraries
colors:
  primary: "#007bff"
  primary-dark: "#0056b3"
  success: "#19692c"
  success-hover: "#145523"
  danger: "#b42318"
  warning: "#ffda6a"
  warning-text: "#664d03"
  neutral-bg: "#f5f5f5"
  neutral-surface: "#ffffff"
  neutral-text: "#212529"
  neutral-muted: "#6c757d"
  border: "#dee2e6"
typography:
  display:
    fontFamily: "Roboto, -apple-system, sans-serif"
    fontSize: "1.7rem"
    fontWeight: 800
    lineHeight: 1.15
  headline:
    fontFamily: "Roboto, -apple-system, sans-serif"
    fontSize: "1.5rem"
    fontWeight: 500
    lineHeight: 1.2
  title:
    fontFamily: "Roboto, -apple-system, sans-serif"
    fontSize: "1.25rem"
    fontWeight: 600
    lineHeight: 1.2
  body:
    fontFamily: "Roboto, -apple-system, sans-serif"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "Roboto, -apple-system, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 700
    lineHeight: 1.2
rounded:
  sm: "4px"
  md: "8px"
  lg: "12px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "16px"
  lg: "24px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.neutral-surface}"
    rounded: "{rounded.sm}"
    padding: "8px 16px"
  button-success:
    backgroundColor: "{colors.success}"
    textColor: "{colors.neutral-surface}"
    rounded: "{rounded.sm}"
    padding: "8px 16px"
  card:
    backgroundColor: "{colors.neutral-surface}"
    rounded: "{rounded.md}"
    padding: "20px"
---

# Design System: ASAP

## 1. Overview

**Creative North Star: "The Civic Registry"**

ASAP is designed as a high-density, professional utility that facilitates the complex workflow of library material acquisition. The aesthetic is institutional and dependable, drawing inspiration from high-end government and educational portals where trust and efficiency are paramount. It rejects the "playful" or "casual" trends of modern consumer SaaS in favor of a serious, expert-focused environment.

**Key Characteristics:**
- **Institutional Trust**: A solid foundation of deep blues and crisp neutrals.
- **Expert Efficiency**: High information density that respects the staff's workflow.
- **Tactile Confidence**: UI elements have clear boundaries, intentional shadows, and strong contrast.

## 2. Colors

The palette is anchored by "Civic Blue," a dependable and authoritative primary color that signifies the system's role as a public utility.

### Primary
- **Civic Blue** (#007bff): Used for primary actions, navigation, and brand identity.
- **Registry Deep Blue** (#0056b3): Used for hover states and active links to provide depth and contrast.

### Secondary
- **Workflow Green** (#19692c): Used for positive actions like creating suggestions or approving purchases.
- **Alert Yellow** (#ffda6a): Used for similar-request warnings and critical workflow signals.

### Neutral
- **Foundation Gray** (#f5f5f5): The primary background color, providing a neutral canvas.
- **Registry White** (#ffffff): Used for panels, cards, and interactive surfaces.
- **Ink Black** (#212529): The primary text color for maximum legibility.
- **Muted Slate** (#6c757d): Used for secondary labels and hints.

### Named Rules
**The Authority Rule.** The primary Civic Blue is reserved for meaningful actions and identity. It should never be used decoratively on non-interactive elements.

## 3. Typography

The system uses a clean, modern sans-serif stack led by Roboto to ensure legibility across various devices and screen sizes.

**Display Font:** Roboto (with -apple-system fallback)
**Body Font:** Roboto (with -apple-system fallback)

### Hierarchy
- **Display** (800, 1.7rem, 1.15): Used for high-level numbers in analytics and large headings.
- **Headline** (500, 1.5rem, 1.2): Used for section titles in the staff dashboard.
- **Title** (600, 1.25rem, 1.2): Used for card titles and modal headings.
- **Body** (400, 1rem, 1.5): The standard for all readable text.
- **Label** (700, 0.875rem, 1.2): Used for table headers, form labels, and small buttons.

## 4. Elevation

The system uses a mix of tonal layering and soft shadows to provide depth and hierarchy, ensuring that the "Civic Registry" feels like a layered physical object.

### Shadow Vocabulary
- **Surface Elevation** (0 4px 12px rgba(0, 0, 0, 0.1)): Used for the main app container and modals to separate them from the foundation.
- **Interactive Lift** (0 2px 8px rgba(0, 0, 0, 0.04)): Used for smaller cards and settings panels.

### Named Rules
**The Shadow-of-State Rule.** Shadows should primarily appear on elevated surfaces (modals, app containers) or as a response to interaction, never as a decorative default for every element.

## 5. Components

Components are "Tactile and Confident," featuring clear borders and robust shapes.

### Buttons
- **Shape:** Rounded Small (4px radius)
- **Primary:** Civic Blue (#007bff) with standard padding (8px 16px).
- **Success:** Workflow Green (#19692c) for "New Suggestion" or approval actions.

### Cards / Containers
- **Corner Style:** Rounded Medium (8px radius)
- **Background:** Registry White (#ffffff)
- **Border:** 1px #dee2e6 to provide clear containment.

### Inputs / Fields
- **Style:** 1px #dee2e6 border, white background.
- **Focus:** Blue border shift to indicate active state.

## 6. Do's and Don'ts

### Do:
- **Do** use Roboto for all text to maintain institutional consistency.
- **Do** keep information density high in staff views to support expert workflows.
- **Do** ensure 4px border-radius on interactive elements for a "Tactile" feel.

### Don't:
- **Don't** use overly round or "bubbly" UI components (anti-reference: playful).
- **Don't** use neon or high-chroma accent colors that conflict with "Civic Blue."
- **Don't** use border-left greater than 1px as a colored stripe on cards.
