# tgcli: послідовність задач і паралельність

> **For agentic workers:** Після погодження відповідної гілки використовуйте subagent-driven-development або executing-plans. Це пропозиція декомпозиції задач, не дозвіл починати реалізацію; точні interfaces і покрокові implementation plans готуються для окремих гілок.

**Goal:** Закрити всі підтверджені дефекти аудиту, перевірити всі гіпотези й отримати простий передбачуваний CLI.

**Architecture:** Зберегти TDLib і наявний session gate. Поступово об'єднати політику виконання, пагінацію і результати CLI/MCP; негайні виправлення не блокувати великим рефакторингом. Кожна задача включає власні regression tests.

**Tech Stack:** C#/.NET 10, TDLib, ConsoleAppFramework, MCP, xUnit, Python E2E, embedded Whisper/ffmpeg.

**Spec:** [Аудит 20.11.2026](../../audits/2026-11-20/README.md), включно з тематичними додатками, F01–F25, H01–H06, A01–A20.

## Обмеження

- Зберегти локальні 7.0.1 fixes і всі сторонні зміни; baseline перевірити повторно перед implementation.
- Жодних приватних повідомлень чи auth-даних у Git. Live side effects — лише контрольовані тестові ресурси.
- Пропозиції schema/exit-code/default changes потребують окремого рішення щодо сумісності; вони не є вже погодженим API.
- Гіпотеза закривається відтворенням і fix або тестом/доказом, що її спростовує. Не переписувати компонент на підставі лише підозри.
- Додавання задач до JungleManager, реалізація і публічний реліз у межах цієї пропозиції не виконуються.

## Дерево робіт

Сусідні гілки можуть рухатися паралельно. Стрілка всередині гілки означає послідовність. Додаткові залежності між гілками наведені нижче.

```text
00. Зберегти baseline 7.0.1 і перенести докази у regression backlog
│
├── 01. Визначити контракт і перехід сумісності
├── 02. Створити спільний fake TDLib transport і fault-injection fixtures
│
├── A. Боти
│   └── A1. Валідація до send/click/revoke/delete [P0]
│       └── A2. Надійний BotFather workflow, token parser і outcome_unknown
│
├── B. Сесія й час виконання
│   └── B1. Lock lease, безпечний import/export сесії, EOF [P0/P1]
│       └── B2. Загальний deadline, cancellation, reconnect/auth recovery
│
├── C. Читання
│   └── C1. Пагінація, cursors, topics/comments, migrations [P0]
│       └── C2. Context/search/list/links і точні stats/metrics
│
├── D. Export
│   └── D1. Заборонити хибні deletions і невалідний resume [P0]
│       └── D2. Edits/upsert, повторюваний incremental, atomic recovery
│
├── E. Медіа
│   └── E1. Єдина attachment identity і повна metadata
│       └── E2. Безпечні download/batch і відновлення після збою
│           └── E3. Cancellation, ресурси й інтеграція transcription
│
├── F. CLI
│   └── F1. Usage errors, непорожні підтримувані formats, preflight
│       └── F2. Спільні results, IDs/time/order, defaults/help/diagnostics
│
├── G. MCP
│   └── G1. Коректна negotiation і bounded errors
│       └── G2. Паритет із CLI, local policy і lifecycle
│
├── H. Installers
│   └── H1. Properties parser, ізоляція adapters, Unix executable discovery
│
└── I. Збірка й реліз
    └── I1. Safe install, recipes, version/checksums і CI gates

Завершення всіх обов'язкових гілок
└── J1. Спільна acceptance-перевірка: Telegram fixtures + три ОС
    └── J2. Прибрати дублікати й завершити погоджену міграцію CLI
        └── J3. Повторити release gate на фінальному кандидатові

Окремо, не блокує стабільний реліз:
└── K1. Виміряти доцільність core/full distribution; рішення за даними
```

## Результат кожної задачі

