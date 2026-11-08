@echo off
rem GlassDesk 0.2.1.3beta installer packaging (Windows)
setlocal
cd /d "%~dp0"
if not exist "scripts\build-app.cmd" (
  echo [ERROR] scripts/build-app.cmd not found. Make sure this bat file is in the project root.
  pause
  exit /b 1
)

echo [1/4] Checking application publish...
if /I "%~1"=="/rebuild" goto build_app
if not exist "artifacts\GlassDesk\GlassDesk.exe" goto build_app
if not exist "artifacts\GlassDesk\LICENSE" goto build_app
if not exist "artifacts\GlassDesk\THIRD-PARTY-NOTICES.md" goto build_app
if not exist "artifacts\GlassDesk\licenses\manifest.json" goto build_app
if not exist "artifacts\GlassDesk\licenses\Glasswork\LICENSE" goto build_app
goto app_ready
:build_app
call scripts\build-app.cmd
if errorlevel 1 (
  echo [ERROR] Application build failed. See the output above.
  pause
  exit /b 1
)
:app_ready
if not exist "artifacts\GlassDesk\GlassDesk.exe" (
  echo [ERROR] Published GlassDesk.exe was not found.
  pause
  exit /b 1
)
if not exist "artifacts\GlassDesk\licenses\manifest.json" (
  echo [ERROR] License manifest was not found in the application publish output.
  pause
  exit /b 1
)

echo [2/4] Locating Inno Setup 6 or 7 compiler...
set "ISCC_EXE="
if defined ISCC set "ISCC_EXE=%ISCC:"=%"
if defined ISCC_EXE if not exist "%ISCC_EXE%" set "ISCC_EXE="
if not defined ISCC_EXE if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC_EXE=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC_EXE if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC_EXE=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC_EXE if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC_EXE=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not defined ISCC_EXE if exist "%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe" set "ISCC_EXE=%ProgramFiles(x86)%\Inno Setup 7\ISCC.exe"
if not defined ISCC_EXE if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC_EXE=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if not defined ISCC_EXE if exist "%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe" set "ISCC_EXE=%LOCALAPPDATA%\Programs\Inno Setup 7\ISCC.exe"
if not defined ISCC_EXE if exist ".tools\innosetup-7.1.0\ISCC.exe" set "ISCC_EXE=%CD%\.tools\innosetup-7.1.0\ISCC.exe"
if not defined ISCC_EXE for /f "delims=" %%I in ('where ISCC 2^>nul') do if not defined ISCC_EXE set "ISCC_EXE=%%I"
if not defined ISCC_EXE (
  echo [ERROR] ISCC.exe was not found. Install Inno Setup with: winget install JRSoftware.InnoSetup
  pause
  exit /b 1
)

echo [3/4] Compiling installer...
if not exist "artifacts\installer\." mkdir "artifacts\installer"
if not exist "artifacts\installer\." (
  echo [ERROR] Could not create artifacts/installer.
  pause
  exit /b 1
)
"%ISCC_EXE%" /DPublishDir="%CD%\artifacts\GlassDesk" installer\GlassDesk.iss
if errorlevel 1 (
  echo [ERROR] Installer compilation failed. See the output above.
  pause
  exit /b 1
)
if not exist "artifacts\installer\GlassDesk-0.2.1.3beta-win-x64-setup.exe" (
  echo [ERROR] ISCC reported success but the installer was not created.
  pause
  exit /b 1
)

echo [4/4] Writing SHA-256 checksum...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $path = 'artifacts\installer\GlassDesk-0.2.1.3beta-win-x64-setup.exe'; $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); [IO.File]::WriteAllText($path + '.sha256', ($hash + '  ' + [IO.Path]::GetFileName($path) + [Environment]::NewLine), [Text.Encoding]::ASCII)"
if errorlevel 1 (
  echo [ERROR] SHA-256 generation failed.
  pause
  exit /b 1
)
if not exist "artifacts\installer\GlassDesk-0.2.1.3beta-win-x64-setup.exe.sha256" (
  echo [ERROR] SHA-256 file was not created.
  pause
  exit /b 1
)
echo [4/4] Installer succeeded!
start "" "artifacts\installer"
echo Done.
