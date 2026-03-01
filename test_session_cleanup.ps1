# Test script to verify session data cleanup in HappyEngine

$appDataPath = "$env:LOCALAPPDATA\HappyEngine"
Write-Host "`n=== HappyEngine Session Data Verification ===" -ForegroundColor Cyan

# Function to display directory structure
function Show-SessionData {
    param([string]$Title)

    Write-Host "`n$Title" -ForegroundColor Yellow
    Write-Host ("-" * 50) -ForegroundColor Gray

    # Check agent bus data
    $agentBusPath = "$appDataPath\agent-bus"
    if (Test-Path $agentBusPath) {
        Write-Host "`nAgent Bus Data:" -ForegroundColor Green
        Get-ChildItem $agentBusPath -Recurse | ForEach-Object {
            $indent = "  " * ($_.FullName.Split('\').Count - $agentBusPath.Split('\').Count)
            Write-Host "$indent$($_.Name)" -ForegroundColor $(if ($_.PSIsContainer) { "Cyan" } else { "White" })
        }
    } else {
        Write-Host "`nNo agent bus data found" -ForegroundColor DarkGray
    }

    # Check persistent data
    Write-Host "`n`nPersistent Data Files:" -ForegroundColor Green
    $persistentFiles = @(
        "settings.json",
        "projects.json",
        "task_history.json",
        "task_templates.json",
        "saved_prompts.json"
    )

    foreach ($file in $persistentFiles) {
        $filePath = "$appDataPath\$file"
        if (Test-Path $filePath) {
            $size = (Get-Item $filePath).Length
            $modified = (Get-Item $filePath).LastWriteTime
            Write-Host "  ✓ $file (${size}B, modified: $modified)" -ForegroundColor White
        } else {
            Write-Host "  ✗ $file (not found)" -ForegroundColor DarkGray
        }
    }
}

# Main verification flow
Write-Host "`nThis script will help verify that:"
Write-Host "1. Session-specific data (agent bus) is cleared when tasks complete"
Write-Host "2. Persistent data (settings, projects, etc.) is retained between sessions"

# Show current state
Show-SessionData "CURRENT STATE"

# Monitor for changes
Write-Host "`n`nMonitoring Instructions:" -ForegroundColor Magenta
Write-Host "1. Start HappyEngine and create a task with 'Enable message bus' checked"
Write-Host "2. While the task is running, press Enter here to see the agent bus data"
Write-Host "3. After all tasks complete, press Enter again to verify cleanup"
Write-Host "4. Close and restart HappyEngine, then press Enter to verify persistence"

$step = 1
while ($true) {
    Write-Host "`n`nPress Enter to check state (step $step), or 'q' to quit: " -NoNewline -ForegroundColor Yellow
    $input = Read-Host

    if ($input -eq 'q') { break }

    Show-SessionData "STATE CHECK #$step"

    # Additional analysis for agent bus
    $agentBusPath = "$appDataPath\agent-bus"
    if (Test-Path $agentBusPath) {
        $busCount = (Get-ChildItem $agentBusPath -Directory).Count
        Write-Host "`n`nAnalysis: Found $busCount active message bus(es)" -ForegroundColor Cyan

        Get-ChildItem $agentBusPath -Directory | ForEach-Object {
            $scratchpad = "$($_.FullName)\_scratchpad.md"
            if (Test-Path $scratchpad) {
                Write-Host "`nBus: $($_.Name)" -ForegroundColor Yellow
                Write-Host "Active tasks in scratchpad:" -ForegroundColor Gray
                Select-String -Path $scratchpad -Pattern "^\| ([a-f0-9]+) \|" | ForEach-Object {
                    Write-Host "  - Task: $($_.Matches[0].Groups[1].Value)" -ForegroundColor White
                }
            }
        }
    }

    $step++
}

Write-Host "`n`nSession data verification complete!" -ForegroundColor Green