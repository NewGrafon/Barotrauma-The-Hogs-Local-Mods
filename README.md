<img width="500" height="312" alt="negr wanna chokolate" src="https://github.com/user-attachments/assets/dc1bc031-57bc-4244-8088-8cfc5cbb9e4c" />

<H1>Моды Последователей Авгурианства</H1>

Что нужно для установки:
1. Git -> https://git-scm.com/ -> Install For Windows
2. При установке гита сделайте так:
 
 2.1 Нажимаете Next пока не дойдете до окна где пункты будут совпадать с теми что на скрине ниже
 <img width="598" height="464" alt="image" src="https://github.com/user-attachments/assets/677244d1-5093-4f62-9840-480cc115d376" />
 
 2.2 Убираете галки с пунктов из красной зоны
 
 2.3 И дальше жмете Next до упора пока не установится

3. Заходите в папку Баготравмы (в Стиме ПКМ по игре -> Управление -> Локальные файлы)
4. Создаете папку LocalMods, если её нет, заходите в нее, копируйте путь до этой папки в буфер обмена
5. Открываете терминал/cmd/повершел/powershell -> вводите cd "" -> в кавычки вставляете скопированный путь до папки -> нажимаете Enter

Если путь слева (перед полем ввода команды путь файловый) не изменился то введите букву диска и ":" где находится Баготравма

<img width="626" height="122" alt="image" src="https://github.com/user-attachments/assets/91a92084-f47e-4321-a750-81aac745b66d" />

6. Вводите в консоль команду " git clone https://github.com/NewGrafon/Barotrauma-The-Hogs-Local-Mods.git . " (без ковычек)
7. В Стиме нажимаете ПКМ по Баготравме -> Свойства -> Параметры запуска -> Вставляете вот енто:

cmd /c "cd LocalMods && git pull && copy /Y Nyblya.xml .. && cd .. && move /Y Nyblya.xml ModLists\Nyblya.xml && powershell -ExecutionPolicy Bypass -File UpdateMods.ps1"

Пофикшеные моды (по крайней мере документно зафиксированные фиксы):
1. Гильзы -> https://claude.ai/share/3c83e9f1-904b-4eed-94ea-2e25ae5b8c69
2. Чето с NetworkTweaks -> https://claude.ai/share/5ef1ce3c-0d39-4d6a-ab13-7fa5268dac37
3. Сасание кислорода у костюмов из EK Mods -> https://claude.ai/share/895b6514-5929-4832-899a-dad415b84fb0
4. Краш от переполнения жопы ивентами -> https://claude.ai/share/d45f7b5c-7293-40b0-b34d-68cad350804c
5. Краш от руды -> https://claude.ai/share/d050f839-d85e-45c1-be00-30e1edea1ce4
6. Ребаланс кувалды -> https://claude.ai/share/c0282fc0-8a69-47d0-890a-9186942ecb10

Как создались скрипты для апдейта модов -> https://claude.ai/share/74fe9631-945f-4cc3-b8df-15286535653a
