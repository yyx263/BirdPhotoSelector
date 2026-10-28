@echo off
setlocal
set "BirdPhotoSelectorExe=%~dp0BirdPhotoSelector.exe"
if not exist "%BirdPhotoSelectorExe%" set "BirdPhotoSelectorExe=%~dp0publish\BirdPhotoSelector\BirdPhotoSelector.exe"
start "" "%BirdPhotoSelectorExe%" --software-rendering %*
