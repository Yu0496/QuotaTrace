@echo off
chcp 65001 >nul
python -B "%~dp0tools\artifact_clean.py" clean --interactive
set "ARTIFACT_CLEAN_EXIT=%errorlevel%"
pause
exit /b %ARTIFACT_CLEAN_EXIT%
