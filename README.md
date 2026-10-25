# Fitness Club API

Нужны .NET 9 SDK и Docker.

## Запуск

```
docker compose up -d
dotnet run --project WebApi
```

Приложение слушает `http://localhost:5080`, Swagger — `http://localhost:5080/swagger`,
проверка живости — `http://localhost:5080/health`. Миграции и начальные данные применяются
при старте.

Занятия в начальных данных расставлены относительно момента первого запуска. Чтобы получить
свежий набор, пересоздайте базу:

```
docker compose down -v
docker compose up -d
```

## Миграции

```
dotnet tool restore
dotnet dotnet-ef migrations add <Name> --project DataAccessLayer --startup-project WebApi
```

Применяются при следующем запуске приложения.

## Тесты

```
dotnet test
```

Тестам нужны запущенные Postgres и Redis из `docker compose`. База для прогона создаётся
отдельная и удаляется после.

## Учётные данные

Пароль у всех пользователей — `Password123!`.

| Роль | Логины |
|---|---|
| `user` | `user001@club.test` … `user200@club.test` |
| `admin` | `admin001@club.test` … `admin005@club.test` |

Вход — `POST /auth/login` с телом `{ "email": "...", "password": "..." }`,
в ответе `accessToken`.
