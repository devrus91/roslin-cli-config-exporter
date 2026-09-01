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
- настраиваемые правила для wrapper-методов из внешних сборок;
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
dotnet tool install --global --add-source artifacts/package RoslynConfigExporter.Tool --version 0.1.0

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

Roslyn не может узнать поведение метода из скомпилированной сторонней библиотеки. Для него можно описать правило:

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
- конфигурацию, читаемую внутри сторонней сборки, если её исходники не входят в анализируемый solution;
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

Sample покрывает прямые ключи, секции, Options, `[ConfigurationKeyName]`, коллекции/словари, connection strings, исходный wrapper, три вида `IOptions`, динамический tenant key и Serilog, читающий настройки из `appsettings.json`. Для внешней сборки Serilog используются правила из `samples/SampleApp/config-usage.rules.json`.
