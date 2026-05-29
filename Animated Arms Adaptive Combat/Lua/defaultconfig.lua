-- НЕ РЕДАКТИРУЙТЕ ЭТОТ КОНФИГ, ЭТО ПРОСТО ШАБЛОН
-- DO NOT EDIT THIS CONFIG, THIS IS JUST A TEMPLATE
-- 不要编辑此配置文件，这只是一个模板

local config = {}

config.accumulatorMax = 150

config.clientMapEntityUpdateInterval = 4
config.serverMapEntityUpdateInterval = 1

config.clientCharacterUpdateInterval = 1
config.serverCharacterUpdateInterval = 1

config.poweredUpdateInterval = 1

config.clientItemHighPriority = {
    "door", "doorwbuttons", "windoweddoorwbuttons", "windoweddoor", "hatchwbuttons", "sonartransducer", "divingsuit",
    "combatdivingsuit", "abyssdivingsuit", "pucs", "slipsuit",
    "battery", "delaycomponent", "acidmistemitter"
}

config.serverItemHighPriority = { "battery",
    "delaycomponent", "acidmistemitter"
}

config.clientComponentPriority = { "Engine", "Pump", "Sonar", "Fabricator", "Deconstructor", "Reactor", "Turret",
    "Controller" }
config.serverComponentPriority = { "Engine", "Pump", "Sonar", "Fabricator", "Deconstructor", "Reactor", "Turret",
    "Controller" }

config.highPriorityCharacters = { "Human" }

config.serverHighPriorityHands = true
config.clientHighPriorityHands = true

config.allowSingleplayerPermanentConfigs = false

config.disableShadowCastingLights = false
config.disableDrawBehindSubsLights = false
config.hideInGameWires = false
config.hideInGameComponents = false

return config