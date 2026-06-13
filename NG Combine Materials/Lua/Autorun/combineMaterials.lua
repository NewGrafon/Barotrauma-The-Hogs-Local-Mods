-- NG Combine Materials
-- Materials/minerals (Material category) with PARTIAL condition can be combined:
-- drag one item onto another of the same type in the inventory -> their conditions add up
-- (two halves -> one whole). Like Combinable Ammo, but for materials.
-- RUS: Материалы/минералы (категория Material) с НЕПОЛНОЙ прочностью можно объединять:
-- RUS: перетащи один предмет на другой того же типа в инвентаре -> их прочности сложатся
-- RUS: (две половинки -> одна целая). Аналог Combinable Ammo, но для материалов.

-- Console tag + best-effort RU/EN by game language (English fallback if undetectable from Lua).
-- RUS: Тег консоли + best-effort RU/EN по языку игры (фоллбэк на английский, если из Lua не определить).
local TAG = "[NG] [Combine Materials] "
local IS_RU = false
pcall(function()
    local gs = LuaUserData.CreateStatic("Barotrauma.GameSettings")
    local lang = tostring(gs.CurrentConfig.Language)
    if lang and string.find(string.lower(lang), "russ") then IS_RU = true end
end)
local function T(ru, en) if IS_RU then return ru else return en end end

print(TAG .. T("загружен", "loaded"))

-- В Lua категория приходит ЧИСЛОМ (флаги MapEntityCategory). Material = 1024.
local MATERIAL_BIT = 1024

-- Помечает один предмет как объединяемый (если это материал).
local function makeCombinable(item)
    if item == nil or item.Prefab == nil then return end

    local catNum = tonumber(tostring(item.Prefab.Category)) or 0
    local isMaterial = (math.floor(catNum / MATERIAL_BIT) % 2) == 1
    -- Минералы/руды (физикорий, фулгурий, дементонит, инцендиум и т.п.) имеют тег "ore",
    -- но категорию "Weapon,Alien" (без бита Material) — поэтому ловим их ещё и по тегу.
    if not (isMaterial or item.HasTag("ore")) then return end

    -- Флаг ставим РОВНО на один компонент (Item.Combine опрашивает все -> иначе двойной перенос прочности).
    local comp =
        item.GetComponentString("Holdable")
        or item.GetComponentString("MeleeWeapon")
        or item.GetComponentString("RangedWeapon")
        or item.GetComponentString("Throwable")
        or item.GetComponentString("Pickable")

    if comp ~= nil then
        comp.CanBeCombined = true
        comp.RemoveOnCombined = false
    end
end

-- 1) Будущие предметы — при создании.
Hook.Add("item.created", "NG_CombineMaterials.itemCreated", function(item)
    makeCombinable(item)
end)

-- 2) Уже существующие предметы — проходим по всему миру.
local function applyToAllExisting()
    local ok, err = pcall(function()
        local n = 0
        for key, value in pairs(Item.ItemList) do
            makeCombinable(value)
            n = n + 1
        end
        print(TAG .. T("обработано существующих предметов: ", "processed existing items: ") .. tostring(n))
    end)
    if not ok then
        print(TAG .. T("ошибка при обработке существующих предметов: ", "error processing existing items: ") .. tostring(err))
    end
end

-- сразу при загрузке скрипта (важно для reloadlua посреди игры)
applyToAllExisting()
-- и на старте каждого раунда
Hook.Add("roundStart", "NG_CombineMaterials.roundStart", applyToAllExisting)
