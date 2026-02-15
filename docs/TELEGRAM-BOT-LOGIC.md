# Telegram Bot Logic

## Сообщение «Ты взял купон» (handleTake)

При взятии купона (команда `/take`, кнопка «Взять N» или `take:{id}`) бот шлёт **одно** сообщение:

- **SendPhoto** с `caption` и `replyMarkup` (не SendPhoto + отдельный SendMessage).
- Caption: `Ты взял купон {id}: {v} EUR, истекает {d}`.
- Кнопки: «Вернуть», «Использован» — `singleTakenKeyboard(coupon)`.
- Callback: `return:{id}:del` и `used:{id}:del`. Суффикс **`:del`** = при успешном действии удалять **это** сообщение (`DeleteMessage`), чтобы в чате не оставалось ни фото, ни кнопок.

## Команда /my (handleMy)

- Показываем **только «Мои взятые»** (не «Мои добавленные»).
- Список строится из `GetCouponsTakenBy`; под каждым купоном — кнопки «Вернуть» и «Использован».
- Callback: `return:{id}` и `used:{id}` **без** `:del` — сообщение не удаляем (список может содержать несколько купонов).

## Обработка callback (return / used)

- `return:{id}` или `return:{id}:del` — `handleReturn`; при `:del` и успехе (`ok`) — `DeleteMessage(cq.Message)` в `try/with`.
- `used:{id}` или `used:{id}:del` — `handleUsed`; при `:del` и успехе — `DeleteMessage(cq.Message)` в `try/with`.
- Парсинг: `deleteOnSuccess = cq.Data.EndsWith(":del")`, для извлечения `id` отрезать `:del` и взять число после `return:` / `used:`.
- `handleReturn` и `handleUsed` возвращают `bool` (успех операции), чтобы callback решал, вызывать ли `DeleteMessage`.

## GetCouponsTakenBy (DbService)

- Обязательно: `WHERE taken_by = @user_id AND status = 'taken'`.

## /add wizard + OCR

### Общий принцип

- `CouponOcrEngine` делает barcode + Azure text OCR и возвращает `CouponOCR` как "есть значение / нет значения".
- **Любой результат OCR — это лишь предзаполнение**: пользователь всё равно подтверждает финальные значения перед `AddCoupon`.

### Стадии wizard (PendingAddFlow.stage)

Базовые:
- `awaiting_photo`
- `awaiting_discount_choice`
- `awaiting_date_choice`
- `awaiting_confirm`

OCR-ветка:
- `awaiting_ocr_confirm` — когда OCR распознал value + min_check + expires_at и просим "Да/Нет".

### Ввод пользователя в wizard (без кнопок "other")

На шагах выбора пользователь может **либо нажать кнопки**, либо **ввести текстом**:

- **Скидка + минимальный чек** (на `awaiting_discount_choice`):
  - Формат `X Y` (например `10 50`)
  - Формат `X/Y` (например `10/50`, пробелы вокруг `/` допустимы)
- **Дата истечения** (на `awaiting_date_choice`):
  - Полная дата (как раньше): `25.01.2026`, `2026-01-25`, `yyyy/MM/dd`, и т.п.
  - Упрощённо: **одно число 1..31** (например `25`) — трактуем как **следующее** такое число **строго в будущем** (UTC). Если в ближайшем месяце такого дня нет, пропускаем месяц и ищем дальше.

### Partial OCR (обязательное поведение)

Если OCR распознал частично, **не откатываться "в ноль"**, а продолжать с первого недостающего шага, сохранив то, что нашли:

- распознаны `value` и `min_check`, но нет `expires_at` → перейти в `awaiting_date_choice`.
- распознана только `expires_at` → сохранить её, перейти в `awaiting_discount_choice`; после ввода скидки/чека можно сразу перейти в `awaiting_confirm`.
- `barcode_text` опционален и **никогда не блокирует** добавление.

### Callback-префиксы в /add wizard

- `addflow:disc:<value>:<min_check>` — выбрать скидку и мин. чек.
- `addflow:date:today|tomorrow` — выбрать дату.
- `addflow:ocr:yes|no` — подтвердить/отклонить OCR-предзаполнение (актуально только для `awaiting_ocr_confirm`).
- `addflow:confirm` — создать купон.
- `addflow:cancel` — отмена wizard.
