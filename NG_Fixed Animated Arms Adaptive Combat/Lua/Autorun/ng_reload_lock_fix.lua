-- ============================================================================================
--  NG fix: weapon stuck "Closed" (NonInteractable) after reloading inside a container.
--
--  AAAC's animated reload locks the gun the moment a magazine is inserted. The lock effect targets
--  THREE things at once (see e.g. M4.xml line 404, no conditional):
--      <StatusEffect type="OnInserted" target="This,Character,Contained"
--                    AllowAccess="false" noninteractable="true" delay="0.01" setvalue="true" />
--    1) the gun item      -> NonInteractable = true   (can't drag/take the gun)
--    2) the gun's ItemContainers -> AllowAccess = false (the gun's slot window won't open on hover)
--    3) every CONTAINED item (the magazine) -> NonInteractable = true ("Closed" mag, can't remove/reload)
--  It is meant to be reverted by the later OnInserted unlock effects, but those are gated behind a
--  CHARACTER weapons-skill conditional (skillrequirement weapons lt/gte 45). In a world/storage
--  container no character holds the gun, so NEITHER skill branch resolves -> nothing is reverted ->
--  the gun, its container access, AND the loaded magazine all stay locked permanently. The fallback
--  reset effects (Type="Always" OneShot="True", lines ~346-350) only run while the item is ACTIVE and
--  only ONCE per lifetime, so picking the gun up later does not heal it either. NonInteractable is also
--  saved to the save file (IsPropertySaveable.Yes), so a stuck gun stays stuck across reloads.
--  Same mechanic (and same bug) on every AAAC firearm (~44).
--
--  Fix (generic, no per-gun XML edits): fully revert the lock for any RangedWeapon that is NOT owned by
--  a character — clear the gun's NonInteractable, restore AllowAccess on its VISIBLE containers, and
--  clear NonInteractable on the items inside them. Two entry points:
--    * OnItemContained — when a magazine is inserted into a container-bound gun, revert after the
--      reload window (so a fresh reload-in-container never gets stuck).
--    * roundStart sweep — heal guns that are ALREADY stuck (e.g. loaded from a save, or broken before
--      this fix existed). One pass per round, cheap.
--  Only ever CLEARS locks, never sets them; skips character-owned guns entirely, so the vanilla in-hand
--  reload animation is untouched. Hidden proxy containers (drawinventory="false": the M4reloading /
--  M4chamber proxies, M4chamber is intentionally NonInteractable) are skipped. No SERVER/CLIENT guard ->
--  runs on both sides like the XML status effects -> multiplayer-safe.
--  RUS: Фикс «оружие застряло в режиме Закрыто после перезарядки в контейнере». Лок AAAC при вставке
--  RUS: магазина бьёт по трём целям сразу: сам ствол (NonInteractable), его контейнеры (AllowAccess=false,
--  RUS: окно не открыть) и магазин внутри (NonInteractable, «закрыто»). Разблокировка висит на навыке
--  RUS: ПЕРСОНАЖА — в контейнере персонажа нет, ничего не снимается. Резервный сброс (Always OneShot)
--  RUS: одноразовый и только пока предмет активен, поэтому подбор в руки тоже не лечит. NonInteractable
--  RUS: сохраняется в сейв -> ствол остаётся сломанным после перезагрузки. Чиним комплексно: для любого
--  RUS: RangedWeapon не у персонажа снимаем NonInteractable со ствола, AllowAccess=true на ВИДИМЫХ
--  RUS: контейнерах, NonInteractable=false с содержимого. Два входа: OnItemContained (новая перезарядка
--  RUS: в контейнере) и roundStart (лечение уже застрявших/сохранённых). Только снимаем, стволы у
--  RUS: персонажа не трогаем. Скрытые прокси-контейнеры (drawinventory="false") пропущены. На сервере И
--  RUS: клиенте -> MP-safe.
-- ============================================================================================

local UNLOCK_DELAY_MS = 3000  -- longer than the slowest AAAC reload (~2.55s low-skill)
local ROUNDSTART_DELAY_MS = 1000 -- let the round/items settle before the heal sweep

-- True only when a Character owns the gun (held or anywhere in their inventory) -> the vanilla
-- skill-gated unlock applies, so we leave it alone. A gun in a world container returns false.
-- RUS: True только если ствол принадлежит персонажу -> ванильный анлок справится, не трогаем.
local function isOwnedByCharacter(item)
    if item == nil then return false end
    local owner = item.GetRootInventoryOwner()
    return owner ~= nil and LuaUserData.IsTargetType(owner, "Barotrauma.Character")
end

-- Technical contained items that must NEVER become interactable: `repair` (a hidden durability proxy —
-- removing it breaks the gun) and `weapons-up`, plus the reload/chamber proxies (defensive — those
-- normally live in drawinventory="false" containers and are skipped anyway). Unlike the magazine, these
-- must stay locked, so we force NonInteractable=true on them (also re-locks any that an earlier buggy
-- pass wrongly freed).
-- RUS: Технические предметы внутри, которые НИКОГДА не должны стать снимаемыми: `repair` (скрытый
-- RUS: прокси прочности — снять = сломать ствол) и `weapons-up`, плюс прокси перезарядки/патронника
-- RUS: (на всякий случай — они обычно в drawinventory="false" и так пропущены). В отличие от магазина их
-- RUS: держим заблокированными: принудительно NonInteractable=true (заодно перелочит ошибочно
-- RUS: разлоченные прошлым багом).
local function idLower(item)
    local ok, s = pcall(function() return tostring(item.Prefab.Identifier) end)
    if ok and s then return string.lower(s) end
    return ""
end

local function isTechnicalContained(item)
    local s = idLower(item)
    return s == "repair" or s == "weapons-up"
        or s:find("reloading", 1, true) ~= nil or s:find("chamber", 1, true) ~= nil
end

-- Fully revert the reload lock on a container-bound gun: gun + its visible containers + the MAGAZINE,
-- while keeping technical proxies (repair/weapons-up/...) locked. Idempotent — safe to call on any
-- non-held gun (no-op on a healthy one).
-- RUS: Полностью снять лок перезарядки: ствол + видимые контейнеры + МАГАЗИН, но технические прокси
-- RUS: (repair/weapons-up/...) оставить заблокированными. Идемпотентно — безопасно на любом стволе не
-- RUS: у персонажа (на здоровом — no-op).
local function unlockGun(gun)
    if gun == nil or gun.Removed then return end
    gun.NonInteractable = false
    for comp in gun.Components do
        if LuaUserData.IsTargetType(comp, "Barotrauma.Items.Components.ItemContainer") and comp.DrawInventory then
            comp.AllowAccess = true
            local inv = comp.Inventory
            if inv ~= nil then
                for it in inv.AllItems do
                    if it ~= nil and not it.Removed then
                        it.NonInteractable = isTechnicalContained(it) -- technical: stay locked; magazine: unlock
                    end
                end
            end
        end
    end
end

local function isRangedWeapon(item)
    return item ~= nil and not item.Removed and item.GetComponentString("RangedWeapon") ~= nil
end

-- Entry 1: a magazine just got inserted into a gun. If that gun is container-bound (no character owner),
-- revert the lock after the reload window so it never gets stuck.
-- RUS: Вход 1: магазин вставлен в ствол. Если ствол в контейнере (без персонажа) — снимаем лок после
-- RUS: окна перезарядки, чтобы не застрял.
Hook.Patch(
    "ng.aaac.reloadlockfix.oncontained",
    "Barotrauma.Items.Components.ItemContainer",
    "OnItemContained",
    function(instance, ptable)
        local container = instance
        if container == nil then return end
        local gun = container.Item
        if not isRangedWeapon(gun) then return end
        if isOwnedByCharacter(gun) then return end

        Timer.Wait(function()
            if isOwnedByCharacter(gun) then return end -- player may have grabbed it during the wait
            local ok, err = pcall(unlockGun, gun)
            if not ok then
                print('[NG] [AAAC reload-lock fix] unlock error: ' .. tostring(err))
            end
        end, UNLOCK_DELAY_MS)
    end,
    Hook.HookMethodType.After
)

-- Entry 2: heal already-stuck guns at the start of each round (covers saves and pre-fix breakage).
-- RUS: Вход 2: лечим уже застрявшие стволы в начале каждого раунда (сейвы и поломки до фикса).
-- We can't cheaply tell a partially-stuck gun (gun interactable, but its container/magazine still
-- locked) from a healthy one, so we just run the idempotent unlockGun on every non-character-owned
-- ranged weapon. On a healthy gun it's a no-op (locks are already clear); on a stuck one it heals all
-- three lock layers. One pass per round -> cheap (only a handful of world guns).
-- RUS: Дёшево отличить частично-застрявший ствол (сам разблокирован, но контейнер/магазин ещё закрыты)
-- RUS: от здорового нельзя, поэтому просто зовём идемпотентный unlockGun на каждом стволе не у
-- RUS: персонажа. На здоровом — no-op; на застрявшем — снимает все три слоя. Раз за раунд -> дёшево.
local function healSweep()
    local list = Item.ItemList
    if list == nil then return end
    for item in list do
        if isRangedWeapon(item) and not isOwnedByCharacter(item) then
            local ok, err = pcall(unlockGun, item)
            if not ok then
                print('[NG] [AAAC reload-lock fix] heal error: ' .. tostring(err))
            end
        end
    end
end

Hook.Add("roundStart", "ng.aaac.reloadlockfix.roundstart", function()
    Timer.Wait(healSweep, ROUNDSTART_DELAY_MS)
end)

-- Manual heal on demand — run it to fix already-stuck guns without reloading the round. Runs the sweep
-- on the local side; on a dedicated server / co-op, a round reload (the roundStart sweep) heals every
-- machine. Mainly a singleplayer/host convenience.
-- RUS: Ручное лечение — команда чинит уже застрявшие стволы без перезагрузки раунда. Выполняется на
-- RUS: локальной стороне; в со-опе надёжнее перезайти в раунд (сработает roundStart на всех машинах).
pcall(function()
    Game.AddCommand("ng_aaac_unlock", "NG: unlock AAAC guns stuck NonInteractable after reloading in a container", function(args)
        healSweep()
        print('[NG] [AAAC reload-lock fix] heal sweep done.')
    end)
end)
