@echo off
rem GlassDesk 0.2.1.3beta one-click build (Windows + VS 2022 C++ Desktop + .NET SDK)
rem Double-click to run. Any step that fails stops here and shows the error.
rem NOTE: This file cannot run on Linux; run it on Windows by double-clicking.
setlocal
cd /d "%~dp0"
if not exist "scripts\build-app.cmd" (
  echo [ERROR] scripts/build-app.cmd not found. Make sure this bat file is in the project root.
  pause
  exit /b 1
)
call scripts\build-app.cmd
if errorlevel 1 (
  echo [ERROR] Build failed. See the output above.
  pause
  exit /b 1
)
echo [5/5] Build succeeded! Opening artifacts\GlassDesk (double-click GlassDesk.exe to run)...
start "" "artifacts\GlassDesk"
echo Done.
pause
