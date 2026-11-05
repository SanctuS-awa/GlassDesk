@echo off
rem GlassDesk 0.2.1.1beta one-click build (Windows + VS 2022 C++ Desktop + .NET SDK)
rem Double-click to run. Any step that fails stops here and shows the error.
rem NOTE: This file cannot run on Linux; run it on Windows by double-clicking.
setlocal
cd /d "%~dp0"
if not exist "scripts\build-native.ps1" (
  echo [ERROR] scripts\build-native.ps1 not found. Make sure this bat file is in the project root.
  pause
  exit /b 1
)
if not exist "src\GlassDesk.App\GlassDesk.App.csproj" (
  echo [ERROR] src\GlassDesk.App\GlassDesk.App.csproj not found.
  pause
  exit /b 1
)

echo [1/5] Locating VS 2022 developer environment...
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo [ERROR] vswhere.exe not found. Install Visual Studio 2022 with "Desktop development with C++".
  pause
  exit /b 1
)
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
if not defined VSINSTALL (
  echo [ERROR] No VS 2022 installation with MSVC x64 tools found.
  pause
  exit /b 1
)
if not exist "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" (
  echo [ERROR] VsDevCmd.bat not found: %VSINSTALL%\Common7\Tools\VsDevCmd.bat
  pause
  exit /b 1
)
call "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" -arch=amd64 -host_arch=amd64
if errorlevel 1 (
  echo [ERROR] VsDevCmd.bat failed.
  pause
  exit /b 1
)

echo [2/5] Building native DLL (Release)...
set "NATIVE_CANDIDATE=%CD%\artifacts\native-candidates\quick-%RANDOM%-%RANDOM%"
powershell -ExecutionPolicy Bypass -File scripts\build-native.ps1 -Configuration Release -OutputDirectory "%NATIVE_CANDIDATE%"
if errorlevel 1 (
  echo [ERROR] Native build failed. See the output above.
  pause
  exit /b 1
)
if not exist "%NATIVE_CANDIDATE%\GlassDesk.Native.dll" (
  echo [ERROR] Native build reported success but GlassDesk.Native.dll was not created.
  pause
  exit /b 1
)

echo [3/5] Running dotnet restore...
dotnet restore src\GlassDesk.App\GlassDesk.App.csproj -r win-x64 --locked-mode "-p:GlassDeskNativeDirectory=%NATIVE_CANDIDATE%" -m:1 -p:BuildInParallel=false
if errorlevel 1 (
  echo [ERROR] dotnet restore failed.
  pause
  exit /b 1
)

echo [4/5] Running dotnet publish (win-x64, self-contained)...
dotnet publish src\GlassDesk.App\GlassDesk.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\GlassDesk "-p:GlassDeskNativeDirectory=%NATIVE_CANDIDATE%" -p:PublishSingleFile=false -p:PublishTrimmed=false -m:1 -p:BuildInParallel=false
if errorlevel 1 (
  echo [ERROR] dotnet publish failed.
  pause
  exit /b 1
)
if not exist "artifacts\GlassDesk\GlassDesk.exe" (
  echo [ERROR] Publish reported success but artifacts\GlassDesk\GlassDesk.exe was not created.
  pause
  exit /b 1
)

echo [5/5] Build succeeded! Opening artifacts\GlassDesk (double-click GlassDesk.exe to run)...
start "" "artifacts\GlassDesk"
echo Done.
pause
