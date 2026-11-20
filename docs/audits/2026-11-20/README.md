# Аудит tgcli: стабільність, актуальність і простий CLI

Дата: 20.11.2026. Результат для перегляду Андрієм, **не прийнятий план реалізації**. Продуктовий код під час аудиту не змінювався. Реліз, commit і push не виконувалися.

## Висновок

Локальні виправлення 7.0.1 закривають конкретні причини інциденту зі стартовою синхронізацією та короткими сторінками звичайного експорту. Проте контракт «звичайний запит повертає актуальну відповідь або чесно повідомляє про неможливість її отримати» ще не забезпечений для всього CLI. Залишаються інші шляхи передчасного завершення історії, непозначений offline-результат, розбіжності CLI/MCP, неповна обробка відмов і небезпечні повтори записувальних операцій.

Найперше варто виправити: валідацію адресата **до** `bot write`; заборону визначати видалення з локального кешу; пагінацію topics/comments/search; коректну обробку incremental export. Потім — єдиний контракт результатів і строків очікування. Daemon, новий кеш чи повне переписування для цього не потрібні.

## Навігація і рівні доказів

- [Повний інвентар CLI, опцій і defaults](cli-inventory.md), [фактичні help](evidence/help.txt).
- [Читання, topics, migrations, статистика й links](reads.md).
- [Export, attachments, downloads, transcription і bots](exports-media.md).
- [Сесії, locks, MCP і installers](session-mcp.md).
- [Деталі installers/discovery](installers.md), [релізи й platform coverage](release.md).
- [Сирі результати перевірок](evidence/), [вихідний стан і SHA-256](evidence/baseline.json).

Позначення в цьому звіті: **R** — відтворено виконанням binary або поточного helper-коду на синтетичних даних; **C** — підтверджено конкретним шляхом у коді, без живого відтворення; **L** — відтворено буквальну логіку фрагмента окремо, не весь handler; **H** — гіпотеза, що потребує інтеграційного тесту; **P** — продуктова пропозиція. Наявність unit-тесту не означає перевірку Telegram end-to-end.

## 1. Фактичний стан і виконані перевірки

Checkout: HEAD `20f3bb4` (`Publish releases after all platform checks pass`). У проєкті й встановленому binary — `7.0.1`. Збережено початкові зміни в `ExportSupport.cs`, `McpTelegramBackend.cs`, `README.md`, `TelegramSession.cs`, `TgCli.Tests/ExportTests.cs`, `TgCli.csproj`, `TgCommands.cs` і нові `TelegramConnection.cs`, `ChatHistoryTests.cs`, `TelegramConnectionTests.cs`. Контрольні суми зафіксовано до аудиту; [остаточна перевірка](evidence/final-verification.json) підтвердила незмінність усіх 10 початкових файлів.

