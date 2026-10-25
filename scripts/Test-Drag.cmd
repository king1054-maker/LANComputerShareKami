@echo off
cd /d "%~dp0"
start /wait "" LanBridge.exe --self-test-drag
if exist drag-self-test.txt type drag-self-test.txt
pause
