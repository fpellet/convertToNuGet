@echo off

call ".paket/paket.bootstrapper.exe" --max-file-age=10080
call ".paket/paket.exe" restore

