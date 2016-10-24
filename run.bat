@echo off

call "initialize.bat"
"packages\FAKE\tools\Fake.exe" .\package.fsx source=%1 output=%2 || EXIT /B 1
