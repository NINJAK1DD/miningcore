@echo off
cd src\Miningcore
dotnet publish -c Release --framework net10.0 -o ../../build
