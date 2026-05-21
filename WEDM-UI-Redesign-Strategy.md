# WEDM Enterprise UI/UX Redesign Strategy
**WebLogic Enterprise Deployment Manager — Complete GUI Architecture Rebuild**
*Version 1.0 — Pre-Implementation Analysis*

---

## Table of Contents

1. [Full UI/UX Architecture Analysis](#1-full-uiux-architecture-analysis)
2. [Current UX Problems List](#2-current-ux-problems-list)
3. [New Navigation Architecture](#3-new-navigation-architecture)
4. [Theme System Design](#4-theme-system-design)
5. [Layout System Design](#5-layout-system-design)
6. [Design System Proposal](#6-design-system-proposal)
7. [Runtime Dashboard Wireframe](#7-runtime-dashboard-wireframe)
8. [Runtime Management Wireframe](#8-runtime-management-wireframe)
9. [Log Viewer Wireframe](#9-log-viewer-wireframe)
10. [Installation Flow Redesign](#10-installation-flow-redesign)
11. [WPF Architecture Improvements](#11-wpf-architecture-improvements)
12. [Performance Strategy](#12-performance-strategy)
13. [Refactor Strategy](#13-refactor-strategy)
14. [Migration Plan](#14-migration-plan)
15. [Risks and Technical Debt Analysis](#15-risks-and-technical-debt-analysis)

---

## 1. Full UI/UX Architecture Analysis

### 1.1 Current Shell Structure

```
Window (WindowStyle=None, 1280×780, MinWidth=1100)
├─ Row 0 [48px]  — Custom Title Bar
│   ├─ Logo + "WebLogic Enterprise Deployment Manager" title
│   ├─ CENTER: "ENTERPRISE MIDDLEWARE AUTOMATION" label + ReleaseChannel badge
│   └─ RIGHT: ⚙ Management toggle | Theme toggle | Min/Max/Close
│
├─ Row 1 [*]     — Main Content (two layouts stacked via ZIndex)
│   ├─ ZIndex 0: Sidebar (240px fixed) + Content Panel
│   │   ├─ Sidebar: "INSTALLATION STEPS" header + step ItemsControl + footer credit
│   │   └─ Content: Step header banner | ScrollViewer ContentControl | 4px progress bar
│   └─ ZIndex 10: RuntimeDashboardView (shown/hidden via BoolToVisibilityConverter)
│
└─ Row 2 [44px]  — Footer Nav Bar
    ├─ LEFT: 📘 Oracle Documentation | About WEDM
    └─ RIGHT: ← Back | Next → / 🚀 Deploy / 📋 Review & Plan / Finish
```

### 1.2 Navigation Model

The application uses a **linear wizard model** exclusively. Navigation is implemented as:

- A `Steps` observable collection bound to the sidebar ItemsControl
- `CurrentStep` bound to a `ContentControl` whose content is resolved via typed `DataTemplates`
- 20+ DataTemplates registered in `Window.Resources`, mapping ViewModel types to UserControl types
- `BackCommand` / `NextCommand` / `NavigateToCommand(stepIndex)` on `MainWindowViewModel`
- The sidebar dynamically repopulates for Deploy, Migrate, and Decommission workflows
- The sidebar header always reads "INSTALLATION STEPS" regardless of active workflow

**Critical gap:** There is no persistent application-level navigation. The only non-wizard surface is the Runtime Management panel — a full-screen ZIndex:10 overlay toggled from the title bar. There is no Dashboard, no Reports section, no Settings, no Log Archive viewer as independently accessible screens.

### 1.3 Theme System

Two `ResourceDictionary` files: `Theme.Dark.xaml` and `Theme.Light.xaml`. The App.xaml merges one at startup and swaps via `ToggleThemeCommand` in `MainWindowViewModel`.

**Dark theme token inventory:**

| Token                  | Value     | Issue |
|------------------------|-----------|-------|
| BackgroundDeep         | `#0F172A` | Very dark navy — 97% perceived darkness |
| BackgroundPanel        | `#111827` | Barely distinguishable from Deep |
| BackgroundCard         | `#111827` | **Same as Panel** — zero differentiation |
| BackgroundElevated     | `#1F2937` | 8pt brighter, the only real step up |
| BorderSubtle           | `#1F2937` | **Same as Elevated** — borders invisible on elevated surfaces |
| BorderDefault          | `#374151` | Only truly visible border |
| SuccessSubtle          | `#14532D` | Forest-black — nearly invisible on dark bg |
| WarningSubtle          | `#422006` | Near-black brown |
| DangerSubtle           | `#450A0A` | Near-black red |
| AccentSubtle           | `#1E3A5F` | Deep navy — acceptable but dark |

**Structural problems:** Panel and Card share the same color (`#111827`), so card-in-panel layouts have no visual hierarchy. BorderSubtle is identical to BackgroundElevated, so subtle borders on elevated surfaces are invisible. The three semantic "subtle" background variants (Success/Warning/Danger) are so dark they offer almost no contrast with the panel background — status badges on dark backgrounds are barely readable.

### 1.4 Typography System

| Token       | Size | Weight   | Use |
|-------------|------|----------|-----|
| TypeDisplay | 22px | SemiBold | Hero / splash (unused outside Migration metric values) |
| TypeH1      | 18px | SemiBold | Step titles, card titles |
| TypeH2      | 15px | SemiBold | Section headers |
| TypeH3      | 14px | SemiBold | Card sub-headers |
| TypeBody    | 13px | Regular  | Content text |
| TypeCaption | 11px | Regular  | Helper text, labels |
| TypeMono    | 12px | Regular  | Log/path/command output |
| TypeLabel   | 12px | SemiBold | Form field labels |
| TypeAccent  | 13px | SemiBold | Branded text |
| TypeBadge   | 11px | Bold     | Version badges |

**Issues:** The H1–H3 range (14–18px) is too compressed for enterprise information density. A management tool like vCenter uses a wider scale (12–20px) with cleaner semantic separation. The `TypeDisplay` style exists but is only used for migration metric values — not for meaningful section heading hierarchy. There are no dedicated "metric" or "stat" text styles for dashboard numbers. TypeH3 uses `TextSecondaryBrush` which is the wrong color for a heading.

### 1.5 Button System

| Style         | Height | Padding    | Background            | Issue |
|---------------|--------|------------|-----------------------|-------|
| BtnBase       | 36px   | 16,0       | —                     | Good baseline |
| BtnPrimary    | 36px   | 16,0       | AccentPrimary         | Fine |
| BtnSecondary  | 36px   | 16,0       | BackgroundElevated    | Fine |
| BtnDanger     | 36px   | 16,0       | `#2D0F0E` hardcoded  | **Breaks theming** |
| BtnSuccess    | 36px   | 16,0       | SuccessSubtle         | Nearly invisible bg |
| BtnGhost      | 36px   | 16,0       | Transparent           | Fine |
| BtnPrimaryLarge | 48px | 28,0      | AccentPrimary         | Too large for enterprise density |
| BtnIcon       | 36×36  | 0          | Transparent           | Fine |
| BtnVersionCard | auto  | 20,16      | BackgroundCard        | 20,16 padding → enormous cards |

**Critical issues:** `BtnDanger` uses hardcoded `#2D0F0E` for background — this doesn't swap on light theme. `BtnPrimaryLarge` at 48px forces large visual weight that breaks enterprise density. `BtnVersionCard` at padding 20,16 creates operation cards of 280px+ height in `OperationSelectionView`.

### 1.6 Input Controls

TextBox/PasswordBox/ComboBox share a consistent 32px MinHeight, 8,6 padding, 4px CornerRadius. Focus state correctly uses a 2px border with `InputBorderFocusBrush`. All use `DynamicResource` for theme tokens — correctly implemented. CheckBox template is custom with 16×16 box and accent checkmark. The ComboBox has a proper hit-test separation between the visual chrome border and the transparent ToggleButton overlay.

### 1.7 Animation Library

Four storyboards: `FadeIn` (0.25s opacity), `SlideInRight` (0.3s translate+fade), `Pulse` (1s opacity repeat), `Spin` (1s rotation repeat). No transitions for state changes, no ripple effects, no progress animations beyond the text `⟳` spinner. Animations are triggered manually via EventTriggers or code-behind — not declarative state transitions. The content area `ContentControl` uses a `TranslateTransform` but no animation storyboard wires to it from the template.

### 1.8 View Layer Inventory

**Wizard views (Deploy workflow):** OperationSelection, Welcome, ProductSystemHealth, VersionSelection, PathConfig, DatabaseConfig, DomainConfig, PatchManagement, SecurityCompliance, Prerequisite, DeploymentSummary, DeploymentProgress

**Migration views:** MigrationSourceVersion, MigrationTargetVersion, MigrationDiscovery, MigrationCompatibility, MigrationStrategy, MigrationValidation, MigrationTransformation, MigrationSummary, MigrationExecution

**Decommission views:** DecommissionScope, DecommissionDiscovery, DecommissionPreview, DecommissionSummary, DecommissionProgress

**Runtime views:** RuntimeDashboardView (new, overlaid)

**Dialogs:** AboutWindow, SplashWindow

**Total:** 28 view files. All UserControls except the dialogs. No standalone pages, no navigation frames.

---

## 2. Current UX Problems List

### P-01 — Single Wizard Paradigm Traps All Functionality

**Severity: Critical.** The application has only one navigation model: a linear wizard with Back/Next. Management tasks (Runtime, Logs, Reports, Settings) do not have their own dedicated screens — they are either absent or hacked in as title-bar overlays. A tool at the scale of WEDM (multi-domain, multi-workflow, runtime management) demands a persistent navigation shell, not a wizard.

**Evidence:** Runtime Management is a `Panel.ZIndex=10` overlay on `Grid.Row[1]`. When shown, the wizard content disappears entirely beneath it. There is no way to view logs, reports, or settings as independent areas. The only non-wizard navigation is the hidden title-bar button.

### P-02 — Sidebar Identity Crisis

**Severity: High.** The sidebar header always reads "INSTALLATION STEPS" regardless of whether the active workflow is Deploy, Migrate, or Decommission. The sidebar is purely a wizard step navigator — it has no persistent application-level value when no workflow is active. On first launch, before any workflow is selected, the sidebar is empty and misleading.

### P-03 — Dark Theme Has Near-Zero Color Hierarchy

**Severity: High.** `BackgroundPanel` (#111827) and `BackgroundCard` (#111827) are identical. `BorderSubtle` (#1F2937) equals `BackgroundElevated` (#1F2937). The three "subtle" semantic colors (SuccessSubtle #14532D, WarningSubtle #422006, DangerSubtle #450A0A) are near-black and provide virtually no visual differentiation. Status badges, prerequisite stat cards, and state indicators in the Runtime Dashboard are barely readable.

### P-04 — Operation Selection Cards are Excessively Large

**Severity: High.** The `OperationSelectionView` uses `UniformGrid Columns=3` with cards that have `MinHeight=280`, `Padding="28,32"`, emoji icons at `FontSize=44`. On a 780px window with 48+44px chrome = 688px content height, minus step header (~60px) = ~628px, these cards consume the entire viewport. The card-based selector is a valid pattern, but the sizing is appropriate for a marketing landing page, not an enterprise tool. The 44px emoji icon is particularly jarring.

### P-05 — BtnDanger Uses Hardcoded Dark Color

**Severity: Medium.** `BtnDanger` Background is `#2D0F0E` (hardcoded). On light theme this will be nearly black text-on-dark — catastrophically unreadable. The same hardcoded color appears in `PrerequisiteView.xaml` at line 72 for the error count stat card.

### P-06 — Runtime Management is Not Discoverable

**Severity: Medium.** The "⚙ Management" toggle button is placed in the title bar between the theme toggle and window controls. This is not an expected location for application feature access. Users coming from vCenter/Portainer/Azure Portal expect persistent sidebar navigation to switch between operational modes.

### P-07 — Log Viewer Does Not Exist as a Screen

**Severity: Medium.** There is no dedicated log viewer. Deployment logs appear inside `DeploymentProgressView` as a `ListBox` with no virtualization. Runtime component logs from `LogTailService` have no UI at all yet. Log archive, search, filtering, and export are absent.

### P-08 — Footer Navigation Bar Wastes 44px Always

**Severity: Medium.** The 44px footer with Back/← and Next/→ is always present, including on the OperationSelection screen where it could be replaced with card-click navigation. On Progress screens where Back/Next are meaningless, the footer still occupies 44px. In a management tool where vertical space is at a premium, permanent chrome that is contextually irrelevant is waste.

### P-09 — Title Bar is 48px with Redundant Marketing Copy

**Severity: Low-Medium.** The 48px title bar contains "ENTERPRISE MIDDLEWARE AUTOMATION" in muted text in the center — a marketing tagline that serves no functional purpose. This occupies the center third of a constrained chrome bar. Reducing this to 36px and removing the tagline reclaims 12px plus improves focus.

### P-10 — No Settings, Reports, or Help Screens

**Severity: Medium.** There is no Settings screen. No Reports archive. No Help/Documentation screen. The "📘 Oracle Documentation" link in the footer opens an external browser. Reports exist only as paths shown in `DeploymentProgressView`. A proper enterprise tool exposes configuration, audit history, and contextual help as first-class screens.

### P-11 — Progress Screen Log ListBox has No Virtualization

**Severity: Medium (performance + UX).** `DeploymentProgressView` uses `<ListBox>` for live log output. ListBox with no `VirtualizingStackPanel` settings will hold every log entry in memory and render all items. For a deployment that emits thousands of log lines, this creates visible scroll lag and memory pressure.

### P-12 — TypeH3 Uses TextSecondary Color

**Severity: Low.** `TypeH3` sets `Foreground=TextSecondaryBrush` instead of `TextPrimaryBrush`. Heading styles should use primary text color — secondary color is for supporting content. This causes card sub-headers to appear dimmer than they should.

### P-13 — No Breadcrumb or Context Indicator

**Severity: Low-Medium.** During deep wizard workflows (e.g., Migration step 7 of 9), there is no breadcrumb trail showing where the user is in the overall application context. The step number in the sidebar provides linear position but not hierarchical context (e.g., "WEDM / Migrate / Compatibility Assessment").

### P-14 — Sidebar Width is Fixed at 240px

**Severity: Low.** The sidebar is 240px fixed, uncollapsible. In smaller window configurations or when working with wide content (wide DataGrid columns), the fixed sidebar wastes space that could be used for content. A collapsible sidebar with 48px icon-only mode is standard in enterprise tools.

### P-15 — Animation Wiring is Incomplete

**Severity: Low.** The `Animations.xaml` defines `SlideInRight` with a `TranslateTransform` target, and `MainWindow.xaml` defines a `TranslateTransform x:Name="ContentTranslate"` on the ContentControl, but no trigger wires the `SlideInRight` storyboard to step navigation events. The animation is declared but never fires.

### P-16 — No Keyboard Navigation Design

**Severity: Low-Medium.** There are no defined keyboard shortcuts. No `AccessText` on menu items. No `KeyBinding` on the Window. Enterprise tools targeting operators and DevOps engineers benefit significantly from keyboard-first operation (F5 = Refresh, Ctrl+Enter = Deploy, etc.).

---

## 3. New Navigation Architecture

### 3.1 Navigation Model: Application Shell with Activity Bar

Replace the single-wizard window with a persistent application shell inspired by VS Code and Azure Portal.

```
╔════════════════════════════════════════════════════════════════════╗
║  TITLE BAR [36px]   W  WEDM  v3.x  [ENV badge]    [─][□][✕]      ║
╠═══╦══════════════════════╦═══════════════════════════════════════════╣
║   ║  SECTION PANEL       ║  CONTENT AREA                            ║
║ A ║  [220px, collapsible]║                                          ║
║ C ╠══════════════════════╣                                          ║
║ T ║  Context-sensitive   ║  Main view for the active section        ║
║ I ║  navigation tree /   ║                                          ║
║ V ║  step navigator /    ║  (tabs, grids, forms, progress)          ║
║ I ║  domain tree         ║                                          ║
║ T ║                      ║                                          ║
║ Y ║                      ║                                          ║
║   ║                      ║                                          ║
║ B ╠══════════════════════╩═══════════════════════════════════════════╣
║ A ║  STATUS BAR [24px]  ● Connected  |  ENV: PROD  |  v3.x.x       ║
║ R ╚══════════════════════════════════════════════════════════════════╝
```

### 3.2 Activity Bar (48px fixed left column)

Six icon-only buttons with tooltips, stacked vertically. Active button gets an accent left-border indicator (2px). This follows the VS Code / JetBrains pattern exactly.

```
[48px]
  ▣   Dashboard      — System overview, recent ops, health summary
  ▶   Deploy/Install — Installation wizard (existing wizard content)
  ⟲   Migrate        — Migration workflow (existing migration content)
  ⚙   Runtime        — Runtime dashboard (replaces overlay approach)
  ≡   Logs           — Log viewer (new screen)
  ⊞   Reports        — Report archive (new screen)
  ···
  ⚙   Settings       — Configuration (new screen, at bottom)
```

### 3.3 Section Panel (220px, collapsible to 0 or icon-only)

Content changes based on active activity:

- **Dashboard:** Environment selector (domain list), quick-stats
- **Deploy/Install:** Wizard step navigator (current sidebar repurposed here)
- **Migrate:** Migration workflow step navigator
- **Runtime:** Domain tree (AdminServer → ManagedServers), component filter
- **Logs:** Log source list (component, deployment, system)
- **Reports:** Report list with dates/types
- **Settings:** Settings category list

Collapse toggle button at bottom of section panel. When collapsed, panel shrinks to 0 and content area expands. State persisted in user preferences.

### 3.4 Content Area

Full remaining space. Content is replaced based on active section + selection in the section panel.

For wizard-based sections (Deploy, Migrate), the content area hosts:
- Section content header (step title + description, replaces current step header banner)
- The current step UserControl
- Step-local action bar at bottom (replaces footer bar)

For non-wizard sections (Dashboard, Runtime, Logs, Reports):
- Section header with command bar
- Main content view

### 3.5 Status Bar (24px)

Persistent bottom bar with:
- Connection/environment indicator (left)
- Current domain/environment label (center)
- Version string (right)

Replaces the 44px footer navigation bar for all non-wizard screens. Wizard screens show a step action bar inside their content area instead.

### 3.6 Navigation State Machine

```
AppShell
  ├── ActiveSection: enum { Dashboard, Deploy, Migrate, Decommission, Runtime, Logs, Reports, Settings }
  ├── ActiveDomain: RuntimeDomainViewModel? (selected in Runtime section)
  └── ActiveWizardStep: WizardStepViewModel? (when in Deploy/Migrate/Decommission)
```

Navigation transitions are handled by `AppShellViewModel` (new, replaces `MainWindowViewModel` as the top-level coordinator). Each section has its own ViewModel that is instantiated once and cached — not re-created on navigation.

### 3.7 Deep-Linking / URL-Style Navigation

Internal navigation uses a lightweight route string for state restoration:
```
wedm://deploy/step/5
wedm://runtime/domain/base_domain/AdminServer
wedm://logs/component/AdminServer/live
```

This enables: "return to where I was after restart", log links from reports, deep-linking from alert notifications.

---

## 4. Theme System Design

### 4.1 Problems with Current Token Structure

The existing token set has structural flaws that cause color hierarchy collapse on dark theme:

1. **No Card/Panel separation:** Both use #111827
2. **BorderSubtle = BackgroundElevated:** Invisible borders on elevated surfaces
3. **Semantic subtle colors are near-black:** Too dark to communicate state meaningfully
4. **No Surface/Canvas distinction:** The "deep" background is for window chrome, but views sit on it — they need a proper "canvas" color between Deep and Panel

### 4.2 Proposed Dark Theme Token Redesign

**Background surface stack (5 levels):**
```
BackgroundCanvas     #0D1117    Window background — deepest layer
BackgroundSurface    #161B22    Sidebar, panels, card containers
BackgroundElevated   #1C2333    Cards, input fields, slightly raised elements
BackgroundOverlay    #21273A    Dropdowns, modals, floating panels
BackgroundHover      #263046    Hover state on interactive items
```

**Border stack (3 levels):**
```
BorderSubtle         #21273A    Separators within elevated surfaces (distinct from Elevated bg)
BorderDefault        #30363D    Normal borders — card outlines, input borders
BorderStrong         #484F58    Emphasis borders, focused input rings (before accent)
```

**Text stack (4 levels):**
```
TextPrimary          #E6EDF3    Main content text
TextSecondary        #8B949E    Supporting text, descriptions
TextMuted            #6E7681    Timestamps, metadata, labels
TextDisabled         #484F58    Disabled state text
```

**Semantic colors (brighter, perceptible subtle variants):**
```
SuccessColor         #3FB950    Bright green — status text, icons
SuccessSubtle        #1A3A26    Clearly green-tinted bg (not near-black)
SuccessBorder        #238636    Border around success cards

WarningColor         #D29922    Amber — warning text, icons
WarningSubtle        #3A2E00    Yellow-tinted — perceptible
WarningBorder        #9A6700    Border around warning cards

DangerColor          #F85149    Bright red — error text, icons
DangerSubtle         #3D0C0C    Red-tinted — clearly different from neutral
DangerBorder         #DA3633    Border around danger cards

InfoColor            #58A6FF    Bright blue — info text, links
InfoSubtle           #0D2045    Blue-tinted bg
InfoBorder           #1F6FEB    Border around info cards
```

**Accent (brand):**
```
AccentPrimary        #2F81F7    Primary action color (slightly brighter than current #3B82F6)
AccentPrimaryHover   #58A6FF    Hover state
AccentPrimaryActive  #1F6FEB    Pressed state
AccentSubtle         #12294E    Accent-tinted bg — clearly different from surface
AccentBorder         #1F6FEB    Accent-colored borders
```

### 4.3 Light Theme Redesign

Light theme uses the same semantic structure with appropriate light-mode values:

```
BackgroundCanvas     #F6F8FA    Window background
BackgroundSurface    #FFFFFF    Panels and content areas
BackgroundElevated   #F6F8FA    Input fields, slightly recessed areas
BackgroundOverlay    #FFFFFF    Dropdowns, modals
BackgroundHover      #F3F4F6    Hover states

BorderSubtle         #D0D7DE    Subtle separators
BorderDefault        #D0D7DE    Normal borders
BorderStrong         #8B949E    Emphasis/focus borders

TextPrimary          #1F2328
TextSecondary        #636C76
TextMuted            #8B949E

SuccessSubtle        #DAFBE1
WarningSubtle        #FFF8C5
DangerSubtle         #FFEBE9
InfoSubtle           #DDF4FF
```

### 4.4 Token Naming Convention

All tokens follow a strict `{Category}{Variant}Color` / `{Category}{Variant}Brush` pattern. No hardcoded hex values anywhere in view files. All semantic colors must exist in both themes. This enables theme swap without any view changes.

### 4.5 Theme Switching Architecture

Current approach: merge/unmerge ResourceDictionary at runtime. Keep this — it's the correct WPF approach. Improve by:

1. Moving the theme source selection to a `ThemeManager` singleton that can be injected
2. Persisting the selected theme in user settings (currently session-only)
3. Adding a `PreferSystemTheme` option that reads Windows accent/dark-mode settings via registry

---

## 5. Layout System Design

### 5.1 Chrome Measurements

```
ActivityBar width:      48px  (was: N/A — no activity bar existed)
SectionPanel width:     220px (was: 240px sidebar)
TitleBar height:        36px  (was: 48px — save 12px)
StatusBar height:       24px  (was: 44px footer nav — save 20px)
```

Net vertical gain: **32px** of content area height per redesign (12px title reduction + 20px footer reduction).

On a 780px window: before = 780 - 48 - 44 = 688px content. After = 780 - 36 - 24 = 720px content. **+32px (~4.6%).**

### 5.2 Content Area Padding

Standardized padding system using multiples of 4px:

```
xs:   4px
sm:   8px
md:   12px
lg:   16px
xl:   20px
2xl:  24px
3xl:  32px
```

Content area horizontal padding: `lg` (16px) left+right.
Section header: `lg` vertical padding.
Card internal padding: `md` or `lg` depending on card density.
Form field vertical spacing: `md` between fields, `lg` between groups.

### 5.3 Grid System for Dashboards

Dashboard-style screens (Dashboard overview, Runtime) use a 12-column implicit grid via `UniformGrid` or `WrapPanel` with fixed column counts. Stat cards occupy 3 columns each (4 per row at 1280px minus chrome). Detail views use a master/detail split: 40% left / 60% right, or 320px fixed left / * right.

### 5.4 Sidebar Section Panel

```
SectionPanelWidth:   220px (collapsed: 0px, icon-only future option: 48px)
SectionHeader:       height 40px, padding 12,8
SectionItem:         height 32px, padding 12,0,12,0 (compact)
SectionItem active:  2px left accent border + BackgroundHover
SectionItemNested:   height 28px, padding 20,0,12,0 (tree indent)
```

Compared to current sidebar step items (Padding=16,10, Margin=8,2 → ~52px effective), new section items at 32px represent a **38% height reduction** — fitting more context into the same space.

### 5.5 Wizard Step Content Layout

Inside Deploy/Migrate wizards:

```
SectionHeader [40px]   — Step title + description
StepActionBar [48px]   — Back + Next/Deploy, inside content area bottom
Content [*]            — ScrollViewer with step UserControl
```

The step action bar moves from the global footer into the content area. This means non-wizard sections don't see the Back/Next bar at all. The 48px step action bar is slightly taller than the 44px footer because it needs to contain the primary Deploy CTA with adequate visual weight.

### 5.6 Operation Card Redesign

`OperationSelectionView` cards redesigned for information density:

```
Old: MinHeight=280, Padding=28,32, Emoji 44px icon, UniformGrid Columns=3
New: MinHeight=120, Padding=16,14, Icon 20px (Segoe MDL2 or simple Path), WrapPanel or UniformGrid
```

Cards at 120px MinHeight fit 3 side-by-side in a 200px content height — completely comfortable. At the new dimensions a user can see all three options without scrolling even on compact displays.

---

## 6. Design System Proposal

### 6.1 Icon Strategy

Current: Unicode emoji characters (`🚀`, `🔄`, `🧹`, `⟳`, `✔`, `✕`). These are inconsistent, platform-dependent, and cannot be color-controlled.

**Replace with:**
- Segoe MDL2 Assets (already available on Windows) for standard icons
- Custom `Path` geometries for domain-specific icons
- Single icon size system: 12px (status dots), 16px (list icons, badges), 20px (action buttons), 24px (navigation), 32px (section cards)

No emoji anywhere except strictly decorative splash/welcome contexts.

### 6.2 State Indicator Design

Runtime state badges redesigned:

```
Running:   [●] green dot + "Running"    — SuccessColor text, SuccessSubtle bg
Starting:  [◌] pulsing accent dot + "Starting" — AccentPrimary text, AccentSubtle bg
Stopping:  [◌] amber dot + "Stopping"   — WarningColor text, WarningSubtle bg
Stopped:   [○] muted dot + "Stopped"   — TextMuted text, BackgroundElevated bg
Failed:    [✕] red icon + "Failed"     — DangerColor text, DangerSubtle bg
Unhealthy: [!] amber icon + "Unhealthy"— WarningColor text, WarningSubtle bg
```

Status dots should be `Ellipse` elements (8px), not `TextBlock` unicode characters. This allows color binding without FontSize complications.

### 6.3 Card Variants

Four card types with standardized styles:

```
CardFlat:    BorderThickness=1, BorderDefault, bg=BackgroundSurface, Padding=lg, Radius=6
CardElevated: BorderThickness=1, BorderDefault, bg=BackgroundElevated, Padding=lg, Radius=6, drop-shadow (effect)
CardSuccess: BorderThickness=1, SuccessBorder, bg=SuccessSubtle, Padding=lg, Radius=6
CardWarning: BorderThickness=1, WarningBorder, bg=WarningSubtle, Padding=lg, Radius=6
CardDanger:  BorderThickness=1, DangerBorder,  bg=DangerSubtle,  Padding=lg, Radius=6
CardInfo:    BorderThickness=1, InfoBorder,     bg=InfoSubtle,    Padding=lg, Radius=6
```

Card backgrounds correctly use the redesigned subtle colors (perceptible on dark theme).

### 6.4 Data Table / DataGrid Style

```
HeaderBackground:   BackgroundElevated
HeaderForeground:   TextMuted, FontSize=11, FontWeight=SemiBold, AllCaps
RowHeight:          34px (was 38px — denser)
AlternatingRow:     BackgroundCanvas (was BackgroundCard — now distinct from row)
GridLines:          Horizontal only, BorderSubtle brush
SelectedRow:        AccentSubtle bg + AccentBorder left accent strip
CellPadding:        10,0 horizontal
```

### 6.5 Form Layout Standards

Form groups use a standardized `FormGroup` border pattern:
- Group border with `BorderSubtle`, CornerRadius=6
- Group header at top: TypeLabel (12px SemiBold) with padding 12,8
- Field rows: label above field, 4px gap, 12px between field rows

```
[Group Header: "Server Configuration"  ]
 [Label: Port Number                   ]
 [TextBox: _____________ ]
 [Caption: Defaults to 7001 for Admin  ]
 
 [Label: Domain Home Path              ]
 [TextBox: _____________ ] [Browse]
```

### 6.6 Progress Indicators

Three types:
- **Determinate progress bar:** Thin (4px, current) for overall flow; standard (8px) for step-level within a card
- **Indeterminate spinner:** `Ellipse`-based arc spinner using a Storyboard RotateTransform (replace `⟳` text)
- **Status pulse dot:** 8px Ellipse with `Pulse` storyboard applied when in "Starting/Stopping" state

### 6.7 Notification / Alert Banners

Inline alert component with semantic variants (Success/Warning/Danger/Info). Structure:

```
[Icon 16px] [Title (TypeLabel)]   [✕ dismiss]
            [Message (TypeCaption)]
```

Applied padding: md. Replaces the current ad-hoc `Border` with hardcoded background colors per-view.

---

## 7. Runtime Dashboard Wireframe

The Runtime Dashboard is the primary monitoring screen, accessed via the Runtime activity bar section. It replaces the full-screen overlay model with a proper first-class screen.

```
╔═══════════════════════════════════════════════════════════════════╗
║ ACTIVITY  │ SECTION PANEL (Runtime)  │ CONTENT AREA              ║
║  BAR      │                          │                            ║
║           │ ▼ Domains                │ Runtime Overview           ║
║ [▣]       │   ├ base_domain ●        │                            ║
║ [▶]       │   │  ├ AdminServer ●     │  ┌──────┐ ┌──────┐ ┌────┐ ║
║ [⟲]       │   │  ├ WLS_SOA1   ●     │  │  2   │ │  0   │ │ 1  │ ║
║ [⚙] ←    │   │  └ WLS_SOA2   ●     │  │Runnng│ │Failed│ │Strt│ ║
║ [≡]       │   └ fraud_domain  ⚠     │  └──────┘ └──────┘ └────┘ ║
║ [⊞]       │     ├ AdminServer ⚠     │                            ║
║           │     └ WLS_FRAUD1  ●     │  Component Grid            ║
║           │                          │  ┌───────────────────────┐ ║
║           │  [+ Discover]            │  │ DOMAIN  COMPONENT  STA│ ║
║           │  [⟳ Refresh]             │  │base_dom AdminServer ● R│ ║
║           │                          │  │base_dom WLS_SOA1    ● R│ ║
║           │  ● Auto  10s             │  │base_dom WLS_SOA2    ● R│ ║
║           │                          │  │fraud_d  AdminServer ⚠ U│ ║
║           │                          │  │fraud_d  WLS_FRAUD1  ● R│ ║
║           │                          │  └───────────────────────┘ ║
║           │                          │                            ║
║           │                          │  [▶ Start][■ Stop][↺ Restart]
╠═══════════╧══════════════════════════╧════════════════════════════╣
║ ● Connected │ ENV: PROD │ base_domain  │  WEDM v3.x               ║
╚═══════════════════════════════════════════════════════════════════╝
```

**Key design decisions:**

- Domain tree lives in the section panel — clicking a domain filters the grid to that domain's components; clicking root "Domains" shows all
- Three stat cards at top: Running count, Failed count, Starting/Stopping count — live-updating
- DataGrid below stat cards: virtualized, sortable, all domains unless filtered
- Action bar at the bottom of the content area: Start/Stop/Restart enabled only when a compatible component is selected
- Auto-refresh toggle moved to section panel (less prominent than a toolbar button)
- No full-screen busy overlay — use inline row animation (row opacity 0.5 + spinner in State column) during operations
- Credentials pane shown as a collapsible panel below the grid (hidden by default, expands when Start/Stop selected)

---

## 8. Runtime Management Wireframe

The detail panel when a single component is selected (split-view or side-panel pattern):

```
Component Grid [60% width]  │  Detail Panel [40% width]
                             │
 DOMAIN  COMPONENT  STATE   │  ┌─────────────────────────────┐
 base_d  AdminSrv   ● RUN ← │  │ AdminServer                 │
 base_d  WLS_SOA1   ● RUN   │  │ base_domain                 │
 base_d  WLS_SOA2   ● RUN   │  ├─────────────────────────────┤
                             │  │ State    ● Running          │
                             │  │ PID      14823              │
                             │  │ Port     7001               │
                             │  │ Uptime   2d 14h 33m         │
                             │  │ Health   ✓ Healthy          │
                             │  │ TCP      ✓ 7001 reachable   │
                             │  │ HTTP     ✓ /console 302     │
                             │  ├─────────────────────────────┤
                             │  │ [▶ Start]   [■ Stop]        │
                             │  │ [↺ Restart] [≡ View Logs]   │
                             │  ├─────────────────────────────┤
                             │  │ Credentials (optional)      │
                             │  │ User: [______] Pwd: [••••]  │
                             │  ├─────────────────────────────┤
                             │  │ Last Operation              │
                             │  │ ✔ Stopped  14:23:01        │
                             │  │ ▶ Started  14:23:45        │
                             │  └─────────────────────────────┘
```

**Key design decisions:**

- Detail panel slides in from right (SlideInRight storyboard properly wired) when a row is selected
- Health check results are shown as three separate lines (Process/TCP/HTTP) not a single aggregate string
- "View Logs" button navigates to the Logs section with the component pre-selected
- Credentials field only visible in detail panel — not in the main grid toolbar
- Operation history (last 5 ops) shown at bottom of detail panel
- Panel dismisses on Escape or clicking another row

---

## 9. Log Viewer Wireframe

New first-class screen accessible from the Logs activity bar button.

```
╔═══════════════════════════════════════════════════════════════════╗
║ ACTIVITY  │ SECTION PANEL (Logs)    │ CONTENT AREA               ║
║  BAR      │                         │                             ║
║           │ ▼ Runtime Logs          │  ┌─ Search/Filter bar ────┐ ║
║ [▣]       │   ├ AdminServer    LIVE │  │ 🔍 [filter text___] ↓  │ ║
║ [▶]       │   ├ WLS_SOA1      LIVE │  │ Level: [ALL ▾] Date:[__]│ ║
║ [⟲]       │   └ WLS_SOA2      OFF  │  └───────────────────────────┘ ║
║ [⚙]       │                         │                             ║
║ [≡] ←    │ ▼ Deployment Logs       │  [LIVE ●] AdminServer log  ║
║ [⊞]       │   ├ deploy_20260520     │  ┌─────────────────────────┐ ║
║           │   ├ deploy_20260519     │  │14:23:01 INFO  Server st │ ║
║           │   └ deploy_20260501     │  │14:23:05 INFO  Listening  │ ║
║           │                         │  │14:23:07 WARN  Memory thr │ ║
║           │ ▼ System Logs           │  │14:24:01 ERROR BEA-123456 │ ║
║           │   └ wedm_system.log     │  │14:24:02 ERROR Exception  │ ║
║           │                         │  └─────────────────────────┘ ║
║           │                         │  [↓ Tail] [⏸ Pause] [⬇ Exp]║
╠═══════════╧═════════════════════════╧══════════════════════════════╣
║ ● Connected │ 1,247 lines shown │ Tailing: AdminServer           ║
╚═══════════════════════════════════════════════════════════════════╝
```

**Implementation details:**

- **Log output control:** Replace `ListBox` with a custom `VirtualizingPanel`-backed `ItemsControl` or a `RichTextBox` in append-only mode. The control MUST use virtualization — target 50,000+ lines without lag.
- **Live tail toggle:** Automatically scrolls to bottom when enabled. "Tail" button flips between following and free-scroll.
- **Filter bar:** Real-time text filter (TypeBody text contains match), level dropdown (ALL/DEBUG/INFO/WARN/ERROR/FATAL), time range picker.
- **Color coding:**
  - DEBUG: TextMuted
  - INFO: TextSecondary
  - WARN: WarningColor
  - ERROR/FATAL: DangerColor
  - BEA-codes: InfoColor (distinct — Oracle-specific)
- **Line format:** `[HH:mm:ss.fff] [LEVEL] [source] message` — monospace (TypeMono)
- **Export button:** Saves filtered view to `.txt` or `.log` in workspace folder
- **Section panel:** Clicking a source selects it; "LIVE" badge pulses green when actively tailing; "OFF" when component stopped or tail cancelled

---

## 10. Installation Flow Redesign

### 10.1 Operation Selection Screen

Replace the 280px-tall card grid with a compact horizontal card strip:

```
┌─────────────────────────────────────────────────────────────────┐
│  What would you like to do?                                     │
├─────────────────────────────────────────────────────────────────┤
│ ┌────────────────────────┐ ┌───────────────────┐ ┌──────────┐  │
│ │ ▶  Deploy              │ │ ⟲  Migrate        │ │ ⊗  Remove│  │
│ │   New WebLogic env     │ │   Upgrade existing│ │  Env     │  │
│ │   [Full wizard]        │ │   [Migration flow]│ │  [Decomm]│  │
│ └────────────────────────┘ └───────────────────┘ └──────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

Card height: 90px. Emoji removed. Icons from Segoe MDL2 or Path data. No emoji `FontSize=44` — use 20px `Path` geometry instead. Cards horizontally scrollable if window narrows below threshold.

### 10.2 Wizard Step Header (inside content area)

```
┌─ Step 3 of 9 ─────────────────────────────────────────────────┐
│  ▣  System Readiness                                            │
│     Check prerequisites before installation begins             │
│  ████████████░░░░░░░░░░░░░░░░  33%                            │
└────────────────────────────────────────────────────────────────┘
```

- Step counter visible in header (was only in sidebar)
- Progress bar at 6px height (slightly thicker than current 4px for readability)
- Step icon rendered as a Path or Segoe MDL2 glyph — not emoji

### 10.3 Step Action Bar (bottom of content, inside wizard)

```
┌────────────────────────────────────────────────────────────────┐
│  ← Back               [Validation status here]     Next →      │
└────────────────────────────────────────────────────────────────┘
```

Height: 48px. Only shown in wizard sections. Global status bar (24px) still visible below it.

### 10.4 Prerequisite Screen Redesign

Stat cards redesigned to use theme tokens:

```
┌───────────────────────────────────────────────────────────────┐
│ System Readiness  ● 12 passed  ⚠ 2 warnings  ✕ 0 errors      │
│                                            [Run Checks]        │
├───────────────────────────────────────────────────────────────┤
│ ┌──────────┐  ┌──────────┐  ┌──────────┐                      │
│ │  ✓  12   │  │  ⚠   2  │  │  ✕   0  │                      │
│ │  Passed  │  │ Warnings │  │  Errors  │                      │
│ └──────────┘  └──────────┘  └──────────┘                      │
├───────────────────────────────────────────────────────────────┤
│ [Check] [Check Name      ] [Result             ] [Remediation ]│
│  ✓     Oracle Inventory    Found at C:\oraInventory            │
│  ✓     JDK Version         1.8.0_361 ≥ 1.8.0_211              │
│  ⚠     JAVA_HOME           Mismatched — auto-repair available  │
└───────────────────────────────────────────────────────────────┘
```

Stat card backgrounds use proper theme tokens: SuccessSubtle, WarningSubtle, DangerSubtle (redesigned to be perceptible). No hardcoded `#2D0F0E`.

### 10.5 Deployment Progress Screen Redesign

```
┌─ Deploying: Step 6/8 — Install WebLogic ──────────────────────┐
│  ████████████████████░░░░  75%   Elapsed: 8m 42s   [Cancel]   │
├────────────────────────────────────────────────────────────────┤
│ Workflow Steps [320px]      │  Live Log                        │
│  ✓ Environment Prep    12s  │  14:23:07.441 INFO  Installer   │
│  ✓ JDK Installation   2m1s │  14:23:07.912 INFO  Extracting  │
│  ▶ WebLogic Install   ···   │  14:23:08.123 INFO  OPatch OK   │
│  ○ Domain Config            │  14:23:09.001 WARN  Memory      │
│  ○ Node Manager             │  14:23:09.440 INFO  Continuing  │
│  ○ Security Config          │  14:23:10.001 INFO  Step done   │
│  ○ Health Checks            │  [virtualized — 10,000+ lines]  │
│  ○ Finalize                 │                                  │
└─────────────────────────────────────────────────────────────────┘
```

Key changes: step list on left is compact (32px per item, not 52px). Live log panel uses a virtualized renderer. Progress bar is prominent at top. Cancel button inline with progress, not in a separate toolbar.

---

## 11. WPF Architecture Improvements

### 11.1 Fix DataTemplate Isolation

In `MainWindow.xaml`, all DataTemplates are declared in `Window.Resources`. For complex views, this means they are shared by default (`x:Shared=true`). If the same ViewModel type is ever navigated to twice (e.g., restarting a wizard), the old view instance may be reused with stale state.

**Fix:** Add `x:Shared="False"` to all DataTemplate entries, OR move to an `IViewFactory` pattern where views are created on demand in `NavigationService`.

### 11.2 Replace ZIndex Panel Stacking with ContentPresenter

The current Runtime Management panel uses `Panel.ZIndex=10` on a sibling grid row — this is fragile and invisible to the layout engine. 

**Fix:** Use a `Frame`-less navigation host pattern: a single `ContentPresenter` in the main content area whose `Content` binding switches between the wizard grid and the runtime view. The `AppShellViewModel.ActiveView` property determines what's shown.

```csharp
// AppShellViewModel
[ObservableProperty] private object _activeContent;

public void NavigateTo(AppSection section) {
    ActiveContent = section switch {
        AppSection.Runtime  => _serviceProvider.GetRequiredService<RuntimeDashboardViewModel>(),
        AppSection.Deploy   => _serviceProvider.GetRequiredService<WizardViewModel>(),
        AppSection.Logs     => _serviceProvider.GetRequiredService<LogViewerViewModel>(),
        ...
    };
}
```

### 11.3 Fix Hardcoded Color Values

Three instances of hardcoded hex colors in views:
- `BtnDanger` Background: `#2D0F0E` → `DangerSubtleBrush` (dynamic resource)
- `PrerequisiteView.xaml` line 72: `Background="#2D0F0E"` → `{DynamicResource DangerSubtleBrush}`
- All `DangerSubtleBrush` references in XAML should use `{DynamicResource}` not `{StaticResource}` to support theme switching

### 11.4 Add DataGrid Virtualization Settings

```xml
<DataGrid VirtualizingPanel.IsVirtualizing="True"
          VirtualizingPanel.VirtualizationMode="Recycling"
          VirtualizingPanel.ScrollUnit="Item"
          EnableRowVirtualization="True">
```

This is missing on all DataGrid instances in the current codebase. Without it, a DataGrid showing 100+ runtime components or 500+ prerequisite check results will render all rows at load time.

### 11.5 Fix StaticResource vs DynamicResource Usage

Views use `{StaticResource}` for theme-sensitive brushes in many places:
- `OperationSelectionView.xaml`: `TextSecondaryBrush`, `AccentSubtleBrush`, etc. all use `{StaticResource}`
- `DeploymentProgressView.xaml`: all accent/status brushes use `{StaticResource}`

When the user toggles the theme at runtime, `{StaticResource}` bindings do NOT update. They must be `{DynamicResource}` for live theme switching to work correctly.

**Rule:** Any resource that differs between `Theme.Dark.xaml` and `Theme.Light.xaml` MUST be bound with `{DynamicResource}`.

### 11.6 Wire ContentControl Animation

In `MainWindow.xaml`, the `ContentControl` has a `TranslateTransform` named `ContentTranslate` but no trigger fires `SlideInRight` on navigation. Wire this in `MainWindowViewModel` via an event or in code-behind:

```csharp
// In MainWindow.xaml.cs
private void OnCurrentStepChanged() {
    var sb = (Storyboard)Resources["SlideInRight"];
    Storyboard.SetTarget(sb, StepContent); // name the ContentControl
    sb.Begin();
}
```

Or declaratively via an EventTrigger on the ContentControl's Loaded event tied to a navigation event.

### 11.7 Introduce NavigationService

Create `INavigationService` to decouple navigation from ViewModels:

```csharp
public interface INavigationService {
    void NavigateTo(AppSection section);
    void NavigateToStep(int stepIndex);
    void NavigateToComponent(Guid domainId, Guid componentId);
    event EventHandler<NavigationEventArgs> Navigated;
}
```

This removes the tight coupling where `MainWindowViewModel` both owns navigation state and references `RuntimeDashboardViewModel` directly.

### 11.8 ProgressRing Control

Replace the `⟳` TextBlock spinner with a proper `ProgressRing` UserControl using a `Path` arc geometry animated via `RotateTransform`. This:

- Renders at GPU layer (Transform animations use hardware acceleration)
- Is colorable via `Stroke` binding
- Scales cleanly to any size
- Is not a font glyph (no ClearType/fontsize artifacts)

---

## 12. Performance Strategy

### 12.1 Log Viewer Virtualization

**Problem:** `ListBox` and `ListBox`-derived controls with large item counts cause UI thread freeze on scroll.

**Solution:** Implement a `VirtualizingLogPanel` custom `Panel` derivative, OR use `TextBox` in append-only mode (fastest for pure display), OR use a `ListView` with `VirtualizingStackPanel.IsVirtualizing=True` and `VirtualizationMode=Recycling`.

Target: 50,000 lines of log output with sub-16ms frame time on scroll.

### 12.2 DataGrid Column Virtualization

Enable horizontal virtualization on wide DataGrids:

```xml
<DataGrid VirtualizingPanel.IsVirtualizingWhenGrouping="True"
          EnableColumnVirtualization="True">
```

### 12.3 ObservableCollection Batching

`RuntimeDashboardViewModel.Components` is an `ObservableCollection<RuntimeComponentViewModel>`. Each `Add` fires `CollectionChanged` which triggers a DataGrid re-layout. When refreshing all components at once, use:

```csharp
// Batch update pattern
var snapshot = newComponents.ToList();
Components.Clear(); // one CollectionChanged
foreach (var c in snapshot) Components.Add(c); // N CollectionChangeds
```

Better: replace `ObservableCollection` with a custom `BulkObservableCollection` that batches notifications, or use `CollectionViewSource` with deferred refresh.

### 12.4 Timer Precision for Auto-Refresh

`System.Threading.Timer` at 10s interval fires on a ThreadPool thread. The `Dispatch()` helper marshals updates to the UI thread. This is correct, but consider:

- Switch to `PeriodicTimer` (net6+) for a cleaner async pattern
- Make the interval configurable (5s/10s/30s/manual in Settings)
- Add backoff: if a refresh is in progress when the timer fires, skip rather than queue

### 12.5 Image and Resource Loading

Current: no images. Future: domain logos, Oracle product icons. Use `BitmapImage` with `CacheOption=OnLoad` and `CreateOptions=IgnoreImageCache=false`. Never load images on the UI thread.

### 12.6 Startup Performance

App.xaml currently loads all ResourceDictionaries at startup. The full resource merge chain is: App.xaml → Theme.Dark.xaml + Typography.xaml + Buttons.xaml + Inputs.xaml + Animations.xaml + WizardValidation.xaml + MigrationDashboard.xaml. This is fast (sub-50ms) and acceptable. No action needed.

### 12.7 ViewModel Construction

All heavy ViewModels (MiddlewareRuntimeService, etc.) are registered as singletons in DI and constructed lazily by the container. No eager construction on startup that would delay the splash window. Keep this pattern.

---

## 13. Refactor Strategy

### 13.1 Phase 1 Refactors (Theme and Shell — prerequisite for visual work)

**R1.1 — Theme token rebuild:**
- Replace `Theme.Dark.xaml` and `Theme.Light.xaml` in-place with new token set
- No view changes needed in this phase
- Validate by running app — all DynamicResource bindings auto-update

**R1.2 — Fix StaticResource → DynamicResource:**
- Global search: all `{StaticResource XxxBrush}` in view files
- Replace with `{DynamicResource XxxBrush}` for any brush that differs between themes
- `{StaticResource}` is acceptable only for non-theme resources (converters, templates, non-color styles)

**R1.3 — Fix hardcoded colors:**
- `BtnDanger` background `#2D0F0E` → `{DynamicResource DangerSubtleBrush}`
- `PrerequisiteView` stat card `#2D0F0E` → `{DynamicResource DangerSubtleBrush}`

**R1.4 — Extract AppShellViewModel:**
- Rename `MainWindowViewModel` → `WizardViewModel` (it only manages wizard state)
- Create new `AppShellViewModel` that owns: `ActiveSection`, `WizardViewModel`, `RuntimeDashboardViewModel`, `LogViewerViewModel`, navigation commands
- `MainWindow.xaml` binds to `AppShellViewModel`

**R1.5 — Create NavigationService:**
- `INavigationService` interface
- `NavigationService` implementation
- Register in DI; inject into `AppShellViewModel`

### 13.2 Phase 2 Refactors (Navigation Shell)

**R2.1 — Build ActivityBar control:**
- `ActivityBarView.xaml` — 48px left-side `ItemsControl` with icon buttons
- `ActivityBarItemViewModel` — icon path data, tooltip, section enum, IsActive binding

**R2.2 — Build SectionPanel control:**
- `SectionPanelView.xaml` — 220px panel that renders context-sensitive navigation
- Uses `ContentControl` with typed DataTemplates for each section's nav content

**R2.3 — Rebuild MainWindow.xaml layout:**
- Replace 3-row grid (title/content/footer) with:
  - `Grid.Row[0]` 36px title
  - `Grid.Row[1]` * content: ActivityBar + SectionPanel + ContentArea columns
  - `Grid.Row[2]` 24px status bar
- ContentArea uses `ContentPresenter` bound to `AppShellViewModel.ActiveContent`

**R2.4 — Create LogViewerViewModel and LogViewerView:**
- `LogViewerViewModel` — section panel sources, active source, filter state, log entry collection
- `LogViewerView.xaml` — filter bar + virtualized log control

### 13.3 Phase 3 Refactors (View Density and Style)

**R3.1 — Rebuild OperationSelectionView:**
- Compact cards (90px height)
- Remove emoji icons, use Path geometry icons
- Cards navigate directly (no Back/Next)

**R3.2 — Rebuild step item template in SectionPanel:**
- 32px item height (from 52px)
- Icon: 16px Path or Segoe MDL2 glyph
- Active indicator: 2px left border (AccentPrimary)

**R3.3 — Fix TypeH3 foreground:**
- Change from `TextSecondaryBrush` to `TextPrimaryBrush`

**R3.4 — Add DataGrid virtualization settings to all DataGrid instances:**
- RuntimeDashboardView, PrerequisiteView, all migration/decommission views with DataGrids

**R3.5 — Redesign stat cards (Prerequisite, DeploymentProgress):**
- Remove hardcoded colors
- Apply CardSuccess/CardWarning/CardDanger styles from new design system
- Reduce padding from 14px to 12px

### 13.4 Phase 4 Refactors (Polish and Animation)

**R4.1 — Wire SlideInRight animation to navigation:**
- ContentControl gets `RenderTransform` + trigger on `AppShellViewModel.ActiveContent` change

**R4.2 — Replace text spinners with ProgressRing:**
- Global `ProgressRing` UserControl in `Controls/` folder
- Replace all `⟳` text-based spinners

**R4.3 — Add keyboard shortcuts:**
- `KeyBinding` on Window for F5 (Refresh), Ctrl+D (Discover), Esc (cancel/close panel)
- `AccessText` on primary action buttons

---

## 14. Migration Plan

### Phase 1 — Theme and Foundation (2–3 days, zero risk)

All changes are backward-compatible. No ViewModel changes. No layout restructuring.

1. Rebuild `Theme.Dark.xaml` with new 5-level surface stack and brighter semantic subtles
2. Rebuild `Theme.Light.xaml` in parallel
3. Fix `{StaticResource}` → `{DynamicResource}` across all view files (search/replace, ~45 occurrences)
4. Fix hardcoded `#2D0F0E` in `Buttons.xaml` and `PrerequisiteView.xaml`
5. Fix `TypeH3` foreground
6. Add DataGrid virtualization attributes to all DataGrid instances

**Validation:** Run app, exercise Deploy/Migrate/Decommission wizard, toggle theme. All stat cards, status badges, and semantic backgrounds must be visually distinct.

### Phase 2 — Navigation Shell (4–6 days, medium risk)

Architectural change. Current `MainWindowViewModel` is replaced by `AppShellViewModel`. All existing wizard content continues to work — it is merely mounted in a new location.

1. Create `INavigationService` and `NavigationService`
2. Create `AppShellViewModel` (wraps `WizardViewModel`, `RuntimeDashboardViewModel`, etc.)
3. Create `ActivityBarView` with 7 section buttons
4. Create `SectionPanelView` with DataTemplate-driven section navigation
5. Rebuild `MainWindow.xaml` — new 3-column layout inside Row 1
6. Move wizard step navigator from global sidebar into `SectionPanelView` for Deploy/Migrate/Decommission sections
7. Move step action bar (Back/Next) from footer into content area of wizard sections
8. Implement status bar (24px) replacing footer nav bar

**Rollback:** If Phase 2 has blocking issues, Phase 1 changes are self-contained and can ship independently.

### Phase 3 — Screen Redesigns (5–7 days, low risk)

Iterative screen-by-screen redesign. Each screen is independently deployable.

1. `OperationSelectionView` — compact cards, Path icons
2. `RuntimeDashboardView` — stat cards, detail side-panel, virtualization
3. `LogViewerView` — new screen, virtualized log renderer
4. `PrerequisiteView` — stat cards, density
5. `DeploymentProgressView` — progress header, virtualized log
6. `WelcomeView` — compact header layout
7. Migration and Decommission screens — density pass

### Phase 4 — Polish and Accessibility (3–4 days)

1. `ProgressRing` control + replace all text spinners
2. `SlideInRight` animation wired to navigation transitions
3. Keyboard shortcuts (`KeyBinding` map)
4. Tooltip completion pass (all buttons should have `ToolTip`)
5. `AutomationProperties.Name` on all interactive elements (accessibility)
6. Final color contrast check (WCAG AA minimum 4.5:1 for text on backgrounds)

**Total estimated effort:** 14–20 developer-days for all 4 phases.

---

## 15. Risks and Technical Debt Analysis

### Risk R-01 — Theme Token Rename Breaks Existing View References

**Probability: High. Impact: Medium.**

Renaming tokens (e.g., `BackgroundPanelBrush` → `BackgroundSurfaceBrush`) will cause XAML `{DynamicResource}` lookup failures that produce transparent/invisible elements at runtime — no compile-time error. New tokens that don't exist yet will silently produce null brushes.

**Mitigation:** Keep all existing token names as aliases pointing to new values. Do NOT rename — only add new tokens and change the color values. Existing view files continue to work without change. Phase 3 can then rename usage sites gradually.

### Risk R-02 — AppShellViewModel Refactor Creates DI Circular Dependencies

**Probability: Medium. Impact: Medium.**

`AppShellViewModel` will need to reference `RuntimeDashboardViewModel`, `WizardViewModel`, `LogViewerViewModel`, etc. If these are all singletons in DI, constructor injection may create ordering issues, or `AppShellViewModel` may need to become the top-level entry point.

**Mitigation:** Use `IServiceProvider` factory injection in `AppShellViewModel` constructor, creating child ViewModels lazily on first navigation. This is the same pattern as the current `MainWindowViewModel`.

### Risk R-03 — Log Viewer Virtualization Regression

**Probability: Medium. Impact: High.**

A virtualized log renderer that also supports text search/filter requires careful architecture. A naive approach (filter all items, re-render) will cause lag on large log files. Virtual rendering and filtering must work together — the visible window must render only the filtered subset.

**Mitigation:** Use `CollectionViewSource` with a filter predicate on the underlying `ObservableCollection<LogTailEntry>`. `CollectionViewSource` is built into WPF and integrates with `ItemsControl` virtualization correctly. For very large files (>100k lines), consider ring-buffer approach where only the last N lines are kept in the observable collection.

### Risk R-04 — Animation Performance on Older Hardware

**Probability: Low. Impact: Low.**

`SlideInRight` storyboard uses a `TranslateTransform` which is hardware-accelerated when `CacheMode="BitmapCache"` is set on the target element. Without this, the animation runs on the software rendering layer and may stutter.

**Mitigation:** Add `<UIElement.CacheMode><BitmapCache /></UIElement.CacheMode>` to the ContentArea element that is animated.

### Risk R-05 — Static vs Dynamic Resource Migration Misses

**Probability: High. Impact: Low.**

The search-and-replace pass for `{StaticResource}` → `{DynamicResource}` is mechanical but can miss:
- Resources set via `Style.Triggers` (uses `{StaticResource}`)
- Resources inside `DataTemplate` scopes

**Mitigation:** After the replacement pass, run the app with both themes and screenshot every screen. Any element that doesn't respond to theme toggle has a missed `{StaticResource}` binding.

### Tech Debt TD-01 — No Settings Persistence

There is currently no settings file, no `ISettingsService`, no persistence of: selected theme, auto-refresh interval, last used domain path, window size/position. These are accumulated across the session but lost on restart.

**Remediation:** Add `IUserSettingsService` backed by `%APPDATA%\WEDM\settings.json`. Inject into App.xaml.cs at startup. Settings: theme, auto-refresh interval, last domain home, window geometry, collapsed panel state.

### Tech Debt TD-02 — All DataTemplates in Window.Resources

28 DataTemplates in `Window.Resources` creates a massive XAML root with complex type resolution. Each DataTemplate is `x:Shared=true` by default — if a ViewModel type is navigated to multiple times (e.g., re-running a deployment), the old UserControl instance may be recycled with stale bindings.

**Remediation:** Phase 3 refactor. Move DataTemplates into per-section merged ResourceDictionary files. Add `x:Shared="False"` where Views must be fresh on each navigation.

### Tech Debt TD-03 — RuntimeDashboardView Credential Row Always Visible

The credentials row (AdminUser + PasswordBox) is always visible at the bottom of the runtime grid, occupying ~50px. It's contextually relevant only when stopping a component via WLST. This permanent visibility wastes space and exposes credential fields unnecessarily.

**Remediation:** Move to the detail side-panel (P-8). Show only when a stoppable AdminServer component is selected.

### Tech Debt TD-04 — No Error Boundary on View Exceptions

If a ViewModel property throws during data binding, WPF silently swallows the exception and the binding target remains at its last-good value. There is no global DataBinding error handler wired up.

**Remediation:** In debug builds, attach `PresentationTraceSources.DataBindingSource` listener. In production, override `App.OnActivated` to attach a `Dispatcher.UnhandledException` handler that logs binding errors to the WEDM log.

### Tech Debt TD-05 — No Accessibility Pass

There are no `AutomationProperties.Name` attributes on any interactive element. Screen readers will fall back to button content (which is often an emoji or unicode character). Custom button templates do not preserve `AutomationPeer` behavior.

**Remediation:** Phase 4. Add `AutomationProperties.Name="{TemplateBinding ToolTip}"` to all custom button templates. Add `AutomationProperties.LabeledBy` on form fields pointing to their label TextBlock.

---

## Summary: Phase-by-Phase Implementation Checklist

| Phase | Key Deliverables | Risk | Effort |
|-------|-----------------|------|--------|
| **1: Theme + Foundation** | New dark/light tokens, fix StaticResource, fix hardcoded colors, DataGrid virtualization | Low | 2–3 days |
| **2: Navigation Shell** | AppShellViewModel, ActivityBar, SectionPanel, new MainWindow layout, StatusBar | Medium | 4–6 days |
| **3: Screen Redesigns** | OperationSelection, RuntimeDashboard, LogViewer, Prerequisite, DeploymentProgress, compact wizards | Low | 5–7 days |
| **4: Polish** | ProgressRing, animations, keyboard shortcuts, accessibility, contrast audit | Low | 3–4 days |

**Total: 14–20 developer-days to complete enterprise-grade redesign.**

---

*End of WEDM Enterprise UI/UX Redesign Strategy v1.0*
*Prepared for implementation approval — no code changes have been made.*