S — орієнтовно до 2 інженерних днів, M — 3–5, L — 1–2 тижні. Це оцінки обсягу з тестами, а не календарні обіцянки; уточнюються під час детального планування.

| ID | Результат і межі відповідальності | Доказ завершення | Обсяг |
|---|---|---|---|
| 00 | Зафіксований reviewable baseline, включені нові connection/history files; кожна знахідка має власника | Контрольні суми/чисте розділення чужих змін, baseline tests; нічого не втрачено | S |
| 01 | Узгоджені scope/empty/page/partial/cached/error, timeout, IDs/time/order, snapshot vs journal, legacy transition | Приклади input/output/exit для A14–A16; жодних прихованих рішень про breaking changes | S–M |
| 02 | Мінімальна підміна TDLib операцій: pages/auth/connection/errors/delays/recorded writes; CLI subprocess harness | Один read, одна mutation preflight і один timeout відтворюються без акаунта; fixtures reusable | M |
| A1 | `BotCommands`: bot identity, format, selectors/options до будь-яких writes | F01/F15, A13: invalid input/human target ⇒ нуль mutation calls | S |
| A2 | Token regex; сценарії create/remove/token/write, edited/delayed replies, temporary IDs, однозначний outcome | E11; scripted BotFather replies; timeout після acceptance не спричиняє автоматичний resend; `revoked` не копіюється сліпо з input | M |
| B1 | `TelegramSession`/`SessionSecret`: lease на весь import/export, cleanup failed acquire, EOF, stage/validate/rollback, caps archive/config, piped secret input і PID-reuse validation | F12/F13, A11–A12: malformed/oversized/duplicate-entry input зберігає стару сесію; concurrent owner не обходиться | M |
| B2 | Operation lifetime від lock/startup до enrichment/dispose: deadline, cancellation, classified retries/flood-wait, terminal auth recovery | F09, A03/A11/A12; reordered auth updates, stuck initialization/drain, revoked auth і reconnect; lock не звільняється під живим native work | L |
| C1 | `ExportSupport`/`ForumTopics`/read handlers: правильні page adapters, short≠EOF, continuation, bounded completeness, migration typed field, start/offset | F03–F05/F08/F20/F21; A02/A04–A06. Кожен API зберігає власну семантику курсора; CLI/MCP використовують спільний result | M–L |
| C2 | Online chat search scope і cold list; context bounds/target/reply cycles; links; service types; exact/estimated counters, Int64 sums | F11/F16/F21; reads R7/R9–R11; A01/A07/A15. Public/TDLib post-ID conversion і page filling після фільтрів перевірені. Метадані unavailable не стають unqualified zero; entities/link domains/paid reactions перевірені | M–L |
| D1 | Заборонити deletions для cached/partial/wrong scope; перевіряти token/chat/schema/IDs і temporal assertions до replacement | F02/F07/F18, exports E10; A09. Навіть до повного D2 повтор tombstones припинений або небезпечний режим явно відхилений | S–M |
| D2 | Incremental edits/upsert та reducer tombstones, точні counts, scoped checkpoint; data+manifest generation, output lock, unique temp/recovery | F06/F07; A08–A10. Repeat no-op стабільний; crash/disk-full не залишає непомітно змішану generation; обмежене buffering перевірено | L |
| E1 | `MessageFiles`/`AttachmentIndex`/schema: metadata primary/thumbnail, точна requested identity, edited/stale references, bounded index | F14, exports E5/E9; A17: запит thumbnail не повертає video; advisory index failure не підміняє результат | M |
| E2 | Single/batch download: normalized aliases, unique destinations, complete native file, staged copy, per-item receipts і recovery | F14, exports E8; A10/A17: kill/copy failure зберігає попередній final; completed batch files мають відновлюваний outcome | M |
| E3 | Telegram/local/export transcription: decode/inference cancel, long media/resources, extraction recovery, explicit external executable/args | A18: wav/ogg/mp3/mp4 + no-audio/corruption/cancel, captions збережено, generated label; external stderr drained; non-English fixture, message/export integration | M |
| F1 | `Program`/`Output`/preflight: unknown command nonzero, підтримуваний format не дає silent empty, один JSON для multi-query, validation до session | F10/F19/F20, A14/A16; bare-help UX окремо від invalid command; bounds/format errors не відкривають TDLib | S–M |
| F2 | Versioned renderer/results, decimal IDs, timezone/date boundaries, consistent order, online/limit defaults; truthful diagnostics і повний help | F17/F18; A14–A16; Broken pipe, TSV escaping, discoverability MCP install та узгоджені lock flags покриті; simple/rich/MCP differences або усунено з migration, або явно задокументовано як legacy | L |
| G1 | `McpCompatibilityTransport`/`McpTools`: negotiation commit після success, response/error limits | F24, session M1; A20 + huge invalid argument; failed handshake не poison connection | S |
| G2 | `McpTelegramBackend`/gate: використовує ті самі read/result/lifetime policies, local enrichment без remote fallback, auth recovery | A01–A07/A11/A14; parity datasets; queued cancellation не виконує operation; capability boundary CLI-only лишається явною | M |
| H1 | `McpConfiguration`/discovery/install: Java properties grammar/conflicts, незалежні failures, executable lookup, persisted verification | F22/F23, A20; unrelated servers збережено, malformed Eclipse не блокує інші clients; concurrent edits досліджені; CR/continuation/reference-parser corpus, Windows quoting/process-tree cancellation | M |
| I1 | `.github/workflows`/tools/docs: safe staged download, pristine build recipe, notes/tag/version, checksum/provenance, packaged-binary gates | F25, release R1–R5, A19; HTTP failure не замінює binary; кожна ОС перевіряє артефакт | M |
| J1 | Інтегрований кандидат проходить A01–A20; контрольований Telegram account/resources, cold/warm/stale, reconnect, edits/deletes; real client/OS compatibility | Немає необлікованих failures/skips чи H без висновку; Linux/macOS/Windows і actual installer client versions задокументовані | M–L |
| J2 | Після перевірки інтеграції: прибрати dead pagination/helper branches, дублювання config/renderers, unused installer paths; deprecation aliases | Одна відповідальність на operation; contract tests без регресій; aliases не вилучені раніше погодженого терміну | M |
| J3 | Повторний повний gate після J2 на фінальному binary; release notes/migration guide й артефакти готові до публікації | Downloaded-candidate checksums, A01–A20 і provenance; публікація окремою явно дорученою дією | S–M |
| K1 | Виміряти size/startup/memory core/full; оцінити packaging change | Рішення «залишити як є» теж валідний результат; standalone/offline не втрачені без окремого погодження | S–M |

