@echo off
set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%PATH%"
cd /d "%~dp0"
if exist "%~dp0AcrobatPdfEditor\bin\Release\net8.0-windows\AcroCatPdfEditor.exe" (
    start "" "%~dp0AcrobatPdfEditor\bin\Release\net8.0-windows\AcroCatPdfEditor.exe"
) else (
    start "" "%~dp0AcrobatPdfEditor\bin\Release\net8.0-windows\AcrobatPdfEditor.exe"
)
