@echo off
setlocal

set SCRIPT_DIR=%~dp0
pushd "%SCRIPT_DIR%"

dotnet tool install --global wix

set PUBLISH_DIR=%SCRIPT_DIR%..\MerlinManager\bin\Release\publish\
dotnet publish ..\MerlinManager\MerlinManager.csproj -c Release -r win-x64 --self-contained true -p:SatelliteResourceLanguages=none -p:PublishDir="%PUBLISH_DIR%"
if errorlevel 1 (
  echo Publish failed.
  popd
  exit /b 1
)

dotnet build MerlinManager.Installer.wixproj -c Release -p:Platform=x64
if errorlevel 1 (
  echo Build failed.
  popd
  exit /b 1
)

echo MSI generated: %SCRIPT_DIR%..\MerlinManager.msi

popd