## Додаткові залежності між гілками

Усі задачі потребують 00. Усередині гілки наступна задача також потребує попередньої.

| Задача | Додатково чекає |
|---|---|
| A2 | 02, B2 для остаточної timeout/outcome інтеграції |
| B2 | 01, 02 |
| C1 | 01 для спільного result contract; негайні bugfix/tests можуть починатися раніше |
| C2 | 02, B2 для cold/reconnect/local freshness інтеграції |
| D2 | 01, 02, C1, B2; E1 для остаточної attachment-schema інтеграції |
| E2 | 02, B2 |
| E3 | 01 для resource/deadline policy; B2 вже отримано через E2 |
| F2 | 01, C1; адаптери C2/D2/E1 підключаються через узгоджений interface, зводяться у J1 |
| G2 | B2, C2, F2 |
| J1 | A2, B2, C2, D2, E3, F2, G2, H1, I1, 02 |
| J2 | J1; видалення public aliases — лише за compatibility schedule із 01 |
| J3 | J2; якщо alias removal відкладений на major, J2 завершується з aliases і явним строком |
| K1 | I1 для достовірного packaged benchmark; не є залежністю J1–J3 |

02 — не привід відкладати P0: A1/B1/C1/D1/F1 можуть створювати локальні regression fixtures відразу. До спільної інтеграції fixtures узгоджуються з 02. I1 можна готувати паралельно, але його фінальні release gates мають охоплювати вже інтегровані contracts.

