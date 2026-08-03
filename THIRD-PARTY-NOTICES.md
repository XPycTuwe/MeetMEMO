# Сторонние компоненты

MeetMemo распространяется под GNU AGPL-3.0 и использует перечисленные ниже открытые
компоненты. Все они под MIT или Apache-2.0 — эти лицензии совместимы с AGPL-3.0
и допускают в том числе коммерческое использование.

## Библиотеки

### sherpa-onnx — Apache License 2.0
Движок распознавания речи и Silero VAD.
https://github.com/k2-fsa/sherpa-onnx

Copyright (c) 2022-2026 Xiaomi Corporation

### Whisper.net — MIT License
Обвязка .NET для whisper.cpp, финальный проход распознавания.
https://github.com/sandrohanea/whisper.net

Copyright (c) Sandro Hanea

### whisper.cpp / ggml — MIT License
Нативный движок Whisper, поставляется внутри Whisper.net.Runtime.
https://github.com/ggml-org/whisper.cpp

Copyright (c) 2023-2026 The ggml authors

### NAudio — MIT License
Захват микрофона и системного звука через WASAPI.
https://github.com/naudio/NAudio

Copyright (c) Mark Heath

### Hardcodet.NotifyIcon.Wpf — MIT License
Значок в области уведомлений.
https://github.com/hardcodet/wpf-notifyicon

Версия 2.0.1 распространяется под MIT (проверено в метаданных пакета). Более ранние
версии выходили под Code Project Open License, которая для коммерческой поставки
подходит хуже — при откате версии это нужно перепроверить.

### CommunityToolkit.Mvvm — MIT License
https://github.com/CommunityToolkit/dotnet

Copyright (c) .NET Foundation and Contributors

### Serilog — Apache License 2.0
Журналирование.
https://github.com/serilog/serilog

## Модели

### GigaAM v2 — MIT License
Русская модель распознавания речи (SberDevices).
https://github.com/salute-developers/GigaAM

Copyright (c) 2024 GigaChat Team

Используется конвертация для sherpa-onnx от **2025-04-19**
(`sherpa-onnx-nemo-ctc-giga-am-v2-russian-2025-04-19`). В карточке этого репозитория
лицензия не проставлена, но приложенный файл LICENSE ссылается на MIT-лицензию
оригинального GigaAM — проверено 03.08.2026.

> **Важно при обновлении.** Ранняя конвертация GigaAM v1 (`giga-am-russian-2024-10-24`)
> распространяется по **некоммерческой** лицензии (GigaAM License_NC) — документация
> sherpa-onnx помечает её «for non-commercial use only». В коммерческую поставку она
> попадать не должна. Всегда сверяйте версию модели: отличается только дата в названии.

### Silero VAD — MIT License
Определение границ речи.
https://github.com/snakers4/silero-vad

Copyright (c) 2024 Silero Team

### Whisper (ggml) — MIT License
Модель `ggml-large-v3-turbo-q5_0` для финального прохода.
https://huggingface.co/ggerganov/whisper.cpp

Copyright (c) 2022 OpenAI (веса модели), сборка ggml — The ggml authors

## Тексты лицензий

Полные тексты MIT и Apache 2.0 доступны по адресам:
- MIT: https://opensource.org/licenses/MIT
- Apache 2.0: https://www.apache.org/licenses/LICENSE-2.0
