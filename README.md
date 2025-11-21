# BuhWise

Локальное Windows-приложение для ведения бухгалтерии по USD, EUR и RUB. Центральная валюта для эквивалентов и отчётов — USD.

## Документация
- [Требования](requirements.md)
- [План разработки](plan.md)

## Структура проекта
- `BuhWise.sln` — решение Visual Studio.
- `src/BuhWise.App` — WPF-приложение, работает с локальной SQLite базой `buhwise.db` (создаётся рядом с исполняемым файлом).

## Сборка и запуск (Windows)
1. Установите [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) и Visual Studio 2022 (Desktop development with C#) либо используйте CLI.
2. Откройте `BuhWise.sln` в Visual Studio и запустите проект **BuhWise.App** в конфигурации `Debug|Any CPU`.
   - CLI-альтернатива: из корня репозитория выполните
     ```powershell
     dotnet restore BuhWise.sln
     dotnet build BuhWise.sln -c Debug
     dotnet run --project src/BuhWise.App/BuhWise.App.csproj
     ```
3. Приложение создаст файл `buhwise.db` в директории сборки, таблицы `Balances`, `Rates`, `Operations` заполнятся начальными значениями.
4. В верхней панели доступны текущие балансы USD/EUR/RUB; ниже — форма добавления операции (пополнение, расход, обмен) и таблица истории.
