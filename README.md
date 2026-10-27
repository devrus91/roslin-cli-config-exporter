# RoslynConfigExporter

[![CI](https://github.com/devrus91/roslin-cli-config-exporter/actions/workflows/ci.yml/badge.svg)](https://github.com/devrus91/roslin-cli-config-exporter/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/RoslynConfigExporter.Tool.svg)](https://www.nuget.org/packages/RoslynConfigExporter.Tool)

CLI-инструмент на Roslyn, который загружает `.sln`, `.slnx` или `.csproj`, семантически находит используемые приложением конфигурационные ключи и экспортирует трассируемый отчёт для локальной разработки и CI.

## Возможности

- `IConfiguration["key"]`, `GetValue<T>`, `GetSection`, `GetRequiredSection`, `GetConnectionString`;
- цепочки и алиасы секций: `config.GetSection("A").GetValue<int>("B")` → `A:B`;
- константы, `nameof`, конкатенация и интерполяция; неизвестные части сохраняются как шаблоны, например `Tenants:{tenantId}:Url`;
- `Configure<T>`, `Bind`, `Get<T>`, `AddOptions<T>().Bind(...)` и `BindConfiguration<T>`;
- рекурсивное разворачивание Options-классов, включая наследование, nullable-типы, массивы, коллекции, словари и `[ConfigurationKeyName]`;
- поиск потребителей `IOptions<T>`, `IOptionsSnapshot<T>` и `IOptionsMonitor<T>`;
- простой межпроцедурный анализ исходных wrapper-методов, пробрасывающих аргумент ключа в стандартный Configuration API;
- чтение IL внешних сборок через reflection без вызова методов приложения: индексатор, `GetValue`, `GetSection`, `GetRequiredSection`, `GetConnectionString`; восстановление родительских секций через аргументы, локальные переменные и поля;
- настраиваемые правила для wrapper-методов из внешних сборок;
- обнаружение стандартных секций Serilog, Microsoft Logging и Kestrel с полными путями; раскрытие конкретных индексов, категорий и endpoint-имён по переданным JSON-файлам;
- Markdown-спецификация с иерархией, типом, обязательностью, требованием переопределения, описанием, возможными значениями, default, примером и env-именем; сначала параметры из исходников, затем из сборок;
- обязательность по nullable-типу, включая `string?`, и требование переопределения по атрибуту `Required`; имена возможных значений enum извлекаются из символов типа;
- описания из `[Description]` и XML `<summary>`, примеры из XML `<example>` в исходниках и XML-файлах сборок; метаданные стандартных настроек связываются с API-символами библиотек;
- значения по умолчанию из `[DefaultValue]` и инициализаторов свойств; массивы и списки выводятся как JSON-массивы, словари — как JSON-объекты, включая пустые коллекции;
- сложные объекты в коллекциях: значения из инициализаторов свойств класса и базовых классов, вложенные объекты и коллекции, приоритет явных присваиваний; `Child = { ... }` сохраняет остальные свойства, `Items = { ... }` добавляет элементы;
- поддержка свойств `IList<T>`, `ICollection<T>` и `IDictionary<TKey, TValue>`, инициализированных через `List<T>` или `Dictionary<TKey, TValue>`;
- env-имена вида `PREFIX_Section__Parameter` с учётом `AddEnvironmentVariables(prefix)` в исходниках и внешних сборках;
- явные отметки неизвестной иерархии и метаданных; исключение неразрешённых открытых generic-типов из отчёта;
- сверка с одним или несколькими `appsettings*.json`: отсутствующие и не сопоставленные параметры;
- JSON, CSV и Markdown; отдельные exit codes для динамических и отсутствующих ключей.

Отчёт хранит не только агрегированные ключи, но и каждое использование: проект, файл, строку, выражение, API, тип значения, Options-тип и уровень уверенности.

## Сборка и запуск

Требуется .NET 10 SDK.

```powershell
dotnet build RoslynConfigExporter.slnx -c Release

dotnet run --project src/RoslynConfigExporter/RoslynConfigExporter.csproj -- `
  path/to/Application.sln `
  --config path/to/config-directory `
  --format json `
  --output configuration-usage-report.json
```

`--config` принимает JSON-файл или директорию. Для директории рекурсивно читаются `appsettings*.json`; параметр можно повторять.

### Установка как dotnet tool

```powershell
dotnet pack src/RoslynConfigExporter/RoslynConfigExporter.csproj -c Release -o artifacts/package
dotnet tool install --global --add-source artifacts/package RoslynConfigExporter.Tool --version 0.4.0

config-usage path/to/Application.sln --config path/to/appsettings.json
```

Все параметры:

```text
config-usage <solution.sln|solution.slnx|project.csproj> [options]

-o, --output <path>       Файл отчёта
-f, --format <format>     json, csv или markdown
-c, --config <path>       JSON-файл/директория конфигурации; можно повторять
-r, --rules <path>        JSON-правила внешних wrapper-методов; можно повторять
    --include-generated   Анализировать сгенерированные C#-файлы
    --fail-on-dynamic     Exit code 2, если остались динамические ключи
    --fail-on-missing     Exit code 3, если точных ключей нет в переданных config-файлах
```

## Внешние wrapper-методы

Для внешних DLL автоматически проверяются тела методов через reflection. Прямые чтения с константными ключами отмечаются Inferred, неизвестные ключи — Dynamic. Поддерживаются простые цепочки секций и локальные переменные. В location хранится путь DLL, line = 0, column = IL offset. Анализ охватывает методы сборки без доказательства их достижимости из приложения. Аргументы вызовов из исходника передаются в анализ внешнего метода; внутри одной DLL отслеживаются простые вызовы, возвращаемые секции и поля экземпляра, заполненные конструктором. Рекурсия ограничена 12 уровнями; сложные ветвления и неоднозначные перегрузки могут оставить контекст неизвестным. Для дополнительных wrapper-методов можно описать правило:

```json
{
  "methods": [
    {
      "method": "Company.Configuration.ConfigReader.Read",
      "keyArgument": 1,
      "valueTypeArgument": 0,
      "kind": "WrapperCall"
    },
    {
      "method": "Company.Configuration.Secrets.Get*",
      "keyArgument": 0,
      "prefix": "Secrets"
    }
  ]
}
```

`method` — полное имя типа и метода; поддерживается `*`. Индексы считаются по аргументам, написанным в месте вызова. Если метод читает фиксированную секцию и не получает ключ отдельным аргументом, задайте `keyArgument: -1` и полный путь в `prefix`. Пример находится в `external-wrapper-rules.example.json`.

## Уровни уверенности

- `Exact` — ключ полностью вычислен компилятором или следует из известного API;
- `Inferred` — ключ восстановлен через wrapper или другую межпроцедурную связь;
- `Pattern` — коллекция/словарь, например `Features:Definitions:*:Enabled`;
- `Dynamic` — часть ключа зависит от runtime-значения.

## Что статический анализ принципиально не гарантирует

- значения ключей, полностью создаваемые reflection, `dynamic`, runtime-кодом или внешним процессом;
- сложные wrapper-методы с ветвящимися преобразованиями аргумента; для них рекомендуется внешнее правило;
- полное покрытие сторонних сборок: нужны DLL с телами методов и доступные зависимости; reference assemblies, сложный поток данных, переходы между методами и runtime reflection полностью не восстанавливаются; ошибки загрузки отражаются в Issues;
- фактическое наличие значений в environment variables, Key Vault, Consul и других providers — сравнение JSON проверяет только переданные файлы;
- все варианты multi-target проекта за один запуск: `MSBuildWorkspace` загружает выбранную MSBuild-конфигурацию проекта.

Такие случаи не маскируются: неизвестные выражения остаются в отчёте как `Dynamic`. Для CI можно включить `--fail-on-dynamic` и добавить wrapper rules.

## Что уже существует

Перед разработкой были проверены близкие решения:

- [Appsettings Analyzer](https://marketplace.visualstudio.com/items?itemName=AntonStepanov.AppsettingsAnalyzer) умеет подсвечивать используемые/неиспользуемые ключи в Visual Studio и знает базовые Options-паттерны, но не предоставляет описанный переносимый CLI/CI-экспорт;
- официальный [.NET configuration binding source generator](https://learn.microsoft.com/dotnet/core/extensions/configuration-generator) генерирует AOT-friendly binding-код, но не является инвентаризатором используемых ключей;
- [Roslynator CLI](https://github.com/dotnet/roslynator) — универсальная инфраструктура анализа, но готового правила экспорта ключей `IConfiguration` в ней нет;
- `Cocoar.Configuration.Analyzers` относится прежде всего к собственной strongly-typed конфигурационной модели Cocoar, а не к универсальному отчёту по обычному `IConfiguration` solution.

## Проверка

```powershell
dotnet test RoslynConfigExporter.slnx -c Release
dotnet build samples/SampleApp/SampleApp.csproj -c Release
```

Sample покрывает прямые ключи, секции, Options, `[ConfigurationKeyName]`, коллекции/словари, connection strings, исходный wrapper, три вида `IOptions`, динамический tenant key и Serilog, читающий настройки из `appsettings.json`. Для стандартного Serilog внешние правила больше не нужны; `samples/SampleApp/config-usage.rules.json` оставлен как пример ручных правил.

## Markdown-спецификация

`--format markdown` формирует таблицу: «Иерархия в спецификации», «Параметр», «Тип значения», «Обязательный (nullable)», «Требует переопределения (required)», «Описание», «Возможные значения», «Значение по умолчанию», «Пример значения», «Источник (env/ехс/...)».

- Иерархия — путь родительской секции, параметр — последний сегмент ключа. Корневые секции отмечаются «Корень». Не восстановленный контекст DLL отмечается «Иерархия неизвестна», при наличии показывается известная часть пути. В JSON неизвестный родитель представлен `{unknown-parent}`; такие пути не сопоставляются с JSON-конфигурацией.
- «Обязательный»: Нет для `Nullable<T>` и nullable reference types (`string?` и т. п.), Да для остальных разрешённых типов. Nullable-аннотации учитываются и в метаданных DLL. «Требует переопределения»: Нет при отсутствии `Required` или явном boolean false в аргументе Required; иначе Да. Для стандартного `[Required]` свойство `AllowEmptyStrings = false` не отключает требование. Ключевое слово C# `required` само по себе на эту колонку не влияет.
- Описание: `[Description]`, затем XML `<summary>` свойства в исходнике или в XML-файле рядом с DLL.
- Значение по умолчанию: `[DefaultValue]`, затем явный инициализатор свойства в исходнике. Константы вычисляются семантически, остальные инициализаторы показываются с пометкой «Выражение». Также учитывается явный default в `GetValue` в исходнике. Инициализаторы в скомпилированных DLL не исполняются и не восстанавливаются; для них используйте `[DefaultValue]`.
- Возможные значения для enum берутся из имён его констант в анализируемой версии сборки, включая aliases и flags. Пример берётся из `<example>` XML-комментария или XML-файла сборки; неизвестные поля отмечены «Неизвестно».
- Источник в Markdown содержит `env: PREFIX_Section__Parameter`. Учитываются строковые prefix из `AddEnvironmentVariables` в исходниках и IL внешних сборок, включая аргументы внешних wrapper-методов. Несколько найденных в проекте провайдеров дают несколько вариантов; связь с отдельными экземплярами IConfiguration и runtime-приоритет не устанавливаются. Динамический prefix и неизвестная иерархия отмечаются явно. Если регистраций не найдено, используется стандартное имя без prefix. Наличие переменных среды не проверяется. JSON/CSV сохраняют сведения о совпадениях с конфигурационными файлами.

Дополнительные метаданные доступны и в JSON в `usages[].metadata`.

### Значения по умолчанию коллекций

Массивы и списки из инициализаторов выводятся как JSON-массивы, словари — как JSON-объекты. Поддерживаются `[]`, `Array.Empty<T>()`, `new List<T>()`, `new Dictionary<TKey, TValue>()`, сокращённое `new()` и непустые инициализаторы с константными элементами. Свойства `IList<T>`, `ICollection<T>` и `IDictionary<TKey, TValue>` поддерживаются при инициализации через `List<T>` или `Dictionary<TKey, TValue>`.

Для объектов внутри коллекций учитываются инициализаторы публичных свойств класса и базовых классов. Явные присваивания в инициализаторе объекта имеют приоритет, включая `null`. Вложенная запись `Child = { Name = "override" }` сохраняет остальные значения существующего объекта. Запись `Items = { 3 }` добавляет элементы к исходному списку; словари поддерживают добавление `{ "key", value }` и замену через `["key"] = value`. Повторный ключ при добавлении пары не превращается в замену: такой инициализатор сохраняется как «Выражение».

JSON сложной коллекции показывается в отдельной строке; значения конкретных элементов не подменяют значения по умолчанию отдельных свойств в строках с `*`. Описание и пример элементов коллекций наследуются из XML-комментариев свойства коллекции, если собственные отсутствуют.

Конструкторы, методы и getters приложения не выполняются. Если значение нельзя восстановить статически или достигнут предел рекурсии, сохраняется исходное выражение вместо неполного JSON. Значения из тел конструкторов и CLR defaults не добавляются автоматически.

[Пример Markdown-отчёта](samples/configuration-report.md) можно пересоздать:

```powershell
dotnet run --project src/RoslynConfigExporter -c Release -- samples/SampleApp/SampleApp.csproj --format markdown --config samples/SampleApp/appsettings.json --output samples/configuration-report.md
```

### Стандартные секции Serilog, Microsoft Logging и Kestrel

При обнаружении `ReadFrom.Configuration`/`ConfigurationSection` применяется встроенная схема Serilog: `MinimumLevel`, `WriteTo`, overrides, sinks и другие стандартные пути. Учитываются родительская секция IConfiguration, аргумент `sectionName` и `ConfigurationReaderOptions.SectionName` в инициализаторе. Неизвестные options сохраняют неизвестный путь. Чтения через wrapper в DLL также обрабатываются. Непривязанный обход внутренних методов Serilog заменён этой схемой, чтобы не создавать дубли без контекста.

`WebApplication.CreateBuilder`, `Host.CreateDefaultBuilder` и `Host.CreateApplicationBuilder` раскрывают стандартную секцию `Logging`. Явный `logging.AddConfiguration(section)` использует переданный путь. Включены `LogLevel:Default`, категории и уровни для отдельных providers.

Для `WebApplication.CreateBuilder`/`CreateSlimBuilder` и `ConfigureWebHostDefaults` обнаруживается `Kestrel`. Явный `KestrelServerOptions.Configure(section)` использует указанную секцию. Схема включает endpoints, протоколы, SNI и сертификаты. `Limits` не добавляется автоматически: стандартный загрузчик секции его не привязывает; нужен явный binding/чтение в коде.

Эти пути отмечаются `Inferred`/`Pattern`: это поддерживаемые настройки API, а не утверждение, что каждая настройка задана или обязательна. По переданным JSON дополнительно раскрываются конкретные пути массивов, endpoint-имена и категории, например `Serilog:WriteTo:0:Name` и `Kestrel:Endpoints:Http:Url`, с соответствующими env-именами. Возможные runtime-переопределения регистрации не моделируются.

Источники схем: [Serilog Settings Configuration](https://github.com/serilog/serilog-settings-configuration), [Microsoft Logging](https://learn.microsoft.com/aspnet/core/fundamentals/logging/), [Kestrel configuration reader](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Servers/Kestrel/Core/src/Internal/ConfigurationReader.cs).

Метаданные стандартных схем связываются с API-символами библиотек: типом enum, свойством или параметром метода. Описания читаются из `[Description]`/XML, а не из вручную написанного словаря. Default извлекается из атрибута, инициализатора, optional-параметра либо явного XML-утверждения о default; свободный текст XML помечается `XML:`. Значения из appsettings не считаются default или example. Если точного соответствия или документации нет, поле остаётся неизвестным; динамические аргументы sinks могут иметь тип `object`.

Markdown сначала выводит «Найдено в исходниках», затем «Найдено в сборках» (включая схемы стандартных библиотек). Ключ, найденный обоими способами, показывается один раз в группе исходников. Происхождение хранится в JSON как `origin`. Неразрешённые открытые generic-типы (`T`, `List<T>` и т. п.) исключены из отчёта; конкретные вызовы wrapper-методов сохраняются.
