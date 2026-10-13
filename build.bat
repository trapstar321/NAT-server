@echo off
setlocal EnableDelayedExpansion
cd /d "%~dp0"
echo Restoring NatTest.sln...
dotnet restore NatTest.sln
if errorlevel 1 goto failed
echo Building Release...
dotnet build NatTest.sln --configuration Release --no-restore
if errorlevel 1 goto failed
for %%A in (Introducer Server Client) do (
  if /I "%%A"=="Introducer" set "APP=introducer"
  if /I "%%A"=="Server" set "APP=server"
  if /I "%%A"=="Client" set "APP=client"
  echo Publishing %%A...
  dotnet publish "src\%%A\%%A.csproj" --configuration Release --no-build --framework net472 --output "artifacts\!APP!"
  if errorlevel 1 goto failed
)
echo.
echo Build and publish succeeded.
echo %~dp0artifacts\introducer\introducer.exe
echo %~dp0artifacts\server\server.exe
echo %~dp0artifacts\client\client.exe
exit /b 0
:failed
echo Build failed. See the first error above.
exit /b 1
