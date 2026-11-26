@echo off
rem Starts the Seamstress MCP server (src/Seamstress.Mcp) for the Claude Code plugin.
rem
rem Where the server is looked for, first match wins:
rem   1. SEAMSTRESS_MCP   the server executable itself (for a copy built with -o elsewhere)
rem   2. SEAMSTRESS_REPO  a Seamstress checkout
rem   3. the project Claude Code is working in, when it is a Seamstress checkout
rem   4. the checkout this plugin folder sits in (when it is loaded in place)
rem The server must be built first: dotnet build src/Seamstress.Mcp -c Release
setlocal EnableExtensions

set "REL=src\Seamstress.Mcp\bin\Release\net8.0\Seamstress.Mcp.exe"
set "EXE="
set "REPO="

if not "%SEAMSTRESS_MCP%"=="" (
  set "EXE=%SEAMSTRESS_MCP%"
  goto found
)
if not "%SEAMSTRESS_REPO%"=="" if exist "%SEAMSTRESS_REPO%\Seamstress.slnx" (
  set "REPO=%SEAMSTRESS_REPO%"
  goto repo
)
if not "%SEAMSTRESS_PROJECT_DIR%"=="" if exist "%SEAMSTRESS_PROJECT_DIR%\Seamstress.slnx" (
  set "REPO=%SEAMSTRESS_PROJECT_DIR%"
  goto repo
)
for %%I in ("%~dp0..\..") do set "UP=%%~fI"
if exist "%UP%\Seamstress.slnx" (
  set "REPO=%UP%"
  goto repo
)

>&2 echo [seamstress plugin] Could not find a Seamstress checkout. Set SEAMSTRESS_REPO to the folder that holds
>&2 echo [seamstress plugin] Seamstress.slnx, or SEAMSTRESS_MCP to a built Seamstress.Mcp.exe, and restart Claude Code.
exit /b 1

:repo
set "EXE=%REPO%\%REL%"

:found
if not exist "%EXE%" (
  >&2 echo [seamstress plugin] The Seamstress MCP server is not built yet: "%EXE%" is missing.
  >&2 echo [seamstress plugin] Build it in the checkout with: dotnet build src/Seamstress.Mcp -c Release
  exit /b 1
)

rem A .NET installed per user (dotnet-install.ps1) is not on the machine-wide path: point the app host at it.
if "%DOTNET_ROOT%"=="" if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET_ROOT=%USERPROFILE%\.dotnet"

"%EXE%" %*
exit /b %ERRORLEVEL%
