# Game Creation Rules

You MUST follow these rules exactly when creating a new minigame for Unity Agent.

## Architecture

- **Namespace**: `UnityAgent.Games`
- **Location**: All game files go in the `Games/` directory
- **Files per game**: Exactly two — `{GameName}.xaml` and `{GameName}.xaml.cs`
- **Interface**: Every game MUST implement `IMinigame` (defined in `Games/IMinigame.cs`)
- **Base class**: Extend `UserControl` AND implement `IMinigame`
- **No external dependencies**: Games must be fully self-contained using only WPF and .NET standard libraries

## IMinigame Contract

```csharp
public interface IMinigame
{
    string GameName { get; }        // Display name (e.g. "Reaction Test")
    string GameIcon { get; }        // Single Unicode character (e.g. "\u25CF")
    string GameDescription { get; } // Short description for tooltip
    UserControl View { get; }       // Return `this`
    event Action? QuitRequested;    // Fire when user clicks Quit
    void Start();                   // Called when game is launched
    void Stop();                    // Called when game is stopped/switched away
}
```

## UI Pattern — Three States

Every game MUST use the three-state panel pattern:

1. **MenuPanel** — Shown on start. Contains:
   - Game title: `Foreground="#DA7756" FontSize="22" FontWeight="Bold" FontFamily="Segoe UI"`
   - Description: `Foreground="#777" FontSize="13" FontFamily="Segoe UI"`
   - Start button (accent style)
   - Quit button (secondary style)

2. **GamePanel** — The actual gameplay area. Visibility toggled.

3. **ResultPanel** — Shown after a round/game ends. Contains:
   - Result/score display
   - "Try Again" button (accent style)
   - "Quit" button (secondary style)

State transitions: `Start()` → show MenuPanel. Start button → show GamePanel. Game ends → show ResultPanel. Quit → fire `QuitRequested`.

**Escape key**: Every game MUST handle the Escape key to quit. Add `PreviewKeyDown="OnPreviewKeyDown"` to the `UserControl` element in XAML and add this handler in code-behind:
```csharp
private void OnPreviewKeyDown(object sender, KeyEventArgs e)
{
    if (e.Key == Key.Escape) { QuitRequested?.Invoke(); e.Handled = true; }
}
```
This ensures pressing Escape fires `QuitRequested` from any state (menu, game, result).

## Theme Colors (Hardcoded — Do NOT Use Theme Resources)

All colors are hardcoded hex values directly in XAML, not bound to theme resources:

| Usage            | Color     |
|------------------|-----------|
| Background       | `#191919` |
| Accent           | `#DA7756` |
| Accent Hover     | `#E89B7E` |
| Success/Score    | `#5CB85C` |
| Danger/Error     | `#A15252` |
| Primary text     | `#FFF`    |
| Secondary text   | `#777`    |
| Muted text       | `#666`    |
| Button secondary | `#333`    |
| Button sec hover | `#444`    |
| Subtle text      | `#AAA`    |
| Input background | `#111`    |
| Input border     | `#444`    |

## Button Template

All buttons use inline `ControlTemplate` with `CornerRadius="6"` and a hover trigger. Two button styles:

**Accent button** (Start, Try Again):
```xml
<Button Content="Start" Click="Start_Click"
        Width="120" Height="36" FontSize="14" FontFamily="Segoe UI" FontWeight="SemiBold"
        Cursor="Hand" Background="#DA7756" Foreground="#FFF" BorderThickness="0">
    <Button.Template>
        <ControlTemplate TargetType="Button">
            <Border x:Name="Bd" Background="{TemplateBinding Background}"
                    CornerRadius="6" Padding="16,8">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Bd" Property="Background" Value="#E89B7E"/>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
    </Button.Template>
</Button>
```

**Secondary button** (Quit):
```xml
<Button Content="Quit" Click="Quit_Click"
        Width="90" Height="34" FontSize="13" FontFamily="Segoe UI" FontWeight="SemiBold"
        Cursor="Hand" Background="#333" Foreground="#AAA" BorderThickness="0" Margin="0,10,0,0">
    <Button.Template>
        <ControlTemplate TargetType="Button">
            <Border x:Name="Bd" Background="{TemplateBinding Background}"
                    CornerRadius="6" Padding="14,7">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="Bd" Property="Background" Value="#444"/>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>
    </Button.Template>
</Button>
```

## XAML Structure

```xml
<UserControl x:Class="UnityAgent.Games.{GameName}"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Focusable="True">
    <Grid Background="#191919">
        <!-- MenuPanel: StackPanel centered -->
        <!-- GamePanel: Grid or Canvas, Visibility="Collapsed" -->
        <!-- ResultPanel: StackPanel centered, Visibility="Collapsed" -->
    </Grid>
</UserControl>
```

## Code-Behind Structure

```csharp
using System;
using System.Windows;
using System.Windows.Controls;

namespace UnityAgent.Games
{
    public partial class {GameName} : UserControl, IMinigame
    {
        public string GameName => "{Display Name}";
        public string GameIcon => "{single unicode char}";
        public string GameDescription => "{short description}";
        public UserControl View => this;
        public event Action? QuitRequested;

        // Private state fields here

        public {GameName}() { InitializeComponent(); }
        public void Start() { ShowMenu(); }
        public void Stop() { /* cleanup timers/state */ }

        private void ShowMenu() { /* show MenuPanel, hide others */ }
        // Game logic methods
        private void Start_Click(object sender, RoutedEventArgs e) => StartGame();
        private void TryAgain_Click(object sender, RoutedEventArgs e) => StartGame();
        private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
    }
}
```

## Registration

After creating the game files, register the game in `MainWindow.xaml.cs` inside `InitializeGames()`:

```csharp
private void InitializeGames()
{
    _availableGames.Add(new ReactionTestGame());
    _availableGames.Add(new QuickMathGame());
    _availableGames.Add(new {YourNewGame}());  // Add here
    RebuildGameSelector();
}
```

## Rules Summary

1. Implement `IMinigame` on a `UserControl`
2. Use the three-state panel pattern (Menu → Game → Result)
3. Hardcode all theme colors — never use `DynamicResource` or `StaticResource`
4. Use inline button `ControlTemplate` with `CornerRadius="6"` and hover triggers
5. Keep it simple — single file pair, no extra classes or helpers
6. Fire `QuitRequested` from all Quit buttons and on Escape key (via `PreviewKeyDown`)
7. Clean up timers and state in `Stop()`
8. Register in `InitializeGames()`
9. Use `FontFamily="Segoe UI"` for all text, `"Consolas"` only for monospace game content
10. No external NuGet packages or dependencies
