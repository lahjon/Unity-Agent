$root = "C:\Unity Projects\Unity Agent"
$extensions = @('.cs', '.xaml', '.csproj', '.sln', '.md', '.bat', '.ps1', '.txt')
$excludeDirs = @('bin', 'obj', 'publish', 'runtime')
$count = 0

Get-ChildItem -Path $root -Recurse -File | ForEach-Object {
    $skip = $false
    foreach ($d in $excludeDirs) {
        if ($_.FullName -like "*\$d\*") { $skip = $true; break }
    }
    if ($skip) { return }
    if ($extensions -notcontains $_.Extension) { return }
    if ($_.Name -eq 'rename_script.ps1') { return }
    
    $content = [System.IO.File]::ReadAllText($_.FullName)
    if ($content -match 'HappyEngine|Happy Engine') {
        $newContent = $content.Replace('Happy Engine', 'Spritely').Replace('HappyEngine', 'Spritely')
        [System.IO.File]::WriteAllText($_.FullName, $newContent)
        $count++
        Write-Host "Updated: $($_.FullName)"
    }
}

Write-Host "`nTotal files updated: $count"

