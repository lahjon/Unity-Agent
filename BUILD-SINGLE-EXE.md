# Building Happy Engine as a Single Executable

Happy Engine can be built as a single executable file that includes the .NET runtime and all dependencies. This creates a self-contained application that can run on Windows without requiring .NET to be installed separately.

## Build Methods

### Method 1: Using Build Scripts

#### Batch Script (Windows Command Prompt)
```cmd
build-single-exe.bat
```

#### PowerShell Script
```powershell
.\build-single-exe.ps1
```

### Method 2: Using dotnet CLI directly

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Method 3: Using Visual Studio

1. Right-click the HappyEngine project
2. Select "Publish..."
3. Choose the "SingleExe" profile
4. Click "Publish"

## Output Location

The single executable will be created at:
```
bin\Release\net9.0-windows\win-x64\publish\HappyEngine.exe
```

## Executable Details

- **File Size**: ~80-85 MB (includes .NET runtime)
- **Runtime**: Self-contained, no .NET installation required
- **Platform**: Windows x64
- **Compression**: Enabled to reduce file size
- **Ready to Run**: Optimized for faster startup

## Configuration

The single-file publish is configured in `HappyEngine.csproj` with these settings:

- `PublishSingleFile`: Creates a single executable
- `SelfContained`: Includes .NET runtime
- `RuntimeIdentifier`: Targets Windows x64
- `EnableCompressionInSingleFile`: Compresses the executable
- `PublishReadyToRun`: Pre-compiles for faster startup
- `SatelliteResourceLanguages`: Only includes English resources

## Distribution

The resulting `HappyEngine.exe` can be distributed as a standalone file. Users can run it directly without any installation process or dependencies.

## Notes

- The executable extracts temporary files when first run (to %TEMP%)
- Antivirus software might flag the self-extracting exe (false positive)
- First startup might be slightly slower due to extraction
- The app icon is embedded in the executable