@echo off
setlocal
if exist "%LOCALAPPDATA%\Programs\AcroCatPdfEditor\Uninstall.exe" (
    start "" "%LOCALAPPDATA%\Programs\AcroCatPdfEditor\Uninstall.exe" --uninstall
    exit /b 0
)
if exist "%LOCALAPPDATA%\Programs\ArcoCatPdfEditor\Uninstall.exe" (
    start "" "%LOCALAPPDATA%\Programs\ArcoCatPdfEditor\Uninstall.exe" --uninstall
    exit /b 0
)
if exist "%LOCALAPPDATA%\Programs\AcrobatPdfEditor\Uninstall.exe" (
    start "" "%LOCALAPPDATA%\Programs\AcrobatPdfEditor\Uninstall.exe" --uninstall
    exit /b 0
)
if exist "%~dp0AcroCatPdfEditor_Setup.exe" (
    start "" "%~dp0AcroCatPdfEditor_Setup.exe" --uninstall
    exit /b 0
)
if exist "%~dp0ArcoCatPdfEditor_Setup.exe" (
    start "" "%~dp0ArcoCatPdfEditor_Setup.exe" --uninstall
    exit /b 0
)
if exist "%~dp0AcrobatPdfEditor_Setup.exe" (
    start "" "%~dp0AcrobatPdfEditor_Setup.exe" --uninstall
    exit /b 0
)
if exist "%~dp0installer\Output\AcroCatPdfEditor_Setup.exe" (
    start "" "%~dp0installer\Output\AcroCatPdfEditor_Setup.exe" --uninstall
    exit /b 0
)
if exist "%~dp0installer\Output\ArcoCatPdfEditor_Setup.exe" (
    start "" "%~dp0installer\Output\ArcoCatPdfEditor_Setup.exe" --uninstall
    exit /b 0
)
echo ArcoCat PDF Editor is not currently installed.
pause
