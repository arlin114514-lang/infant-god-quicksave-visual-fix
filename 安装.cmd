@echo off
chcp 65001 >nul
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
if errorlevel 1 echo 安装未完成，请保留错误信息。
pause
