@echo off
rem FL Automate one-click deploy - Windows launcher.
rem Runs install-all.sh through Git Bash (ships with Git for Windows).
rem Usage is identical to the .sh:  install-all.cmd [--only site] [--skip-build]
setlocal

set "BASH=%ProgramFiles%\Git\bin\bash.exe"
if not exist "%BASH%" set "BASH=%ProgramFiles(x86)%\Git\bin\bash.exe"
if not exist "%BASH%" set "BASH=%LocalAppData%\Programs\Git\bin\bash.exe"
if not exist "%BASH%" (
  echo ERROR: Git Bash not found. Install Git for Windows: https://git-scm.com/download/win
  exit /b 1
)

"%BASH%" --noprofile --norc "%~dp0install-all.sh" %*
exit /b %errorlevel%