## Безпечна паралельність у checkout

Логічних гілок більше, ніж безпечних одночасних редакторів. `TgCommands.cs`, `Output.cs`, `ExportSchema.cs` і `ExportSupport.cs` спільні для багатьох напрямків.

- Призначити одного інтегратора спільних handlers; виконавці гілок володіють своїми helpers/tests і готують невеликі адаптерні patches.
- `TgCommands.cs`: C володіє read handlers, D export handler, E download handlers, F загальним CLI contract; інтегратор вносить ці зміни послідовно після узгодження interfaces.
- `ExportSupport.cs`: C1 має першість на paginator; D1 робить мінімальний захист deletion у своєму handler, D2 починається після C1.
- `Output.cs`/`ExportSchema.cs`: F2 володіє загальною serialization/schema, E1 attachment DTO/metadata; інтегрувати погоджені ділянки, не дві паралельні перебудови файла.
- B володіє session lifecycle; G змінює MCP gate/backend після B2. G1 transport negotiation незалежна і може йти одразу.
- H і I майже незалежні від core handlers. A1 ізольована в BotCommands. Практичний старт: 3–4 implementation streams плюс integration/review; для решти паралельно писати fixtures/дослідження.
- Окремі worktrees/branches полегшують ізоляцію, але не усувають логічних конфліктів. Кожен merge повторює відповідні regression tests.

## Перевірка повноти декомпозиції

| Знахідки | Власники |
|---|---|
| F01, F15 | A1 |
| F02, F07, F18 (export integrity) | D1 → D2 |
| F03–F05, F08, F20 (cursor), F21 (pagination) | C1 → C2 |
| F06 | D2 |
| F09 | B2, adoption C/D/E/G |
| F10, F19, F20 (multi-query JSON) | F1 |
| F11, F16, F21 (statistics) | C2 |
| F12–F13 | B1 → B2 |
| F14 | E1 → E2 |
| F17, F18 (result contract) | 01 → F2 → G2 |
| F22–F23 | H1 |
| F24 | G1 |
| F25 | I1 |
| Додатки: token parser/BotFather outcomes | A2 |
| Додатки: exact attachment ID/index drift | E1 |
| Додатки: context bounds/cycles, links, metrics semantics/fields | C2 |
| Додатки: unbounded secret archive, auth-state races, native drain | B1/B2 |
| Додатки: response error size, protocol mode | G1 |
| Додатки: partial copy, lost batch receipts, export buffering | E2, D2 |
| H01 cold list, H02 counter/edit/delete freshness | C2, B2, J1 |
| H03 local enrichment | B2 policy, C2/G2 implementation |
| H04 crash/concurrency/kill/rate limits | B2, D2, E2/E3, J1 |
| H05 links/context | C2, J1 |
| H06 installers concurrent edits | H1, J1 |
| S1–S8 simplifications | 01/B/C/D/E/F/G, потім J2 |
| S9 optional distribution | K1 |

## Загальний цикл задачі

- [ ] Відтворити confirmed finding або перевірити H на контрольованій моделі.
- [ ] Додати regression test, який показує конкретний збій (для H — підтвердити або спростувати перед зміною поведінки).
- [ ] Реалізувати мінімальне виправлення у межах ownership; погоджені структурні зміни — тільки у відповідній задачі.
- [ ] Запустити цільові перевірки та суміжні contracts; зберегти evidence і compatibility notes.
- [ ] Пройти незалежний review, інтегрувати і повторити потрібні перевірки; статус прийняття лишається за користувачем.

Review focus: порожній застарілий кеш із старим export (D1), timeout після прийняття send (A2), kill між data/manifest commit (D2), native call після cancellation (B2/G2), valid unfamiliar client config syntax (H1). Ці випадки включені в критерії відповідних задач, а не відкладені до загального тестування.
