# Coupon Hub Bot

Telegram бот для управления купонами Dunnes в закрытом сообществе.

## Архитектура

- **F#/.NET 10** - основной язык и фреймворк
- **PostgreSQL** - база данных
- **Dapper** - ORM для работы с БД
- **Telegram.Bot 22.8.1** - библиотека для работы с Telegram API
- **ASP.NET Core Minimal API** - веб-сервер для webhook
- **Docker** - контейнеризация
- **GitHub Actions** - CI/CD

## Основные возможности

### Команды в личке (DM)

- `/add` - добавить купон (с фото, значением и датой истечения)
- `/coupons` - список доступных купонов
- `/take <id>` - взять купон (транзакционно, предотвращает двойное взятие) — либо нажать кнопку «Взять N» в `/coupons`
- `/used <id>` - отметить купон как использованный
- `/return <id>` - вернуть купон в доступные
- `/my` - мои взятые купоны с кнопками «Вернуть» и «Использован»
- `/stats` - статистика пользователя
- `/help` - справка

### Уведомления в группе

Бот автоматически отправляет уведомления в закрытый чат сообщества:
- Добавление купона
- Взятие купона
- Использование купона
- Возврат купона
- Утренние напоминания об истекающих купонах

## Разработка

См. [README.dev.md](README.dev.md) для инструкций по локальной разработке.

## Деплой

Деплой настроен через GitHub Actions (`.github/workflows/deploy.yml`):

1. Запускает тесты
2. Применяет миграции Flyway к продакшн БД
3. Билдит Docker image для `linux/amd64`
4. Пушит в GitHub Container Registry: `ghcr.io/szer/coupon-bot`

### Требуемые Secrets

- `WIREGUARD_CONFIG` - конфигурация VPN для доступа к продакшн БД
- `DB_PROD_URL` - URL продакшн базы данных
- `DB_PROD_USERNAME` - пользователь БД
- `DB_PROD_PASSWORD` - пароль БД

### Использование образа

```bash
docker pull ghcr.io/szer/coupon-bot:latest
docker run -e BOT_TELEGRAM_TOKEN=... -e DATABASE_URL=... ghcr.io/szer/coupon-bot:latest
```

## Лицензия

MIT
