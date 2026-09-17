@echo off
@chcp 65001 >nul
cd /d "%~dp0"
set "LOG=%~dp0build-log.txt"

echo ================================================= > "%LOG%"
echo Run started: %DATE% %TIME% >> "%LOG%"
echo ================================================= >> "%LOG%"

echo Stopping running instance...
REM Stop the running app FIRST: it runs straight out of dist\, so the compiler
REM cannot overwrite dist\CodexClaudeUsage.exe while it is still locked.
taskkill /im CodexClaudeUsage.exe /f >> "%LOG%" 2>&1
ping 127.0.0.1 -n 3 >nul

echo Building...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Clean >> "%LOG%" 2>&1
set "BUILD_ERR=%ERRORLEVEL%"

type "%LOG%"

if not "%BUILD_ERR%"=="0" (
    echo.
    echo BUILD FAILED ^(exit code %BUILD_ERR%^) - see the messages above.
    echo A copy of this output is saved to: %LOG%
    echo.
    pause
    exit /b 1
)

if not exist "%~dp0dist\CodexClaudeUsage.exe" (
    echo.
    echo BUILD PRODUCED NO EXECUTABLE - see the messages above.
    echo A copy of this output is saved to: %LOG%
    echo.
    pause
    exit /b 1
)

set "INSTALLED=%LOCALAPPDATA%\Programs\CodexClaudeUsage"
if exist "%INSTALLED%\CodexClaudeUsage.exe" (
    copy /y "%~dp0dist\CodexClaudeUsage.exe" "%INSTALLED%\CodexClaudeUsage.exe" >> "%LOG%" 2>&1
    copy /y "%~dp0config.json" "%INSTALLED%\config.json" >> "%LOG%" 2>&1
    echo Installed copy updated.
)

start "" "%~dp0dist\CodexClaudeUsage.exe" --background
echo. >> "%LOG%"
echo Deployed and restarted OK. >> "%LOG%"
echo.
echo Done - rebuilt and restarted.
echo A copy of this output is saved to: %LOG%
echo.
pause
