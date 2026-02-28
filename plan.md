# Plan: Eliminate Hardcoded Color Strings with Theme Resource Lookup

## Overview
Replace hardcoded hex color strings across C# and XAML files with named brush resources defined in `Themes/Colors.xaml`. This centralizes theming, reduces duplication, and makes future theme changes trivial.

## Step 1: Expand `Themes/Colors.xaml` with new named brushes

Add ~30 new `SolidColorBrush` resources organized by category:

**Backgrounds (darkest → lightest):**
| Key | Value | Semantic Use |
|-----|-------|-------------|
| BgAbyss | #0A0A0A | Terminal/output read-only areas |
| BgPit | #0E0E0E | Log viewer body |
| BgTerminalInput | #141414 | Terminal text inputs |
| BgDarkTerminal | #181818 | Terminal root bar |
| BgSection | #1E1E1E | Section/card backgrounds within panels |
| BgPopup | #252525 | Popups, saved prompt cards |
| BgCard | #2A2A2A | Card/input backgrounds, grid splitter |
| BgCardHover | #2E2E2E | Card hover state |
| BgOverlay | #CC191919 | Semi-transparent loading overlay |

**Borders:**
| Key | Value | Semantic Use |
|-----|-------|-------------|
| BorderMedium | #3A3A3A | Panel/dialog borders (heavier than BorderSubtle) |

**Text (dimmest → brightest):**
| Key | Value | Semantic Use |
|-----|-------|-------------|
| TextDisabled | #555555 | Disabled/fallback/hint text |
| TextDim | #777777 | Dimmed labels |
| TextSubdued | #888888 | Dim labels, secondary info, close buttons |
| TextTabHeader | #AAAAAA | Tab header text, neutral info |
| TextBody | #B0B0B0 | Standard body text, terminal output |
| TextButton | #C8C8C8 | Button foreground text |
| TextLight | #CCCCCC | Light body text, input foreground |
| TextBright | #E0E0E0 | TextBox foregrounds, bright text |

**Accents:**
| Key | Value | Semantic Use |
|-----|-------|-------------|
| AccentBlue | #4EA8DB | Chat/Gemini selection blue |
| AccentTeal | #4DB6AC | Stored tasks, Gemini teal |

**Status/Feedback:**
| Key | Value | Semantic Use |
|-----|-------|-------------|
| DangerBright | #E05555 | Failed/error (brighter than Danger) |
| DangerAlert | #FF6B6B | Bright error message text |
| DangerDeleteHover | #E57373 | Delete button hover |
| WarningYellow | #FFD600 | Queued status |
| WarningAmber | #E0A030 | Cancelled/amber status |
| WarningOrange | #CC8800 | File lock badge |
| SuccessGreen | #4CAF50 | MCP enabled green |
| HighScoreGold | #FFD700 | Game high-score text |

**Specialty:**
| Key | Value | Semantic Use |
|-----|-------|-------------|
| PlanBadgeBg | #2A1F3D | Plan mode badge background |
| PlanBadgeBorder | #7E57C2 | Plan mode badge border |
| PlanBadgeText | #CE93D8 | Plan mode badge/pause text |
| PausedBlue | #64B5F6 | Resume button foreground |
| ToolActivityText | #7A9EC2 | Tool activity feed text |
| SeparatorDark | #444444 | Separator lines, dep chip borders |

## Step 2: Replace hardcoded colors in C# files with `FindResource`

For each file, replace `new SolidColorBrush(Color.FromRgb(...))` and `ColorConverter.ConvertFromString(...)` with resource lookups:

- **MainWindow.xaml.cs** (~15 replacements) — uses `FindResource()` directly (is a Window)
  - Chat bubbles, saved prompts, dependency chips
  - Refactor `AddChatBubble` to accept `Brush` instead of hex string

- **Managers/OutputTabManager.cs** (~25 replacements) — uses `Application.Current.FindResource()`
  - Output panel, input boxes, tab colors, stored task cards

- **Managers/GeminiImagePanel.cs** (~8 replacements) — uses `Application.Current.FindResource()`
  - Image gallery backgrounds, borders, labels

- **Managers/ProjectManager.cs** (~20 replacements, skip palette array lines 32-43)
  - Project cards, MCP indicators, rename UI

- **TerminalTabManager.cs** (~12 replacements)
  - Terminal tab colors, labels, backgrounds

- **Dialogs/CreateProjectDialog.cs** (~12 replacements)
- **Dialogs/DarkDialog.cs** (~8 replacements)
- **Dialogs/LogViewerDialog.cs** (~10 replacements)
- **Dialogs/StoredTaskViewerDialog.cs** (~10 replacements)
- **App.xaml.cs** (~6 replacements) — with fallback guard for early-startup calls
- **Converters/StringToBrushConverter.cs** (1 replacement — fallback color)

## Step 3: Replace hardcoded colors in XAML files with `{StaticResource}`

- **MainWindow.xaml** (~100+ replacements) — largest file
  - Map all `Background="#191919"` → `{StaticResource BgDeep}`, etc.
  - Leave `ColorAnimation To="..."` values hardcoded (WPF limitation)
  - Leave category-specific tint colors hardcoded (unique semantics)

- **Games/*.xaml** (3 files, ~8 replacements each)
  - BirdHunterGame.xaml, QuickMathGame.xaml, ReactionTestGame.xaml

- **Themes/*.xaml** (~15 replacements across ComboBox, TabControl, GridSplitter, ToggleSwitch, Buttons)

## Step 4: Build verification

Run `dotnet build` to verify all resource keys resolve correctly.

## What NOT to change
1. Project color palette array in ProjectManager.cs (data, not theme)
2. `AgentTask.StatusColor` string properties (bound via StringToBrush converter)
3. `Brushes.Transparent` references (framework-provided)
4. `ColorAnimation To="..."` values in XAML storyboards (WPF limitation)
5. Category-specific tint colors (#3A2020, #1A3A20, etc.) — unique semantics
6. Colors used only once with no reusable semantic meaning

## Risks & Edge Cases
- **App.xaml.cs early calls**: `ShowErrorDialog` may run before resources load — wrap FindResource in try-catch fallback
- **Frozen brushes**: StaticResource brushes are frozen/shared — verify no code mutates returned brushes
- **ColorAnimation**: Storyboard `To=` attributes cannot use StaticResource — must stay hardcoded
