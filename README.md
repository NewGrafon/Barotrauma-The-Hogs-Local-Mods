<img width="500" height="312" alt="negr wanna chokolate" src="https://github.com/user-attachments/assets/dc1bc031-57bc-4244-8088-8cfc5cbb9e4c" />

<H1>Моды Последователей Авгурианства</H1>

<H4>
Что нужно для установки:

1. Подписаться на коллекцию модов в Steam ->

https://steamcommunity.com/sharedfiles/filedetails/?id=3730869790

<img width="639" height="357" alt="image" src="https://github.com/user-attachments/assets/a897a45f-0326-4bc4-983e-aa34bd553cc3" />

<img width="1469" height="194" alt="image" src="https://github.com/user-attachments/assets/a2f8ad61-18bf-4724-9c78-5eb6d527cf4e" />

2. Git -> https://git-scm.com/ -> Install For Windows
3. При установке гита сделайте так:

3.1 Нажимаете Next пока не дойдете до окна где пункты будут совпадать с теми что на скрине ниже

<img width="598" height="464" alt="image" src="https://github.com/user-attachments/assets/677244d1-5093-4f62-9840-480cc115d376" />

3.2 Убираете галки с пунктов из красной зоны

3.3 И дальше жмете Next до упора пока не установится

4. Перезаходим в Стим полностью (не в аккаунт, именно Стим перезапускаем), после заходите в папку Баготравмы (в Стиме ПКМ по игре -> Управление -> Локальные файлы)
5. Если есть папка LocalMods, очистите её полностью, а если её нет то создайте, после заходите в нее, копируйте путь до этой папки в буфер обмена
6. Открываете терминал/cmd/повершел/powershell -> вводите cd "" -> в кавычки вставляете скопированный путь до папки -> нажимаете Enter

Если путь слева (перед полем ввода команды путь файловый) не изменился то введите букву диска и ":" где находится Баготравма

<img width="703" height="105" alt="image" src="https://github.com/user-attachments/assets/10e92a33-9cb6-4a3f-82f7-6bdcb8b16ee3" />

7. Вводите в консоль команду " git clone https://github.com/NewGrafon/Barotrauma-The-Hogs-Local-Mods.git . " (без ковычек)
8. В Стиме нажимаете ПКМ по Баготравме -> Свойства -> Параметры запуска -> Вставляете вот енто:

cmd /c "LocalMods\UpdateMods.bat & %command%"

9. Зайдя в игру, зайдите в настройки, а потом в Mod Gameplay Settings (ЕСЛИ ЭТОГО РАЗДЕЛА В НАСТРЙОКАХ НЕТ ТО ПРОПУСТИТЕ ПУНКТ)

<img width="260" height="678" alt="image" src="https://github.com/user-attachments/assets/9683d1ed-5f1a-44c5-98e8-3d6cd96fa7fc" />

После, выставьте такие же настройки как на скрине

<img width="905" height="647" alt="image" src="https://github.com/user-attachments/assets/49b9ef93-9065-4ee4-ad0f-e57c4261535c" />

После, нажмите Применить

10. Зайдя в лобби/матч/раунд, нажмите ESC -> Performance Enhancement

<img width="328" height="429" alt="image" src="https://github.com/user-attachments/assets/adbc2213-d915-4a1b-a78f-cd219fae01cb" />

После, выберите Preset Config "Balanced" -> После промотайте немного вниз до раздела "-- Update Control --" -> Включаете пункты:

"WARNING: Skip container item updates"

"WARNING: Skip passive world item updates"

"WARNING: Skip offscreen character updates"

"Throttle client afflication Lua events"

После, мотните наверх и нажмите Save Current Config (ЭТО ОБЯЗАТЕЛЬНО)

11. КОНЕЦ!!!

</H4>

<hr>

Пофикшеные и измененные моды (по крайней мере документно зафиксированные фиксы):
0. Хотелось бы прикрепить самый важный и крутой диалог, но его нельзя расшарить из-за того что он не просто чат, а чат+выбранная папка для изменений Claude Code, печалька :( <br> Все что дальше идет, было сделано до того как у меня появился доступ к Claude Code, и частично эти диалоги не актуальные
1. Гильзы -> https://claude.ai/share/3c83e9f1-904b-4eed-94ea-2e25ae5b8c69
2. Чето с NetworkTweaks -> https://claude.ai/share/5ef1ce3c-0d39-4d6a-ab13-7fa5268dac37
3. Сасание кислорода у костюмов из EK Mods -> https://claude.ai/share/895b6514-5929-4832-899a-dad415b84fb0
4. Краш от переполнения жопы ивентами -> https://claude.ai/share/d45f7b5c-7293-40b0-b34d-68cad350804c
5. Краш от руды -> https://claude.ai/share/d050f839-d85e-45c1-be00-30e1edea1ce4
6. Ребаланс кувалды -> https://claude.ai/share/c0282fc0-8a69-47d0-890a-9186942ecb10
7. Fix 5.56 ammo in Scout weapon crash, but maybe Scout can spawn without ammo in loot container and in NPCs
8. Increased max stacks of materials and minerals from 32 to 64 in storages and from 8 to 16 in human inventories. Its need for increase performance a little

Как создались скрипты для апдейта модов -> https://claude.ai/share/74fe9631-945f-4cc3-b8df-15286535653a + https://claude.ai/share/4bd1756e-17fe-4945-a292-b658adec5bad
