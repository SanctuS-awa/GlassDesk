@echo off
rem GlassDesk 0.2.2 application publish (Windows + VS 2022 C++ Desktop + .NET SDK)
setlocal
cd /d "%~dp0.."
if not exist "scripts\build-native.ps1" (
  echo [ERROR] scripts/build-native.ps1 not found. Make sure this file is in the project scripts directory.
  exit /b 1
)
if not exist "src\GlassDesk.App\GlassDesk.App.csproj" (
  echo [ERROR] src/GlassDesk.App/GlassDesk.App.csproj not found.
  exit /b 1
)

echo [1/5] Locating VS 2022 developer environment...
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo [ERROR] vswhere.exe not found. Install Visual Studio 2022 with "Desktop development with C++".
  exit /b 1
)
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
if not defined VSINSTALL (
  echo [ERROR] No VS 2022 installation with MSVC x64 tools found.
  exit /b 1
)
if not exist "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" (
  echo [ERROR] VsDevCmd.bat not found: %VSINSTALL%\Common7\Tools\VsDevCmd.bat
  exit /b 1
)
call "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64
if errorlevel 1 (
  echo [ERROR] VsDevCmd.bat failed.
  exit /b 1
)

echo [2/5] Building native DLL (Release)...
set "NATIVE_CANDIDATE=%CD%\artifacts\native-candidates\quick-%RANDOM%-%RANDOM%"
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1 -Configuration Release -OutputDirectory "%NATIVE_CANDIDATE%"
if errorlevel 1 (
  echo [ERROR] Native build failed. See the output above.
  exit /b 1
)
if not exist "%NATIVE_CANDIDATE%\GlassDesk.Native.dll" (
  echo [ERROR] Native build reported success but GlassDesk.Native.dll was not created.
  exit /b 1
)

echo [3/5] Running dotnet restore...
dotnet restore src\GlassDesk.App\GlassDesk.App.csproj -r win-x64 --locked-mode "-p:GlassDeskNativeDirectory=%NATIVE_CANDIDATE%" -m:1 -p:BuildInParallel=false
if errorlevel 1 (
  echo [ERROR] dotnet restore failed.
  exit /b 1
)

echo [4/5] Running dotnet publish (win-x64, self-contained)...
dotnet publish src\GlassDesk.App\GlassDesk.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\GlassDesk "-p:GlassDeskNativeDirectory=%NATIVE_CANDIDATE%" -p:PublishSingleFile=false -p:PublishTrimmed=false -m:1 -p:BuildInParallel=false
if errorlevel 1 (
  echo [ERROR] dotnet publish failed.
  exit /b 1
)
if not exist "artifacts\GlassDesk\GlassDesk.exe" (
  echo [ERROR] Publish reported success but artifacts/GlassDesk/GlassDesk.exe was not created.
  exit /b 1
)
echo Staging license and notice files...
robocopy licenses "artifacts\GlassDesk\licenses" /E /COPY:DAT /DCOPY:DAT /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 (
  echo [ERROR] Could not copy the licenses directory into the publish output.
  exit /b 1
)
copy /Y "LICENSE" "artifacts\GlassDesk\LICENSE" >nul
if errorlevel 1 (
  echo [ERROR] Could not copy LICENSE into the publish output.
  exit /b 1
)
copy /Y "THIRD-PARTY-NOTICES.md" "artifacts\GlassDesk\THIRD-PARTY-NOTICES.md" >nul
if errorlevel 1 (
  echo [ERROR] Could not copy THIRD-PARTY-NOTICES.md into the publish output.
  exit /b 1
)
if not exist "artifacts\GlassDesk\licenses\manifest.json" (
  echo [ERROR] License manifest is missing from the publish output.
  exit /b 1
)
if not exist "artifacts\GlassDesk\licenses\Glasswork\LICENSE" (
  echo [ERROR] Glasswork license is missing from the publish output.
  exit /b 1
)
exit /b 0
