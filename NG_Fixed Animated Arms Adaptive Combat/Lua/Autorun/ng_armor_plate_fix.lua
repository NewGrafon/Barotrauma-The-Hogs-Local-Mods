-- ============================================================================================
--  NG fix: armor plates lost when the vest is taken off / on round end (save & reload), WITH wear kept.
--
--  AAAC plate mechanic: inserting a plate (ceramic/steel/kevlar) into a body armor applies a buff
--  affliction to the wearer (armored_ceram/steel/kevlar) and DESTROYS the plate item — so an installed
--  plate exists only as a character affliction. The buff strength IS the plate's remaining durability
--  (combat reduces it). While the armor is worn its OnWearing refreshes `armored_proxy`(=100) each second;
--  the proxy decays -50/s, and the moment the vest stops being worn (taken off, into hands, dropped) the
--  proxy is no longer refreshed and at <=25 (~1.5s later) a status effect wipes armored_ceram/steel/kevlar
--  (Afflictions.xml ~253-258). The plate item is already gone -> the plate is simply LOST. Same on round
--  end / save+reload. An armor can hold all 3 plate types at once (3 separate buffs).
--
--  Fix (wear-preserving, no exploit):
--    * POLL (server, 0.5s): if a wearer has any plate buff but is NOT wearing a body armor, the vest was
--      removed and the plate is about to be wiped -> hand it back as an item, with the item's condition set
--      to the current buff strength (so its wear is preserved). Runs OUTSIDE inventory ops (no re-entrancy).
--    * ROUND END: convert every still-active plate to an item before the save (the wearer keeps the armor
--      on across the transition, so the poll wouldn't catch it).
--    * INSERT OVERRIDE: when a plate is put into a body armor, the vanilla OnInserted applies the buff at a
--      fixed 100; we override it to the plate item's actual condition, so re-inserting a worn (e.g. 77%)
--      plate restores armored_X=77, NOT 100. This is what makes returning worn plates non-exploitable.
--  We only ever convert buff<->item 1:1 with the wear carried on the item, so nothing is lost and nothing
--  is gained. Server-authoritative (afflictions + spawning) -> server-side only; changes sync to clients.
--  Plate durability scale is 0..100 (plate items have no maxcondition override -> default 100), matching
--  the affliction strength 0..100. The cosmetic armor-handle props are NOT spawned (they'd be junk).
--  RUS: Фикс пропажи боевой пластины при снятии брони / на конце раунда, С СОХРАНЕНИЕМ ИЗНОСА. Пластина =
--  RUS: бафф-аффликция (сила = остаток прочности), сам предмет удаляется. При снятии брони proxy затухает
--  RUS: и стирает бафф -> пластина потеряна. Чиним: ОПРОС (0.5с) возвращает пластину предметом с condition
--  RUS: = текущей силе баффа; roundEnd — то же до сейва; хук ВСТАВКИ переопределяет бафф (ваниль ставит
--  RUS: фикс. 100) на condition вставленной пластины -> ре-вставка изношенной даёт её износ, не 100 (нет
--  RUS: эксплойта). Конвертация баффа<->предмета 1:1 с переносом износа: ничего не теряется и не множится.
-- ============================================================================================

local PLATES = {
    { aff = "armored_ceram",  item = "ceramic-plate" },
    { aff = "armored_steel",  item = "steel-plate"   },
    { aff = "armored_kevlar", item = "kevlar-plate"  },
}
local RETRIEVE_THRESHOLD = 1   -- wear is preserved on the item, so saving a worn plate is NOT an exploit
local POLL_MS = 500            -- well under the ~1.5s armored_proxy decay window before the wipe fires
local INSERT_OVERRIDE_MS = 120 -- after vanilla OnInserted sets the buff to 100, override to plate condition

local function isServerSide()
    return SERVER or Game.IsSingleplayer or (Game.IsMultiplayer and not CLIENT)
end

local function isValidCharacter(character)
    if character == nil then return false end
    local ok = true
    pcall(function()
        if character.Removed or character.IsDead
           or character.CharacterHealth == nil or character.Inventory == nil then ok = false end
    end)
    return ok
end

local function idLower(item)
    local ok, s = pcall(function() return tostring(item.Prefab.Identifier) end)
    if ok and s then return string.lower(s) end
    return ""
end

local function plateForItem(item)
    if item == nil then return nil end
    local id = idLower(item)
    for _, p in ipairs(PLATES) do
        if id == p.item then return p end
    end
    return nil
end

-- (strength, affliction) for a plate buff, or (0, nil).
local function getPlateAffliction(character, identifier)
    local aff = nil
    pcall(function() aff = character.CharacterHealth.GetAffliction(identifier, false) end)
    if aff == nil then pcall(function() aff = character.CharacterHealth.GetAffliction(identifier) end) end
    if aff == nil then return 0, nil end
    local s = 0
    pcall(function() s = tonumber(aff.Strength) or 0 end)
    return s, aff
end

local function clearPlateAffliction(character, identifier, aff)
    if aff ~= nil then pcall(function() aff.Strength = 0 end) end
    pcall(function() character.CharacterHealth.ReduceAffliction(identifier, 1000000) end)
    pcall(function() character.CharacterHealth.ReduceAfflictionOnAllLimbs(identifier, 1000000) end)
end

local function setPlateStrength(character, identifier, value)
    local _, aff = getPlateAffliction(character, identifier)
    if aff == nil then return end
    pcall(function() aff.Strength = value end)
    pcall(function() aff.SetStrength(value) end)
end

-- Queue a plate item into the wearer's inventory with the given condition (carries the wear); optionally
-- force-flush the spawn queue so it exists immediately (used at round end, before the save).
local function spawnPlate(character, itemId, condition, forceFlush)
    local prefab = nil
    pcall(function() prefab = ItemPrefab.GetItemPrefab(itemId) end)
    if prefab == nil then return false end
    local ok = false
    pcall(function()
        Entity.Spawner.AddItemToSpawnQueue(prefab, character.Inventory, condition, nil, nil, true)
        ok = true
    end)
    if ok and forceFlush then pcall(function() Entity.Spawner.Update(true) end) end
    return ok
end

-- Convert every active plate buff back into a plate item, carrying its wear as the item's condition.
-- Spawn FIRST; remove a buff only once its spawn is confirmed queued, so a failure can't lose the plate.
local function retrievePlate(character, forceFlush, threshold)
    threshold = threshold or RETRIEVE_THRESHOLD
    if not isValidCharacter(character) then return end
    for _, p in ipairs(PLATES) do
        local strength, aff = getPlateAffliction(character, p.aff)
        if strength > 0 and strength >= threshold then
            if spawnPlate(character, p.item, strength, forceFlush) then -- condition = wear
                clearPlateAffliction(character, p.aff, aff)
            end
        end
    end
end

-- "Wearing a body armor" = the OuterClothes slot is occupied. NOTE: don't test InvSlotType via tostring —
-- a Flags enum stringifies to its NUMBER in Lua, not the name.
local function isWearingBodyArmor(character)
    local worn = nil
    pcall(function() worn = character.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes) end)
    return worn ~= nil
end

-- A body-armor item has OuterClothes among its AllowedSlots (used to scope the insert-override).
local function isBodyArmorItem(item)
    if item == nil then return false end
    local found = false
    pcall(function()
        for slot in item.AllowedSlots do
            if slot == InvSlotType.OuterClothes then found = true end
        end
    end)
    return found
end

-- Trigger 1: poll. A plate buff while NO body armor is worn = the vest was removed and the plate is about
-- to be wiped -> hand it back now (before the proxy decay wipes it). Generation guard so hot-reload doesn't
-- stack polling loops.
-- RUS: Триггер 1: опрос. Бафф пластины без надетой брони = снято, пластина вот-вот сотрётся -> вернуть.
NG_AAAC_PLATEFIX = NG_AAAC_PLATEFIX or {}
NG_AAAC_PLATEFIX.gen = (NG_AAAC_PLATEFIX.gen or 0) + 1
local myGen = NG_AAAC_PLATEFIX.gen
local function pollTick()
    if NG_AAAC_PLATEFIX.gen ~= myGen then return end
    if isServerSide() then
        pcall(function()
            for character in Character.CharacterList do
                if isValidCharacter(character) and character.IsHuman and not isWearingBodyArmor(character) then
                    retrievePlate(character, true)
                end
            end
        end)
    end
    Timer.Wait(pollTick, POLL_MS)
end
Timer.Wait(pollTick, POLL_MS)

-- Trigger 2: round end. Convert every still-active plate to an item before the save.
-- RUS: Триггер 2: конец раунда. Все ещё надетые пластины -> предметы до сейва.
Hook.Add("roundEnd", "ng.aaac.platefix.roundend", function()
    if not isServerSide() then return end
    pcall(function()
        for character in Character.CharacterList do
            if isValidCharacter(character) and character.IsHuman then
                retrievePlate(character, true)
            end
        end
    end)
end)

-- Trigger 3: insert override. When a plate is inserted into a body armor, vanilla OnInserted sets the buff
-- to a fixed 100. Override it to the plate item's actual condition (its preserved wear), so re-inserting a
-- worn plate restores its real strength instead of resetting to full. Fresh (100%) plates are unaffected.
-- RUS: Триггер 3: переопределение вставки. Ваниль ставит бафф = 100; меняем на condition пластины (её
-- RUS: сохранённый износ) -> ре-вставка изношенной даёт реальную силу, а не сброс до 100. Целые (100%) — без изменений.
Hook.Patch(
    "ng.aaac.platefix.insert",
    "Barotrauma.Items.Components.ItemContainer",
    "OnItemContained",
    function(instance, ptable)
        if not isServerSide() then return end
        local container = instance
        if container == nil then return end
        local armor = container.Item
        if not isBodyArmorItem(armor) then return end
        local plate = ptable["containedItem"]
        local pdef = plateForItem(plate)
        if pdef == nil then return end

        local cond = 100
        pcall(function() cond = tonumber(plate.Condition) or 100 end)
        if cond > 100 then cond = 100 elseif cond < 1 then cond = 1 end

        local owner = nil
        pcall(function() owner = armor.GetRootInventoryOwner() end)
        if owner == nil or not LuaUserData.IsTargetType(owner, "Barotrauma.Character") then return end

        -- after vanilla OnInserted has applied the fixed-100 buff, set it to the plate's real wear
        Timer.Wait(function()
            if isValidCharacter(owner) then setPlateStrength(owner, pdef.aff, cond) end
        end, INSERT_OVERRIDE_MS)
    end,
    Hook.HookMethodType.Before
)

-- Diagnostics: `ng_plate` prints each human character's plate-buff strengths + worn slot, then force-runs a
-- retrieve (threshold 1) to verify the spawn.
pcall(function()
    Game.AddCommand("ng_plate", "NG: armor plate fix diagnostics + force retrieve", function(args)
        print("[NG plate] serverSide=" .. tostring(isServerSide())
            .. " IsSingleplayer=" .. tostring(Game.IsSingleplayer))
        local found = 0
        pcall(function()
            for c in Character.CharacterList do
                if isValidCharacter(c) and c.IsHuman then
                    found = found + 1
                    local name, worn = "?", nil
                    pcall(function() name = tostring(c.Name) end)
                    pcall(function()
                        local w = c.Inventory.GetItemInLimbSlot(InvSlotType.OuterClothes)
                        if w ~= nil then worn = tostring(w.Prefab.Identifier) end
                    end)
                    local parts = {}
                    for _, p in ipairs(PLATES) do
                        local s = getPlateAffliction(c, p.aff)
                        table.insert(parts, p.aff .. "=" .. string.format("%.0f", s))
                    end
                    print(string.format("[NG plate] %s | outerSlot=%s | %s",
                        name, tostring(worn), table.concat(parts, " ")))
                    retrievePlate(c, true, 1)
                end
            end
        end)
        if found == 0 then print("[NG plate] no valid human characters found") end
        print("[NG plate] force-retrieve (threshold=1) done — check inventory / feet")
    end)
end)
