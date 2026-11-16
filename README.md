# Sandrock Fast Doors

Мод для **My Time at Sandrock**: входить в дома и выходить из них быстрее.
*English below.*

## Что делает
- Пропускает подход к двери, разворот и анимацию открытия.
- Убирает искусственные задержки экрана загрузки.
- Держит посещённые дома в памяти, поэтому повторный вход почти мгновенный.
  На ПК автора (SSD) повторный вход занимает ~0.26–0.4 с, первый ~0.75 с.
- Вместо экрана загрузки показывает непрозрачную заставку: ночное небо и случайная фраза
  (английская + русская подпись). Заставка убирается только когда игрок уже стоит на месте
  и камера встала.
- Первый заход в дом за сессию всё равно грузится с диска: это ограничение игры.

## Установка
1. Скачайте `SandrockFastDoors_v1.3.0.zip` со страницы [Releases](../../releases).
2. Steam → My Time at Sandrock → Управление → Посмотреть локальные файлы.
3. Распакуйте всё содержимое архива в эту папку (рядом с `Sandrock.exe`).
4. Запустите игру.

BepInEx 5.4.23.6 уже входит в архив. Если BepInEx 5 у вас уже стоит, достаточно папки `BepInEx/plugins`.

## Удаление
Удалите `BepInEx/plugins/SandrockFastDoors.dll` и папку `BepInEx/plugins/SandrockFastDoors`
(или `winhttp.dll`, чтобы отключить BepInEx целиком). Сохранения мод не изменяет.

## Настройки
Файл `BepInEx/config/combovine.sandrock.fastdoors.cfg` появляется после первого запуска.
Каждая опция описана прямо в нём. Основные:

| Опция | По умолчанию | Описание |
|---|---|---|
| `[Doors] SkipDoorAnimation` | `true` | без анимации двери |
| `[LoadingScreen] HideLoadingScreen` | `Always` | `Always` / `CachedScenesOnly` / `Never` |
| `[LoadingScreen] TransitionCover` | `Image` | `Image` / `Black` / `None` |
| `[LoadingScreen] CoversFolder` | `SandrockFastDoors/covers` | папка с картинками |
| `[LoadingScreen] MinCoverSeconds` | `1` | минимум секунд заставки |
| `[LoadingScreen] RevealFadeSeconds` | `0.2` | плавность исчезновения |
| `[Cache] KeepInteriorsLoaded` | `true` | держать дома в памяти |
| `[Cache] MaxCachedInteriors` | `16` | лимит домов в памяти |
| `[Preload] PreloadOnSaveLoad` | `false` | экспериментальная предзагрузка |

## Свои заставки
Положите `.png`/`.jpg` (лучше 16:9) в `BepInEx/plugins/SandrockFastDoors/covers`.

## Совместимость
Steam-версия для Windows (Unity 2021.3, Mono), одиночная игра. Мультиплеер не проверялся.
Если игра обновится и внутренности не совпадут, мод отключит несовместимые функции и запишет
ошибку в `BepInEx/LogOutput.log`.

## Сборка из исходников
Нужен .NET SDK 6+ и установленная игра с BepInEx 5:
```
cd src
dotnet build -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\My Time at Sandrock"
```
DLL игры в репозиторий не входят (они защищены авторским правом Pathea): сборка ссылается на
`Sandrock_Data\Managed` вашей установки.

## Как это работает (кратко)
Harmony-патчи `ScenarioModule.LoadCoroutine` (кеш интерьеров через штатный механизм `remain`,
пропуск `Resources.UnloadUnusedAssets`, приоритет загрузки), `Portal.GoToScenario` (анимация двери),
`LoadingMaskControl`/`LoadingMaskUI` (задержки экрана загрузки) и `Player.OnEndSceneLoad`
(момент, когда игрок поставлен на точку появления).

---

# English

**Sandrock Fast Doors** makes entering and leaving houses in *My Time at Sandrock* faster:
- skips the door walk/open animation;
- removes artificial loading-screen delays;
- keeps visited interiors in memory, so re-entry is near-instant (author's PC, SSD:
  ~0.26–0.4 s re-entry vs ~0.75 s first entry);
- replaces the loading screen with an opaque night-sky cover with a random phrase,
  removed only after the player is placed and the camera has settled.

**Install:** download the release zip, extract everything next to `Sandrock.exe`
(Steam → Manage → Browse local files). BepInEx 5.4.23.6 is bundled.
**Uninstall:** delete the plugin DLL + its folder, or `winhttp.dll`.
**Config:** `BepInEx/config/combovine.sandrock.fastdoors.cfg`.
**Custom covers:** drop `.png`/`.jpg` into `BepInEx/plugins/SandrockFastDoors/covers`.
**Build:** `dotnet build -c Release -p:GameDir="<game folder>"` in `src/` (game DLLs are not included).

## License
Mod code: MIT (see `LICENSE`). Release archives bundle BepInEx 5 (MIT), HarmonyX/MonoMod/Mono.Cecil (MIT)
and UnityDoorstop (`winhttp.dll`, LGPL-2.1, source: https://github.com/NeighTools/UnityDoorstop).
Not affiliated with Pathea.
