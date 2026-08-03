# Сборка MeetMemo.
# .NET 8 SDK установлен в профиль пользователя (без прав администратора), поэтому
# используем его явно: системный dotnet.exe содержит только рантайм.
$ErrorActionPreference = 'Stop'
$sdk = "$env:LOCALAPPDATA\dotnet-sdk\dotnet.exe"
if (-not (Test-Path $sdk)) { $sdk = "dotnet" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
& $sdk @args
exit $LASTEXITCODE
