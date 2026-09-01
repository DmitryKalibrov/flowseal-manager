# Стандарт обновлений Flowseal Manager

`update-manifest.json` имеет схему версии 1:

```json
{
  "schemaVersion": 1,
  "releaseVersion": "1.1.0",
  "buildVersion": "1.2.13.0",
  "packages": [
    {
      "runtimeIdentifier": "win-x64",
      "assetName": "FlowsealManager-win-x64.zip",
      "sha256": "64 шестнадцатеричных символа",
      "size": 123456789,
      "executable": "FlowsealManager.exe"
    },
    {
      "runtimeIdentifier": "win-arm64",
      "assetName": "FlowsealManager-win-arm64.zip",
      "sha256": "64 шестнадцатеричных символа",
      "size": 123456789,
      "executable": "FlowsealManager.exe"
    }
  ]
}
```

Правила:

- `releaseVersion` обязан совпадать с тегом GitHub Release без префикса `v`;
- поддерживаются только стабильные версии `X.Y.Z` и архитектуры `win-x64`, `win-arm64`;
- имена пакетов фиксированы и не берутся из произвольного URL;
- каждый ZIP содержит `FlowsealManager.exe` в корне;
- размер ZIP и SHA-256 должны совпасть с GitHub Asset и манифестом;
- обновление всегда выполняется из временной копии приложения;
- до первого успешного запуска новой версии сохраняется резервная копия заменённых файлов;
- настройки и компоненты в `%LocalAppData%\FlowsealManager` и `%ProgramData%\FlowsealManager` не входят в пакет.