Встановлений binary: `/home/andrii/.local/bin/tgcli`, SHA-256 `3afa02463739a117fd1c7f50a0eef043c894f5190bbf846a1055834bad72f2d3`. Це ідентифікатор перевіреного файла, а не доказ відтворюваності збірки з поточного дерева. Публічний latest на момент перевірки — [v7.0.0](https://github.com/podkolzzzin/tgcli/releases/tag/v7.0.0), опублікований 03.11.2026; публічний 7.0.1 не підтверджений.

| Перевірка | Результат | Межі доказу |
|---|---|---|
| `dotnet test TgCli.Tests/TgCli.Tests.csproj --logger 'trx;LogFileName=audit.trx' --results-directory /tmp/tgcli-audit-2026-11-20/test-results --verbosity minimal` | 113 passed, 0 failed, 0 skipped; SDK 10.0.101 | Debug-збірка поточного checkout; не живі Telegram-сценарії |
| `python3 -m unittest discover -s tools -p 'test_*.py' -v` | 4 passed | Підготовка assets/checksum/archive/runtime mapping |
| `python3 tools/e2e_mcp.py /home/andrii/.local/bin/tgcli` | PASS, modern/legacy stdio, 10 конфігурацій | Тимчасові профілі, fake CLI clients, без робочої авторизації |
| `python3 tools/e2e_transcription.py /home/andrii/.local/bin/tgcli --rid linux-x64` | PASS WAV, OGG, MP3, MP4, auto/plain, missing/no-audio | Локальний публічний speech fixture, окремі extraction directories |
| Рекурсивний `--help`, `--version`, invalid-argument probes | 31 command surface; дефект невідомої команди exit 0 | Встановлений Linux-binary; звичайні Telegram-запити не запускалися |
| Синтетичні probes helpers/фрагментів | Див. R/L у дефектах та додатках | Не замінюють TDLib/server integration |
| Windows/macOS release binary, живі reconnect/offline/revoked auth/rate limit, реальні боти | **Не виконано** | Потрібні ізольовані ресурси та відповідні runners |

Розподіл 113 тестів: installers 24, protocol 14, export/helpers 24, transcription 18, connection 5, MCP tools 15, session auth descriptions 3, history 6, session gate 4. Число 113 не є оцінкою покриття: більшість handlers і їхніх error paths не мають наскрізних тестів.

Технічний опис попереднього інциденту прочитано в приватному mentorship-звіті. Його переписку, ідентифікатори людей/чатів і auth-дані сюди не переносили. Попередній живий успіх 7.0.1 — історичний доказ, **не нова live-перевірка цього аудиту**.

## 2. Матриця покриття всього функціоналу

Для всіх рядків нижче виконано читання коду і фактичного help. «Немає live» означає, що актуальність на сервері не була перевірена цим аудитом.

| Напрямок | Очікуваний контракт | Фактична реалізація / доказ | Прогалини |
|---|---|---|---|
| Login / session | Обмежене очікування, явний login-required, без циклу на EOF | Auth state machine, file lock, import/export secret; 3 auth-description + lock/secret helper tests; R EOF/lock/import | Немає live login, revoked auth, kill/restart, конкурентного import |
| Chat list/search/resolve | Актуальний список/збіги у межах limit | `GetChats`; search default `SearchChats`, optional server; shared CLI/MCP operations | Немає cold-cache server parity; локальний default search — C; list coverage — H |
| History / get | Наповнити запитаний діапазон, не плутати page/EOF | Online startup gate; all-history helper має 6 tests; одна TDLib page для звичайного read | Немає cold/warm live; single page може недонаповнювати limit; немає єдиного completeness envelope |
| Message search | Дотримуватися server cursor, dates і result limit | CLI та MCP мають різні paginator; MCP cursor tests | C CLI ігнорує continuation, short-page stop; неповний збіг `--all` з фільтрами |
| Context / reply chain | Повний доступний контекст або marker unavailable | Shared helper, depth cap 100, catch/break для replies | Немає tests boundary/deleted reply/cycle; errors можуть виглядати як короткий контекст |
| Links | Коректний тип chat/message ID і доступність | Shared helper, fallback URL; offline probe в додатку | Не перевірено відкриття links на Telegram clients; fallback семантика — H |
| Export / incremental / resume | Відтворюваний файл, актуальні edits, без хибних deletions | JSONL schema 5.0, `.tmp` rename, manifest, dedupe/tombstones; R/L probes | Немає handler crash recovery tests; edit/upsert, deletion repeat, token scope і projected rows — дефекти/прогалини |
| Topics / migrations | Повний topic range і migration chain | Paginator з tests, reflection migration lookup | R topic short-page false complete; R неправильний тип migration property |
| Stats / metrics | Scope/count_kind/completeness; актуальні counters | Stats shared, aggregate helpers tested | R false exact із short/local history; R Int32 overflow; live counter freshness — H |
| Comments | Усі доступні comments у межах запиту | Окремий thread paginator і summaries | C root cursor/short page; немає dedicated tests; error marker exit 0 |
| Attachments / download | Правильний primary file, metadata, safe destination | Metadata/index/batch parsing helpers tested | R rich attachment metadata губиться; немає real expired file-reference/download retry tests |
| Batch download | Обмежена concurrency, per-item failure і ненульовий exit | Parallel workers, deterministic destination; parser/parallelism tests | R однаковий destination для різних type одного message; kill/partial-output не перевірено |
| Transcription | Локальна bounded обробка; generated label, cleanup | Embedded Whisper/ffmpeg, 18 tests + Linux binary E2E | Немає native inference cancellation/long media/OOM matrix; Telegram-вбудований та external-engine режими не перевірено E2E |
| Bots list/create/delete/token/write | Валідація до side effects; однозначний outcome | BotFather conversational state, polling, output token/text | Немає live або dedicated bot integration tests; C human-send-before-check, late format validation |
| Diagnostics | Стан авторизації/мережі з provenance, чесний exit | 7.0.1 observed connection instead of constant true | Offline diagnostics чекає online gate, не дає повний structured status; cache-derived me не незалежний ping |
| MCP | Та сама семантика даних, стабільна schema/cursor/cancel | 12 tools; gate serialization; protocol/tools/gate tests; binary E2E | Немає live Telegram parity; cancellation underlying native call/deadline; output enrichment може обійти local policy |
| MCP installers | Safe merge, atomic write, persisted-value verification | 24 tests + Linux 10 fake client configs E2E | Реальні versions клієнтів/OS не перевірено; read-modify-write race — H |
| Packaging/release | Перевіряти саме артефакт на кожній підтримуваній ОС | CI linux-x64/osx-x64/win-x64; tests+published E2E; final release depends on matrix | Немає Telegram freshness gate; provenance і downloaded-release smoke недостатні; ARM не заявляти перевіреним |

## 3. Пріоритизовані проблеми

P0/P1/P2 тут — рекомендований порядок реалізації (P0 передусім), а не шкала кількості постраждалих користувачів. Тематичні додатки зберігають локальні оцінки оглядачів; ця таблиця задає підсумкову чергу.

Нижче — конкретні відтворення на fake transport/тимчасових файлах або умови для майбутнього integration fixture. Команди з `<test-chat>` / `<test-user>` **не запускати на реальних контактах**. Детальні code references і додаткові дефекти наведені в тематичних додатках; номери рядків стосуються зафіксованого checkout.

| ID / пріоритет / доказ | Відтворення та actual | Expected / причина / код |
|---|---|---|
| F01 P0 C | `bot write --username <test-human> --text probe`: private chat проходить перевірку; `SendTextAsync` викликається раніше за перевірку UserTypeBot; потім помилка | Не виконувати жодного send/callback для людини. Валідація стоїть після side effect: [BotCommands.cs](../../../BotCommands.cs#L155), 191–214 |
| F02 P0 R+C | Empty `onlyLocal` page ⇒ `Complete=true`; incremental з попереднім файлом може обчислити всі cached keys як deleted | Offline cache не може бути доказом серверного видалення. `local` не впливає на EOF completeness; deletedKeys потребує authoritative scope: [ExportSupport.cs](../../../ExportSupport.cs#L76), [TgCommands.cs](../../../TgCommands.cs#L1230) |
| F03 P0 R+C | Topic pages `[one message], [older], []`: після першої `Complete=true`, дві сторінки не прочитані; stats може оголосити exact | Продовжити до доказового EOF/range end, коротка сторінка не EOF: [ForumTopics.cs](../../../ForumTopics.cs#L112). Existing test теж кодує хибну передумову |
| F04 P0 C | Thread root ID 100, comments 101–120: cursor починається з min starter messages, отже history запитується від root назад; short page завершує цикл | Почати comments з newest cursor 0, правильно включити root, продовжувати pages: [TgCommands.cs](../../../TgCommands.cs#L782), 801 |
| F05 P0 C | Search page коротка, але `NextFromMessageId` ненульовий: CLI `--all` завершується; MCP продовжує інакше | Дотримуватися continuation, не виводити неповне як повне: [TgCommands.cs](../../../TgCommands.cs#L1386), [McpMessagePage.cs](../../../McpMessagePage.cs#L7) |
| F06 P1 L+C | Cached message A має старий text, server A новий; `RemoveAll(cachedKeys.Contains)` відкидає нову версію. Повтор deleted B додає tombstone щоразу; manifest рахує keys, а не фактичні рядки | Визначити snapshot/upsert semantics; terminal deletion не додавати повторно; точні counters. [TgCommands.cs](../../../TgCommands.cs#L1218), 1230–1253. Edits — підтверджена невідповідність цільовому контракту, не заявленому README upsert API |
| F07 P1 L+C | Resume приймає `wrong-chat:900`; `--fields text` видаляє ключі, а читач cache отримує `(0,0)` | Перевіряти chat/filter/schema fingerprint до I/O, вимагати ID-поля для resumable output: [TgCommands.cs](../../../TgCommands.cs#L1179), 1220; `Output.SelectFields` |
| F08 P1 R+C | Legacy basic-group з upgrade: reflection `BasicGroupFullInfo.UpgradedToSupergroupId` ⇒ null; ланцюг не переходить у successor | Читати властивість `BasicGroup`, перевіряти обидва напрямки chain: [ExportSupport.cs](../../../ExportSupport.cs#L205) |
| F09 P1 R+C | Під час pending history-page скасувати token: очікування не завершується; повернути empty page — helper може повернути success | Загальний deadline і cancellation до/після запиту; не втрачати gate до завершення native work. [ExportSupport.cs](../../../ExportSupport.cs#L64), [TgCommands.cs](../../../TgCommands.cs#L993), [TelegramSession.cs](../../../TelegramSession.cs#L76) |
| F10 P1 R | `tgcli no-such-command` ⇒ exit 0 + root help на stdout; bare required-arg leaf теж help success | Unknown command ⇒ usage error, stderr, ненульовий code. Bare leaf help-on-empty — окреме UX-рішення, не доведений дефект. [Program.cs](../../../Program.cs#L14), [probes](evidence/cli-probes.jsonl) |
| F11 P1 C | `search` без `--server` використовує локальний search навіть після connection-ready | Online default має серверну семантику в заявленому scope; cache mode явний. [TelegramOperations.cs](../../../TelegramOperations.cs#L12). Реальний cache-miss у цьому аудиті не відтворювався |
| F12 P1 R+C | Створити directory `tdlib.lock.owner`, викликати acquire: metadata write падає, відкритий lock stream не dispose. Prompt із EOF читає null у циклі | Вивільняти lock у failure path, EOF ⇒ auth-required/error, передавати cancellation: [TelegramSession.cs](../../../TelegramSession.cs), точні рядки в session-mcp додатку |
| F13 P1 R+C | Синтетичний secret із валідним manifest, невалідним config/binlog; import `--force` приймає й видаляє попередній sentinel. Lock перевіряється без утримання на час replacement | Stage/validate/backup/atomic swap під утриманим lock; malformed input не нищить попередню сесію: [SessionSecret.cs](../../../SessionSecret.cs). Race доведений структурою, не конкурентним live-run |
| F14 P1 R+C | Full rich attachment path через `GetAllFiles` губить filename/MIME/duration/dimensions; thumbnail video має kind video. Різні type одного message у batch можуть мати той самий destination | Один canonical attachment extractor, primary/thumbnail role, collision-free paths. [MessageFiles.cs](../../../MessageFiles.cs), [BatchDownloads.cs](../../../BatchDownloads.cs), деталі exports-media |
| F15 P1 C | Invalid `--format` у bot mutation перевіряється після дії; send/delete/revoke може відбутись, хоча команда поверне error | Усі параметри перевірити до session/mutation; outcome відділити від rendering. Не повторювати send автоматично після ambiguous timeout: [BotCommands.cs](../../../BotCommands.cs) |
| F16 P2 R+C | `service-only` приймає звичайні photo/poll/video-note; сума views 3 млрд ⇒ OverflowException | Явні TDLib service types; Int64 counters: [TgCommands.cs](../../../TgCommands.cs), helper probe в reads |
| F17 P1 C+P | Plain/TSV newest-first, JSON/JSONL oldest-first; simple date без offset у local timezone, rich ISO UTC; numeric CLI IDs vs string MCP IDs | Розбіжності підтверджено; уніфікація — продуктова зміна з міграцією, не доказ порушення старої обіцянки: [Output.cs](../../../Output.cs#L115), 213–260, 343; [McpSchemas.cs](../../../McpSchemas.cs) |
| F18 P1 C | Export max-pages за замовчуванням exit 0 з manifest incomplete у stderr; stats має metadata, messages --all кидає error; `--expect-since` пропускає empty manifest (R) | Узгодити complete-within-scope/partial/page-limit, вихідний code не залежить від знання `--fail-incomplete`: [TgCommands.cs](../../../TgCommands.cs#L1194), [ExportSupport.cs](../../../ExportSupport.cs#L336) |
| F19 P1 C | `chat list`, `chat resolve`, top-level `search` приймають `--format json/plain/text`, але `PrintChats` не має цих cases — навіть непорожній result дає порожній stdout | Реалізувати формати або відхилити до I/O, ніколи не маскувати дані порожнім успіхом: [Output.cs](../../../Output.cs#L195) |
| F20 P1 C | `chat messages --all --from-message-id Y` починає cursor із 0; offset теж ігнорується; `chat search --queries ... --format json` друкує кілька незалежних JSON arrays | Не змінювати scope мовчки; один JSON document на команду. [TgCommands.cs](../../../TgCommands.cs#L993), 1105–1129 |
| F21 P1 C | Metrics/comments-all викидають `HistoryFetchResult.Complete`; forum topics short-page/max-pages повертає звичайні rows | Зберігати completeness у всіх aggregates/pages: [TgCommands.cs](../../../TgCommands.cs#L617), 685; [ForumTopics.cs](../../../ForumTopics.cs#L277) |
| F22 P1 R | Eclipse whitespace-delimited `mcp {...}` не розпізнано: merge додає нове `mcp=...`, яке замінює логічне попереднє значення | Коректно парсити Java properties або відмовлятися без змін; не губити unrelated servers. [McpConfiguration.cs](../../../McpConfiguration.cs#L200), [відтворення](installers.md) |
| F23 P2 R | Пошкоджений Eclipse registry перериває discovery всього Copilot; Unix PATH бере non-executable файл раніше робочого launcher | Ізолювати adapter failure; перевіряти executable permission. [McpClientDiscovery.cs](../../../McpClientDiscovery.cs#L86), 28 |
| F24 P2 R | Malformed initialize повертає error, але транспорт уже перейшов у legacy mode; наступний metadata-free tools/list проходить | Commit protocol mode тільки після успішної negotiation, це protocol correctness, не auth bypass. [McpCompatibilityTransport.cs](../../../McpCompatibilityTransport.cs#L31) |
| F25 P2 C | Unix install recipe пише curl response прямо поверх робочого binary, без HTTP fail/checksum/staging; MCP publish recipe пропускає assets preparation | Зберегти попередній binary до verified atomic install; позначити 7.0.1 unreleased і узгодити recipes. [README.md](../../../README.md#L32), [MCP.md](../../../MCP.md#L6), [release review](release.md) |

Короткі сторінки дозволені контрактами [TDLib getChatHistory](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1get_chat_history.html) і [getMessageThreadHistory](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1get_message_thread_history.html). `ConnectionStateReady` означає [робоче з'єднання](https://core.telegram.org/tdlib/docs/classtd_1_1td__api_1_1connection_state_ready.html), але сам по собі не доводить повноту кожної вибірки чи актуальність усіх похідних counters.

### Гіпотези й обмеження, які не слід називати доведеними live-дефектами

- H01: `GetChats` на холодній chat list може не повернути очікувану кількість без явного завантаження; потрібен контрольований account fixture.
- H02: reconnect між startup-ready і завершенням запиту, видалені/відредаговані повідомлення у TDLib cache, актуальність metrics — не перевірені живою мережею.
- H03: `--local` enrichment через replies/links/participant full-info може звертатися до мережі або зависати. Шляхи викликів наявні; фактична поведінка залежить від TDLib cache.
- H04: crash між rename data і rename manifest, disk full, два процеси на один output, native inference cancellation, flood-wait/backoff, PID reuse — потребують fault injection. Наявність `.tmp` не доводить атомарність пари data+manifest.
- H05: формування fallback group links, контекст із великим after та доступність віддалених replies потребують integration перевірок.
- H06: installers мають перевірку optimistic concurrency, але window між перевіркою та rename лишається; реальна втрата concurrent edit не відтворювалася.

## 4. Запропонований цільовий контракт (P)

1. **Online — default.** Команда чекає готовності сесії і виконує операцію з єдиним загальним deadline. Успіх означає перевірений результат саме в scope запиту, не твердження про весь Telegram. Внутрішній кеш не потребує ручного прогрівання/очищення.
2. **Scope явний.** `--limit N` — до N відповідних результатів, не розмір одного TDLib RPC. Читати короткі/відфільтровані сторінки до N або доказового кінця. `--all` — весь визначений діапазон до safety budget; вичерпання budget — partial, а не success як для всього діапазону. Limit-reached з continuation — нормальна виконана сторінка, не збій.
3. **Різні стани.** `ok`/`empty`/`partial`/`cached`/`error`; окремо `has_more`, `next_cursor`, `termination_reason`, `requested_scope`, `verified_at`. Empty online — успішний порожній результат тільки після перевірки scope. Cached ніколи не називати verified/exact server state.
4. **Exit codes — пропозиція для versioned переходу:** 0 для виконаного online-запиту (включно з empty/limit page); 1 operational error; 2 usage; 3 partial requested range; 4 login-required; 5 session-busy; 6 explicitly cached/unverified; 130 cancellation. Machine-readable `error.code` стабільніший за текст. Не створювати десятки спеціальних flags для обходу цих правил.
5. **stdout — дані, stderr — пояснення.** JSON — один versioned envelope; JSONL — versioned records із явним terminal result record, щоб навіть empty/cached можна було розпізнати. Для сумісності старий формат залишається explicit legacy protocol до major release. Pipe failure/cancellation не видавати за порожній успіх.
6. **Одна семантика CLI/MCP.** Decimal-string IDs, ISO-8601 UTC timestamps, documented exclusive cursors і half-open date range `[since, until)`, однаковий ordering незалежно від renderer. Для history — newest-first як запропонований default; archival snapshot може мати явний oldest-first. Межі дат без timezone — або явний documented UTC, або usage error; не залежати мовчки від TZ хоста.
7. **Side effects — досяжний outcome.** Валідація до I/O; write не повторювати автоматично після невідомого результату. Повідомляти confirmed/failed/outcome_unknown і receipt за наявності. Read retry — лише класифіковані transient errors з обмеженим budget і server flood-wait.
8. **Recovery — автоматичний і локальний.** Тимчасові файли мають ownership/generation; не видаляти робочу сесію для ремонту. Import stage/validate під lock; export commit зв'язує data+manifest через checksum/generation. Повторна команда не потребує cache surgery.

## 5. Дев'ять найцінніших спрощень (P)

| Ідея | До → після | Вигода | Компроміс і сумісність |
|---|---|---|---|
| S1 Спільний read service/paginator | CLI search має власний loop, MCP інший → один PageResult для обох | Один regression fix для short page/cursor/filter | Середня зміна; спершу internal refactor під characterization tests, без зміни schema |
| S2 Online default без «лікувальних» flags | Потрібно знати `--server`, `--fail-incomplete`, `--expect-count-min` → звичайна команда сама перевіряє scope, `--cached` лише explicit offline | Прогнозовані базові команди | Збільшення latency; `--local` лишити deprecated alias, старі scripts отримають nonzero там, де раніше був partial success |
| S3 Єдиний budget | `--lock-timeout`, `--request-timeout` лише в частині commands, необмежені RPC → спільний `--timeout` для всієї команди | Зрозуміла верхня межа, узгоджене Ctrl-C | Залишити advanced budgets internal; довгі exports можуть явно збільшити deadline |
| S4 Один machine result contract | JSON simple/rich/MCP різняться IDs/time/status → спільні domain DTO + versioned adapters | Менше парсерів, точний partial/empty | Wire-format зміни тільки opt-in/vNext; підтримати legacy протягом оголошеного періоду |
| S5 Один resumable export workflow | `--all` + `--all-history`, resume/incremental/token/fields комбінації → snapshot export та один validate-and-update режим | Менше прихованого стану, визначені edits/deletes | Потрібне рішення snapshot vs journal; рекомендація snapshot для простоти, journal тільки як окремий explicit format |
| S6 Один attachment model | Primary rich path і raw all-files path гублять різні metadata → canonical attachment із role/identity | Однакові download/transcription/export, без thumbnail ambiguity | Адитивні metadata; destination naming migration для batch |
| S7 Валідація й rendering окремо від операцій | Invalid format може впасти після send → parse/validate → execute → render | Безпечні помилки, менше повторних sends | Низький ризик; receipt не гарантує exactly-once Telegram execution |
| S8 Diagnostics як inspect, без умовного успіху | Чекає online для пояснення offline → structured auth/lock/connection/cache state в межах deadline | Корисна діагностика після збою без знання TDLib | Жодних secrets/повних paths за default; active probe за потреби окремо позначений |
| S9 Менший default distribution, опціональні capability bundles | Telegram reads несуть embedded model/decoder у великому binary → зберегти full build, оцінити core build + explicit offline transcription bundle | Менший download/update/startup footprint, transcription лишається offline | Це P2 гіпотеза вартості, спершу виміряти; не додавати runtime download без згоди, не ламати нинішній standalone режим |

Альтернативи: (A) лише латати handlers — найдешевше зараз, але дублювання лишає регресійний ризик; (B) поступово звести спільні reads/results та прибрати зайві режими — рекомендовано; (C) daemon/новий cache/store — додатковий lifecycle, state і deploy surface без доведеної потреби. Вибір B — рекомендація аудиту, не рішення Андрія.

## 6. План P0/P1/P2 для перегляду

Оцінки — інженерні робочі дні з тестами одного виконавця, не обіцянка строку. S: до 2 днів; M: 3–5; L: 1–2 тижні. Роботи можуть перетинатися; оцінки не слід механічно підсумовувати.

| Черга | Конкретна зміна | Розмір | Ризик сумісності | Умова приймання |
|---|---|---|---|---|
| P0.1 | Зберегти/оформити існуючі 7.0.1 fixes як окремий перевірюваний patch; не оголошувати весь продукт fresh | S | Низький, online wait додає latency | A01–A03; включені untracked helper/tests; no secrets у diff |
| P0.2 | Preflight bot target/options; errors до будь-якого send/click/revoke/delete | S | Низький; неправильні inputs тепер гарантовано нічого не роблять | A13 |
| P0.3 | Заборонити authoritative deletion з local/partial/wrong scope; зупинити повторні tombstones | M | Середній для consumers tombstone log | A08–A09 |
| P0.4 | Виправити topics/comments/search cursor/short-page termination; migration lookup | M | Середній: більше правильних результатів і запитів | A04–A07 |
| P1.1 | Загальний execution context: deadline, cancellation, state gate, classified retries; cleanup locks/EOF | L | Середній: explicit timeout/nonzero замість hang | A03, A10–A12 |
| P1.2 | Export snapshot/upsert, scoped checkpoint, обов'язкові IDs, generation/checksum data+manifest | L | Високий для старих resumable файлів; versioned migration | A08–A10 |
| P1.3 | Shared read PageResult для CLI/MCP, online search default, consistent limit/filter/date semantics | L | Середній–високий; dual adapter/legacy tests | A04–A07, A14–A15 |
| P1.4 | CLI usage/nonzero, structured diagnostics, IDs/time/order schemas, partial contract | M–L | Високий для scripts; opt-in vNext до major | A14–A16 |
| P1.5 | Session import stage/validate/backup під lock; safe output replacement/download identities | M | Середній для force import і batch names | A10–A12, A17 |
| P1.6 | Canonical attachments, service type filter, Int64 metric counters; перевірити counter freshness | M | Низький для bugfix; metadata адитивна | A07, A17–A18 |
| P1.7 | Виправити Java-properties conflict preservation; відокремити adapter parse errors; negotiation тільки після success | S–M | Низький для коректних конфігурацій; unsupported syntax тепер може явно відхилятися | A20 |
| P2.1 | Deprecate дублікати `--all-history`, user-facing cache-repair flags; залишити aliases з терміном вилучення | S–M | Високий лише при видаленні; major release | Help/compat snapshots, migration guide |
| P2.2 | Рознести transport/domain/rendering, скоротити монолітні handlers без переписування TDLib/session | M–L | Низький при незмінному adapter contract | CLI/MCP parity suite |
| P2.3 | Виміряти доцільність core/full bundles; спростити лише за підтвердженої вигоди | M на spike | Packaging/installation; opt-in variant | Startup/size/platform measurements + offline transcription gate |
| P2.4 | Усунути install/source-build recipe drift, додати safe staged download і version/tag/release-note validation | S | Низький; старі релізні інструкції лишити історичними | A19; pristine checkout publish recipe |

До implementation потрібно погодити лише суттєві продуктові рішення: versioned JSON/JSONL і exit-code перехід, snapshot чи journal для incremental, строк deprecated aliases. Не потрібно чекати нового daemon-дизайну, щоб виправити P0 ordering/pagination/local deletion.

## 7. Acceptance і регресійна матриця

Усі server fixtures — окремий контрольований account/chat/bot. Fake TDLib transport має вміти задавати pages, connection/auth transitions, delay, transient/permanent errors і side-effect receipts. Реальні контакти не використовувати. Позначка «потрібно» означає, що сценарій **ще не пройдено** цим аудитом.

| ID | Сценарій | Обов'язковий observable результат | Стан зараз |
|---|---|---|---|
| A01 | Зберегти auth+cache до T0; іншим контрольованим клієнтом додати marker T1; запустити базове читання один раз, без warm-up | Marker T1 у межах limit/date scope або явний failure; ніколи успішний stale prefix; повторити з empty/warm/stale cache, CLI і MCP | Connection unit є; новий live fixture потрібен |
| A02 | Auth Ready раніше Connection Updating→Ready, short pages `[1,2,1,0]`, duplicate boundary | Чекати Ready; усі унікальні records, EOF тільки за доказом; status і counters точні | 7.0.1 connection/history unit pass; всі command adapters ще потрібно |
| A03 | Offline startup, slow network; disconnect після першої page; reconnect перед наступною MCP call | Загальний deadline; partial/error з nonzero; повторний run без cache cleaning працює; cached only при explicit mode | Helper reconnect unit pass; request fault injection потрібно |
| A04 | Topic/history/comments: short, empty, repeated boundary, nonadvancing cursor, max-pages | Немає premature EOF/loop; incomplete позначений; comments newer than root включені | Topic failure reproduced; решта matrix потрібно |
| A05 | Search short raw page з nonzero continuation; усі результати першої page відфільтровані; limits 1/30/100 | Продовжити до matching limit/EOF; CLI/MCP ті самі IDs/order/cursor | MCP helper test є; CLI failure за кодом |
| A06 | Міграція basic→super; старт із кожного ID, IDs що повторюються між chats; date boundaries | Повний chain; composite dedupe `(chat,id)`; cursor не переноситься між chats без scope | Неправильний property reproduced; інтеграція потрібна |
| A07 | Exact stats/metrics/comments на fixture з відомими counts, edits, deletes, 3 млрд views | Exact лише для verified complete scope; no overflow; partial має нижню межу/count_kind | Overflow/topic/local problems reproduced; server freshness потрібно |
| A08 | Export→edit existing message→incremental→repeat; delete→repeat | Edited text оновлено; одна terminal deletion; повтор без змін ідентичний по змісту, counters відповідають records | Logic replay failure; handler fixture потрібно |
| A09 | Empty/partial offline cache + populated previous export; wrong-chat token; projected fields без IDs | Жодних invented deletions; invalid checkpoint/projected resume відхиляється до replacement | R/L failure; integration потрібно |
| A10 | Kill після partial output, disk full, corrupt JSONL tail, crash між data/manifest rename; restart | Старий committed export придатний, або явна recoverable generation; temp cleanup належить своєму run; ніякого mixed success | Потрібно; code має лише per-file rename |
| A11 | Два CLI + MCP на одній сесії; active/stale locks; metadata write failure; Ctrl-C у queue/RPC | Один owner, bounded busy; active lock не видалено, failed acquire не залишає handle; canceled native work не запускається паралельно | Lock/gate unit частково pass, leak reproduced; cross-process потрібно |
| A12 | Revoked auth, stdin EOF, malformed secret import, force import при concurrent owner | Noninteractive auth-required, без prompt loop; старі дані збережено; немає session replacement без exclusive lock | EOF/import reproduced; revoked live потрібно |
| A13 | Fake target human, invalid format/click; bot send timeout до/після acceptance | Нуль writes для invalid target/options; ambiguous outcome явний, automatic resend відсутній | Code ordering confirmed; mock mutation suite потрібно |
| A14 | Empty vs page-limit vs partial vs cached vs error, CLI всі formats і MCP | Stable status/exit/schema, stdout парситься, stderr окремо; cancel 130; unknown command ненульовий | Unknown command reproduced; контракт ще запропонований |
| A15 | TZ UTC/Kyiv, near DST boundary, date-only/explicit offset, big IDs, JSON vs TSV vs plain | Однаковий набір/order IDs, UTC machine timestamps, documented boundaries, no precision loss | MCP string-ID tests pass; cross-format suite потрібно |
| A16 | `--help` усіх leaves/groups; invalid limit/format/schema до session | Scope-specific help, опис кожного flag, usage error до I/O | 40 help/version invocations pass; UX gaps у inventory |
| A17 | Primary+thumbnail, expired file reference, batch same-message different types, network break/kill copy | Canonical metadata, unique destination, explicit per-file failure; неповний final file не виглядає готовим | Helper failures reproduced; real download recovery потрібно |
| A18 | Local wav/ogg/mp3/mp4, long media, no audio, corrupted model cache, cancel decode/inference; external engine багато stderr | Generated label, cleaned temp, bounded process/resources, stdout clean | Linux normal/error E2E pass; stress/native cancellation потрібно |
| A19 | Скачати кандидата release на Linux/macOS/Windows, clean HOME і без залежності від repo assets | SHA/tag/version збігаються; help/protocol/transcription/installer fixture; native TDLib read fixture свіжий на першому запуску | Installed Linux pass; downloaded multi-OS + live потрібно |
| A20 | Eclipse whitespace property/conflicting tgcli/unrelated servers, malformed registry з валідним іншим adapter; failed MCP initialize→retry | Немає override/loss, незалежні targets лишаються доступні; invalid negotiation не змінює mode | Синтетичні failures reproduced; regression suite потрібно |

### Мінімальний release gate

1. Review фактичного diff, включно з новими helper/tests; secret scan без публікації приватних fixtures. Version/tag/changelog узгоджені. Не релізити лише тому, що 113 старих тестів зелені.
2. Unit + fault-injection P0/P1 contracts: A01–A17 на fake transport, з окремим regression test для кожного підтвердженого дефекту. Переписати тест, що вважає коротку topic page кінцем, до виправлення коду.
3. OS matrix для `linux-x64`, `osx-x64`, `win-x64`: тести саме packaged binary у clean temporary profile, version/help/invalid args, MCP modern+legacy, installers fixtures, embedded transcription. ARM підтримку не вважати підтвердженою цією матрицею.
4. Один controlled Telegram freshness suite: marker після baseline cache, cold/warm/stale і reconnect, перший запуск, CLI/MCP; перевірити edit/delete і scope. Записати pass/fail і технічні counters, не реальні повідомлення/credentials. Живі credentials зберігати лише в приватному test environment.
5. Артефакт provenance: SHA-256 кожного binary, source SHA, version, RID, assets hashes; після upload/download повторити smoke і перевірити checksum. Publish тільки після всіх обов'язкових platform jobs; нинішній final `needs: publish` зберегти.
6. Release notes мають розділяти виправлений інцидент і невирішені обмеження; schema/exit-code migration guide перед major змінами. До виконання live gate не називати випуск доказово fresh для всіх commands.

## 8. Межі завершення аудиту

Аудит охопив усі наявні command surfaces, код напрямків і наявні тести; невиконані живі/platform/fault сценарії позначені явно. Результат — звіт, відтворювані локальні докази та план для перегляду. Жодних повідомлень реальним контактам, видалення ботів, скидання auth, зміни робочих MCP profiles чи публічного релізу не виконано. Прийняття задачі залишається за Андрієм.
