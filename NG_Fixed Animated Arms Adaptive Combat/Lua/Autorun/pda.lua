if SERVER then return end

terminals = {}
terminalLookup = {}
delayedTerminals = {}
nextTerminalDelayCheck = nil
terminalCreationTimes = {} -- Store creation time for each terminal
terminalLanguages = {} -- Store language preference for each terminal
terminalStates = {} -- Store state for each terminal (quiz, guess, etc.)
userLanguageInitialized = {} -- Track if user language has been initialized for this terminal

Hook.Patch("Barotrauma.Items.Components.Terminal", "ShowOnDisplay", function(terminal, ptable)
	--get input
	local input = ptable["input"]

	--find terminal
	local terminalKey = tostring(terminal.item.ID)
	terminalCur = terminalLookup[terminalKey]

	if (terminalCur==nil) then
		--create if new
		terminalCur = TerminalClass:new(nil, terminal)
		table.insert(terminals, terminalCur)
		terminalLookup[terminalKey] = terminalCur
		
		-- Store terminal creation time (game time)
		local terminalId = tostring(terminalCur.instance.item.ID)
		if not terminalCreationTimes[terminalId] then
			terminalCreationTimes[terminalId] = os.clock()
		end
		
		-- Initialize language based on user's game language (only once)
		if not terminalLanguages[terminalId] and not userLanguageInitialized[terminalId] then
			-- Get user's game language
			local userLang = tostring(GameSettings.CurrentConfig.Language)
			
			-- Map game language to our language codes
			if userLang == "Russian" then
				terminalLanguages[terminalId] = "ru"
			elseif userLang == "Chinese" or userLang == "ChineseSimplified" then
				terminalLanguages[terminalId] = "cn"
			else
				terminalLanguages[terminalId] = "en" -- Default to English for other languages
			end
			
			userLanguageInitialized[terminalId] = true
		elseif not terminalLanguages[terminalId] then
			terminalLanguages[terminalId] = "en"
		end
		
		-- Initialize terminal state
		if not terminalStates[terminalId] then
			terminalStates[terminalId] = {
				mode = "idle",
				quiz = {
					active = false,
					currentQuestion = nil,
					score = 0,
					waitingForContinuation = false,
					lastAnswerCorrect = false,
					correctAnswersInRow = 0, -- Track consecutive correct answers for progressive XP
					justStarted = false -- Flag to prevent auto-answering with "quiz"
				},
				guess = {
					active = false,
					secretNumber = nil,
					attempts = 0,
					maxAttempts = 10,
					lastGuess = nil
				}
			}
		end
	end

	--update mode
	prevent = true
	if (terminalCur.mode==TerminalMode.NULL) then
		prevent = terminalCur:prompt(input)
	elseif (terminalCur.mode==TerminalMode.PRINT) then
		terminalCur.mode = TerminalMode.NULL
		prevent = false
	elseif (terminalCur.mode==TerminalMode.READ) then
		terminalCur.read = input
		terminalCur.mode = TerminalMode.NULL -- Добавляем сброс режима
		if terminalCur.co and coroutine.status(terminalCur.co) ~= "dead" then
			coroutine.resume(terminalCur.co)
		end
	end

	--update function
	if (prevent) then
		ptable.PreventExecution = true
	end

	if (CLIENT==false) then
		terminal.SyncHistory()
	end
end, Hook.HookMethodType.Before)

TerminalClass = {}

TerminalMode = {}
TerminalMode.NULL = 0
TerminalMode.PRINT = 1
TerminalMode.READ = 2
TerminalMode.DELAY = 4

function TerminalClass:new(o, instance)
	o = o or {}
	setmetatable(o, self)
	self.__index = self
	self.instance = instance
	self.mode = TerminalMode.NULL
	return o
end

function TerminalClass:prompt(input)
	-- Remove spaces and convert to lowercase for command matching
	local cleanInput = string.gsub(input, "%s+", "")
	cleanInput = string.lower(cleanInput)
	
	-- Get current terminal language and state
	local terminalId = tostring(self.instance.item.ID)
	local currentLang = terminalLanguages[terminalId] or "en"
	local terminalState = terminalStates[terminalId]
	
	-- Always reset script variable at the start
	local script = nil
	
	-- Check if we're in an active game state and handle continuation first
	if terminalState.quiz.waitingForContinuation then
		if cleanInput == "да" or cleanInput == "yes" or cleanInput == "是" then
			-- Continue quiz
			terminalState.quiz.waitingForContinuation = false
			self:startNewQuizQuestion(terminalId, currentLang, terminalState)
			return true
			elseif cleanInput == "нет" or cleanInput == "no" or cleanInput == "否" then
				-- End quiz
				terminalState.quiz.active = false
				terminalState.quiz.waitingForContinuation = false
				terminalState.quiz.correctAnswersInRow = 0 -- Reset streak when ending quiz
				terminalState.mode = "idle"
				
				if currentLang == "ru" then
					TerminalPrint("Викторина завершена!")
					TerminalPrint("Финальный счет: " .. terminalState.quiz.score)
					TerminalPrint("Введите любую команду для продолжения")
				elseif currentLang == "cn" then
					TerminalPrint("测验结束！")
					TerminalPrint("最终分数: " .. terminalState.quiz.score)
					TerminalPrint("输入任何命令继续")
				else
					TerminalPrint("Quiz finished!")
					TerminalPrint("Final score: " .. terminalState.quiz.score)
					TerminalPrint("Type any command to continue")
				end
				return true
			end
	end
	
	-- Check if we're in an active game state
	if terminalState.guess.active then
		return self:handleGuessGame(cleanInput, terminalId, currentLang, terminalState)
	end
	
	if terminalState.quiz.active and not terminalState.quiz.waitingForContinuation then
		-- If quiz just started, ignore the first input (which is "quiz")
		if terminalState.quiz.justStarted then
			terminalState.quiz.justStarted = false
			-- Clear the input and show the question again if needed
			if CLIENT == false then
				self.instance.History = {}
				self.instance.SyncHistory()
			end
			-- Re-display the current question
			self:displayCurrentQuestion(terminalId, currentLang, terminalState)
			return true
		end
		return self:handleQuizGame(cleanInput, terminalId, currentLang, terminalState)
	end
	
	-- Lang command - change language
	if (cleanInput == "lang") then
		if CLIENT == false then
			self.instance.History = {}
			self.instance.SyncHistory()
		end
		
		-- Language selection menu
		script = [[
			local terminalId = tostring(terminalCur.instance.item.ID)
			local currentLang = terminalLanguages[terminalId] or "en"
			
			if currentLang == "ru" then
				print("=== ВЫБОР ЯЗЫКА ===")
				print("Выберите язык:")
				print("1. English")
				print("2. Русский")
				print("3. 中文")
				print("===========================")
				print("Введите число (1-3):")
			elseif currentLang == "cn" then
				print("=== 语言选择 ===")
				print("选择语言:")
				print("1. English")
				print("2. Русский")
				print("3. 中文")
				print("===========================")
				print("输入数字 (1-3):")
			else
				print("=== LANGUAGE SELECTION ===")
				print("Select language:")
				print("1. English")
				print("2. Русский")
				print("3. 中文")
				print("===========================")
				print("Enter number (1-3):")
			end
			
			local choice = TerminalRead()
			
			if choice == "1" then
				TerminalSetLanguage("en")
				print("Language set to: English")
			elseif choice == "2" then
				TerminalSetLanguage("ru")
				print("Язык установлен: Русский")
			elseif choice == "3" then
				TerminalSetLanguage("cn")
				print("语言设置为：中文")
			else
				if currentLang == "ru" then
					print("Неверный выбор")
				elseif currentLang == "cn" then
					print("无效选择")
				else
					print("Invalid choice")
				end
			end
			
			print("===========================")
			if currentLang == "ru" then
				print("Введите любую команду для продолжения")
			elseif currentLang == "cn" then
				print("输入任何命令继续")
			else
				print("Type any command to continue")
			end
		]]
	
	-- Time command - shows how long terminal exists in game
	elseif (cleanInput == "time") then
		if CLIENT == false then
			self.instance.History = {}
			self.instance.SyncHistory()
		end
		
		local terminalId = tostring(self.instance.item.ID)
		local creationTime = terminalCreationTimes[terminalId]
		
		if creationTime then
			local totalTime = os.clock() - creationTime
			local hours = math.floor(totalTime / 3600)
			local minutes = math.floor((totalTime % 3600) / 60)
			local seconds = math.floor(totalTime % 60)
			
			local timeString = ""
			if hours > 0 then
				timeString = string.format("%d h %d min %d sec", hours, minutes, seconds)
			elseif minutes > 0 then
				timeString = string.format("%d min %d sec", minutes, seconds)
			else
				timeString = string.format("%d sec", seconds)
			end
			
			if currentLang == "ru" then
				TerminalPrint("⏰ Игровое время: " .. timeString)
			elseif currentLang == "cn" then
				TerminalPrint("⏰ 游戏时间: " .. timeString)
			else
				TerminalPrint("⏰ Game time: " .. timeString)
			end
		else
			if currentLang == "ru" then
				TerminalPrint("Таймер не запущен")
			elseif currentLang == "cn" then
				TerminalPrint("计时器未启动")
			else
				TerminalPrint("Timer not started")
			end
		end
		return true
	
	-- Mini-game "Guess the number"
	elseif (cleanInput == "guess") then
		-- Remove guess command message
		if CLIENT == false then
			self.instance.History = {}
			self.instance.SyncHistory()
		end
		
		-- Initialize guess game state
		terminalState.guess.active = true
		terminalState.guess.secretNumber = math.random(1, 100)
		terminalState.guess.attempts = 0
		terminalState.guess.maxAttempts = 10
		terminalState.guess.lastGuess = nil
		terminalState.mode = "guess"
		
		if currentLang == "ru" then
			TerminalPrint("=== УГАДАЙ ЧИСЛО ===")
			TerminalPrint("Я загадал число от 1 до 100!")
			TerminalPrint("У тебя 10 попыток угадать.")
			TerminalPrint("После каждой попытки я скажу 'больше' или 'меньше'")
			TerminalPrint("===========================")
			TerminalPrint("Попытка 1/10 - Введите число:")
		elseif currentLang == "cn" then
			TerminalPrint("=== 猜数字 ===")
			TerminalPrint("我想了一个1到100之间的数字！")
			TerminalPrint("你有10次猜测机会。")
			TerminalPrint("每次尝试后我会说'更高'或'更低'")
			TerminalPrint("===========================")
			TerminalPrint("尝试 1/10 - 输入数字:")
		else
			TerminalPrint("=== GUESS THE NUMBER ===")
			TerminalPrint("I'm thinking of a number from 1 to 100!")
			TerminalPrint("You have 10 attempts to guess.")
			TerminalPrint("After each attempt I'll say 'higher' or 'lower'")
			TerminalPrint("===========================")
			TerminalPrint("Attempt 1/10 - Enter number:")
		end
		return true
	
	-- Dice command - simplified version
	elseif (string.match(cleanInput, "^dice%d+$")) then
		-- Remove dice command message
		if CLIENT == false then
			self.instance.History = {}
			self.instance.SyncHistory()
		end
		
		-- Extract number after "dice" (e.g., "dice20" -> 20)
		local maxNumber = tonumber(string.match(cleanInput, "dice(%d+)"))
		
		if maxNumber and maxNumber > 0 then
			local result = math.random(1, maxNumber)
			if currentLang == "ru" then
				TerminalPrint("🎲 Бросок кубика: " .. result .. " (из " .. maxNumber .. ")")
			elseif currentLang == "cn" then
				TerminalPrint("🎲 骰子点数: " .. result .. " (从 " .. maxNumber .. ")")
			else
				TerminalPrint("🎲 Dice roll: " .. result .. " (from " .. maxNumber .. ")")
			end
			return true
		else
			if currentLang == "ru" then
				TerminalPrint("Использование: dice<число>")
				TerminalPrint("Пример: dice20 - бросок кубика от 1 до 20")
			elseif currentLang == "cn" then
				TerminalPrint("用法: dice<数字>")
				TerminalPrint("例子: dice20 - 投掷1到20的骰子")
			else
				TerminalPrint("Usage: dice<number>")
				TerminalPrint("Example: dice20 - rolls dice from 1 to 20")
			end
			return true
		end
	
	-- Quest command - Barotrauma quiz
	elseif (cleanInput == "quiz") then
		-- Remove quiz command message
		if CLIENT == false then
			self.instance.History = {}
			self.instance.SyncHistory()
		end
		
		-- Initialize quiz state
		terminalState.quiz.active = true
		terminalState.quiz.score = 0
		terminalState.quiz.waitingForContinuation = false
		terminalState.quiz.correctAnswersInRow = 0 -- Reset streak when starting new quiz
		terminalState.quiz.justStarted = true -- Set flag to ignore the "quiz" input
		terminalState.mode = "quiz"
		
		-- Start quiz
		self:startNewQuizQuestion(terminalId, currentLang, terminalState)
		return true
	end
	
	

	--run script if available
	if (script~=nil) then
		self.co = coroutine.create(TerminalRun)
		coroutine.resume(self.co, script)
		return true
	end




	--normal behavior
	return false
end


function TerminalClass:handleGuessGame(input, terminalId, currentLang, terminalState)
	local guessState = terminalState.guess
	
	if not guessState.active then
		return false
	end
	
	-- Remove input message
	if CLIENT == false then
		self.instance.History = {}
		self.instance.SyncHistory()
	end
	
	local guess = tonumber(input)
	
	if guess == nil then
		if currentLang == "ru" then
			TerminalPrint("Пожалуйста, введите число!")
			TerminalPrint("Попытка " .. (guessState.attempts + 1) .. "/" .. guessState.maxAttempts .. " - Введите число:")
		elseif currentLang == "cn" then
			TerminalPrint("请输入数字！")
			TerminalPrint("尝试 " .. (guessState.attempts + 1) .. "/" .. guessState.maxAttempts .. " - 输入数字:")
		else
			TerminalPrint("Please enter a number!")
			TerminalPrint("Attempt " .. (guessState.attempts + 1) .. "/" .. guessState.maxAttempts .. " - Enter number:")
		end
		guessState.lastGuess = nil
		return true
	end
	
	guessState.attempts = guessState.attempts + 1
	guessState.lastGuess = guess
	
	if guess == guessState.secretNumber then
		-- Player guessed correctly
		if currentLang == "ru" then
			TerminalPrint("ПОЗДРАВЛЯЮ! Вы угадали!")
			TerminalPrint("Загаданное число: " .. guessState.secretNumber)
			TerminalPrint("Использовано попыток: " .. guessState.attempts)
		elseif currentLang == "cn" then
			TerminalPrint("恭喜！你猜对了！")
			TerminalPrint("数字是: " .. guessState.secretNumber)
			TerminalPrint("使用尝试次数: " .. guessState.attempts)
		else
			TerminalPrint("CONGRATULATIONS! You guessed it!")
			TerminalPrint("The number was: " .. guessState.secretNumber)
			TerminalPrint("Attempts used: " .. guessState.attempts)
		end
		
		-- Reset game state
		guessState.active = false
		terminalState.mode = "idle"
		
		TerminalPrint("===========================")
		if currentLang == "ru" then
			TerminalPrint("Введите любую команду для продолжения")
		elseif currentLang == "cn" then
			TerminalPrint("输入任何命令继续")
		else
			TerminalPrint("Type any command to continue")
		end
		
	elseif guessState.attempts >= guessState.maxAttempts then
		-- Player ran out of attempts
		if currentLang == "ru" then
			TerminalPrint("К сожалению, попытки закончились!")
			TerminalPrint("Загаданное число: " .. guessState.secretNumber)
		elseif currentLang == "cn" then
			TerminalPrint("不幸的是，尝试次数用完了！")
			TerminalPrint("数字是: " .. guessState.secretNumber)
		else
			TerminalPrint("Unfortunately, attempts are over!")
			TerminalPrint("The number was: " .. guessState.secretNumber)
		end
		
		-- Reset game state
		guessState.active = false
		terminalState.mode = "idle"
		
		TerminalPrint("===========================")
		if currentLang == "ru" then
			TerminalPrint("Введите любую команду для продолжения")
		elseif currentLang == "cn" then
			TerminalPrint("输入任何命令继续")
		else
			TerminalPrint("Type any command to continue")
		end
		
	else
		-- Give hint and continue
		local remainingAttempts = guessState.maxAttempts - guessState.attempts
		
		if guess < guessState.secretNumber then
			if currentLang == "ru" then
				TerminalPrint("Больше! (" .. guess .. ") | Попытка " .. (guessState.attempts + 1) .. "/" .. guessState.maxAttempts .. " - Введите число:")
			elseif currentLang == "cn" then
				TerminalPrint("更高！(" .. guess .. ") | 尝试 " .. (guessState.attempts + 1) .. "/" .. guessState.maxAttempts .. " - 输入数字:")
			else
				TerminalPrint("Higher! (" .. guess .. ") | Attempt " .. (guessState.attempts + 1) .. "/" .. guessState.maxAttempts .. " - Enter number:")
			end
		else
			if currentLang == "ru" then
				TerminalPrint("Меньше! (" .. guess .. ") | Попытка " .. (guessState.attempts + 1) .. "/" .. guessState.maxAttempts .. " - Введите число:")
			elseif currentLang == "cn" then
				TerminalPrint("更低！(" .. guess .. ") | 尝试 " .. (guessState.attempts + 1) .. "/" .. guessState.maxAttempts .. " - 输入数字:")
			else
				TerminalPrint("Lower! (" .. guess .. ") | Attempt " .. (guessState.attempts + 1) .. "/" .. guessState.maxAttempts .. " - Enter number:")
			end
		end
	end
	
	return true
end

function TerminalClass:handleQuizGame(input, terminalId, currentLang, terminalState)
	local quizState = terminalState.quiz
	
	if not quizState.active or not quizState.currentQuestion then
		return false
	end
	
	-- Remove input message
	if CLIENT == false then
		self.instance.History = {}
		self.instance.SyncHistory()
	end
	
	local userAnswer = string.lower(input)
	local correct = false
	local currentQuestion = quizState.currentQuestion
	
	-- Check if answer is correct in selected language
	if currentLang == "ru" then
		for _, answer in ipairs(currentQuestion.answers_ru) do
			if userAnswer == answer then
				correct = true
				break
			end
		end
	elseif currentLang == "cn" then
		for _, answer in ipairs(currentQuestion.answers_cn) do
			if userAnswer == answer then
				correct = true
				break
			end
		end
	else
		for _, answer in ipairs(currentQuestion.answers_en) do
			if userAnswer == answer then
				correct = true
				break
			end
		end
	end
	
if correct then
    quizState.score = quizState.score + 1
    quizState.correctAnswersInRow = quizState.correctAnswersInRow + 1
    
    -- Calculate progressive XP based on consecutive correct answers
    local xpToGive = 5 -- Base XP
    
    -- Increase XP every 5 correct answers, up to a maximum of 20
    if quizState.correctAnswersInRow >= 15 then
        xpToGive = 20
    elseif quizState.correctAnswersInRow >= 10 then
        xpToGive = 15
    elseif quizState.correctAnswersInRow >= 5 then
        xpToGive = 10
    end
    
    -- Give experience points to the player for correct answer
    local character = self.instance.item.ParentInventory.Owner
    if character and character.IsHuman then
        character.Info.GiveExperience(xpToGive)
        if currentLang == "ru" then
            TerminalPrint("✅ ПРАВИЛЬНО! +" .. xpToGive .. " опыта!")
            TerminalPrint("🔥 Серия правильных ответов: " .. quizState.correctAnswersInRow)
        elseif currentLang == "cn" then
            TerminalPrint("✅ 正确！+" .. xpToGive .. " 经验！")
            TerminalPrint("🔥 连续正确次数: " .. quizState.correctAnswersInRow)
        else
            TerminalPrint("✅ CORRECT! +" .. xpToGive .. " experience!")
            TerminalPrint("🔥 Correct streak: " .. quizState.correctAnswersInRow)
        end
    else
        if currentLang == "ru" then
            TerminalPrint("✅ ПРАВИЛЬНО!")
            TerminalPrint("🔥 Серия правильных ответов: " .. quizState.correctAnswersInRow)
        elseif currentLang == "cn" then
            TerminalPrint("✅ 正确！")
            TerminalPrint("🔥 连续正确次数: " .. quizState.correctAnswersInRow)
        else
            TerminalPrint("✅ CORRECT!")
            TerminalPrint("🔥 Correct streak: " .. quizState.correctAnswersInRow)
        end
    end
else
    -- СБРАСЫВАЕМ СЧЕТ и счетчик правильных ответов подряд при неправильном ответе
    quizState.score = 0
    quizState.correctAnswersInRow = 0
    if currentLang == "ru" then
        TerminalPrint("❌ НЕПРАВИЛЬНО! Правильный ответ:")
        TerminalPrint(currentQuestion.correct_ru)
        TerminalPrint("💔 Серия правильных ответов сброшена!")
    elseif currentLang == "cn" then
        TerminalPrint("❌ 错误！正确答案是：")
        TerminalPrint(currentQuestion.correct_cn)
        TerminalPrint("💔 连续正确次数已重置！")
    else
        TerminalPrint("❌ WRONG! The correct answer is:")
        TerminalPrint(currentQuestion.correct_en)
        TerminalPrint("💔 Correct streak reset!")
    end
end
	
	-- Ask if player wants to continue
	TerminalPrint("===========================")
	if currentLang == "ru" then
		TerminalPrint("Продолжить викторину? (да/нет)")
	elseif currentLang == "cn" then
		TerminalPrint("继续测验？(是/否)")
	else
		TerminalPrint("Continue quiz? (yes/no)")
	end
	
	-- Set up state for continuation
	quizState.waitingForContinuation = true
	quizState.lastAnswerCorrect = correct
	
	return true
end

function TerminalClass:startNewQuizQuestion(terminalId, currentLang, terminalState)
	local quizState = terminalState.quiz
	
	-- Questions array
	local questions = {
		{
			ru = "Кто является идеологическим оппонентом Европейской Коалиции?",
			en = "Who is the ideological opponent of the European Coalition?",
			cn = "谁是欧洲联盟的意识形态对手？",
			answers_ru = {"юпитерианские сепаратисты", "сепаратисты", "юпитерианские сепаратисты", "юпитерианцы"},
			answers_en = {"jovian separatists", "separatists", "jovian separatists", "jovians"},
			answers_cn = {"木星分离主义者", "分离主义者", "木星分离主义者", "木星人"},
			correct_ru = "Юпитерианские Сепаратисты",
			correct_en = "Jovian Separatists",
			correct_cn = "木星分离主义者"
		},
		{
			ru = "Как называется тяжёлое орудие, использующее силу электромагнитов для запуска снарядов на огромной скорости?",
			en = "What is the name of the heavy weapon that uses electromagnetic force to launch projectiles at tremendous speed?",
			cn = "使用电磁力以极高速度发射炮弹的重型武器叫什么？",
			answers_ru = {"рельсотрон", "railgun"},
			answers_en = {"railgun"},
			answers_cn = {"电磁轨道炮"},
			correct_ru = "Рельсотрон",
			correct_en = "Railgun",
			correct_cn = "电磁轨道炮"
		},
		{
			ru = "Название многоствольного стационарного орудия, работающего на принципах магнитного орудия, покрывающего врагов шквалом огня?",
			en = "Name of the multi-barrel stationary weapon operating on magnetic principles, covering enemies with a hail of fire?",
			cn = "基于磁力原理运作的多管固定武器，用弹雨覆盖敌人的武器叫什么？",
			answers_ru = {"цепная пушка", "chaingun"},
			answers_en = {"chaingun"},
			answers_cn = {"链式炮"},
			correct_ru = "Цепная пушка",
			correct_en = "Chaingun",
			correct_cn = "链式炮"
		},
		{
			ru = "Какое стационарное орудие, встречающееся на подлодках, использует шрапнель в выпускаемых снарядах?",
			en = "Which stationary weapon found on submarines uses shrapnel in its projectiles?",
			cn = "在潜艇上发现的哪种固定武器在其射弹中使用霰弹？",
			answers_ru = {"шрапнельное орудие", "шрапнельная пушка", "shrapnel cannon", "shrapnel gun"},
			answers_en = {"shrapnel cannon", "shrapnel gun"},
			answers_cn = {"霰弹炮", "霰弹枪"},
			correct_ru = "Шрапнельное орудие",
			correct_en = "Shrapnel Cannon",
			correct_cn = "霰弹炮"
		},
		{
			ru = "Какой материал при производстве боеприпасов для импульсного лазера является ключевым в достижении химической реакции, что позволяет орудию испускать лазер во врагов?",
			en = "Which material in pulse laser ammunition production is key to achieving the chemical reaction that allows the weapon to emit lasers at enemies?",
			cn = "在脉冲激光弹药生产中，哪种材料是实现化学反应的关键，使武器能够向敌人发射激光？",
			answers_ru = {"инопланетная кровь", "alien blood"},
			answers_en = {"alien blood"},
			answers_cn = {"外星血液"},
			correct_ru = "Инопланетная кровь",
			correct_en = "Alien Blood",
			correct_cn = "外星血液"
		},
		{
			ru = "Какой инопланетный минерал по праву считается одним из самых редких в глубинах Европы?",
			en = "Which alien mineral is rightly considered one of the rarest in the depths of Europa?",
			cn = "哪种外星矿物被公认为木卫二深处最稀有的矿物之一？",
			answers_ru = {"грозовий", "дементонит", "воспламенит", "кислородит", "сернит", "физикорий"},
			answers_en = {"stormite", "dementonite", "igniterite", "oxygenite", "sulphurite", "physicorium"},
			answers_cn = {"风暴石", "痴呆石", "点燃石", "氧气石", "硫石", "物理石"},
			correct_ru = "Грозовий",
			correct_en = "Stormite",
			correct_cn = "风暴石"
		},
		{
			ru = "Как называется редкий инопланетный материал, что хранит в себе огромный потенциал энергии?",
			en = "What is the name of the rare alien material that stores enormous energy potential?",
			cn = "储存巨大能量潜力的稀有外星材料叫什么？",
			answers_ru = {"грозовий", "stormite"},
			answers_en = {"stormite"},
			answers_cn = {"风暴石"},
			correct_ru = "Грозовий",
			correct_en = "Stormite",
			correct_cn = "风暴石"
		},
		{
			ru = "Название самого обсуждаемого инопланетного материала, на заглавных страничках научпопа, который человеческий рассудок ещё не научился воспринимать из-за многомерности?",
			en = "Name of the most discussed alien material, featured in science headlines, which human mind cannot yet perceive due to its multidimensionality?",
			cn = "科学头条中最受讨论的外星材料名称，由于多维性，人类思维尚无法感知？",
			answers_ru = {"дементонит", "dementonite"},
			answers_en = {"dementonite"},
			answers_cn = {"痴呆石"},
			correct_ru = "Дементонит",
			correct_en = "Dementonite",
			correct_cn = "痴呆石"
		},
		{
			ru = "Чем можно помочь человеку, на слизистую оболочку которого попала эссенция Бича Раптора?",
			en = "How can you help a person who got Raptor Bile essence on their mucous membrane?",
			cn = "如何帮助粘膜沾上猛禽胆汁精华的人？",
			answers_ru = {"водой", "вода", "water"},
			answers_en = {"water"},
			answers_cn = {"水"},
			correct_ru = "Водой",
			correct_en = "Water",
			correct_cn = "水"
		},
		{
			ru = "Какая печально известная фракция Европы характеризует себя с шутами?",
			en = "Which infamous Europa faction characterizes itself with jesters?",
			cn = "哪个臭名昭著的木卫二派系以小丑为特征？",
			answers_ru = {"клоуны", "красный нос", "нефоры"},
			answers_en = {"clowns", "red nose", "nefors"},
			answers_cn = {"小丑", "红鼻子", "非主流"},
			correct_ru = "Клоуны",
			correct_en = "Clowns",
			correct_cn = "小丑"
		},
		{
			ru = "Популярное название тяжёлой модификации стандартного боевого костюма",
			en = "Popular name for the heavy modification of the standard combat suit",
			cn = "标准战斗服重型改装的流行名称",
			answers_ru = {"экзокостюм", "exosuit"},
			answers_en = {"exosuit"},
			answers_cn = {"外骨骼服"},
			correct_ru = "Экзокостюм",
			correct_en = "Exosuit",
			correct_cn = "外骨骼服"
		},
		{
			ru = "Аналог уранового топлива для реактора, что служит дольше?",
			en = "Alternative to uranium fuel for the reactor that lasts longer?",
			cn = "反应堆铀燃料的替代品，使用寿命更长？",
			answers_ru = {"торий", "ториевый стержень"},
			answers_en = {"thorium", "thorium rod"},
			answers_cn = {"钍", "钍棒"},
			correct_ru = "Торий",
			correct_en = "Thorium",
			correct_cn = "钍"
		},
		{
			ru = "Как называется минерал, содержащий железо и алюминий?",
			en = "What is the name of the mineral containing iron and aluminum?",
			cn = "含有铁和铝的矿物叫什么？",
			answers_ru = {"бавалин", "bavalite"},
			answers_en = {"bavalite"},
			answers_cn = {"巴瓦矿"},
			correct_ru = "Бавалин",
			correct_en = "Bavalite",
			correct_cn = "巴瓦矿"
		},
		{
			ru = "Как называется минерал, содержащий медь?",
			en = "What is the name of the mineral containing copper?",
			cn = "含有铜的矿物叫什么？",
			answers_ru = {"борнит", "халькопирит"},
			answers_en = {"bornite", "chalcopyrite"},
			answers_cn = {"斑铜矿", "黄铜矿"},
			correct_ru = "Борнит",
			correct_en = "Bornite",
			correct_cn = "斑铜矿"
		},
		{
			ru = "Как называется минерал, содержащий торий и фосфор?",
			en = "What is the name of the mineral containing thorium and phosphorus?",
			cn = "含有钍和磷的矿物叫什么？",
			answers_ru = {"брокит", "brockite"},
			answers_en = {"brockite"},
			answers_cn = {"布罗克矿"},
			correct_ru = "Брокит",
			correct_en = "Brockite",
			correct_cn = "布罗克矿"
		},
		{
			ru = "Как называется минерал, содержащий свинец?",
			en = "What is the name of the mineral containing lead?",
			cn = "含有铅的矿物叫什么？",
			answers_ru = {"галена", "galena"},
			answers_en = {"galena"},
			answers_cn = {"方铅矿"},
			correct_ru = "Галена",
			correct_en = "Galena",
			correct_cn = "方铅矿"
		},
		{
			ru = "Как называется органическое растение, содержащее опиоиды?",
			en = "What is the name of the organic plant containing opioids?",
			cn = "含有阿片类药物的有机植物叫什么？",
			answers_ru = {"водяной мак", "мак"},
			answers_en = {"water poppy", "poppy"},
			answers_cn = {"水罂粟", "罂粟"},
			correct_ru = "Водяной мак",
			correct_en = "Water Poppy",
			correct_cn = "水罂粟"
		},
		{
			ru = "Как называется органическое растение, содержащее спирт, или же этанол?",
			en = "What is the name of the organic plant containing alcohol, or ethanol?",
			cn = "含有酒精或乙醇的有机植物叫什么？",
			answers_ru = {"морские дрожжи", "дрожжи"},
			answers_en = {"sea yeast", "yeast"},
			answers_cn = {"海洋酵母", "酵母"},
			correct_ru = "Морские дрожжи",
			correct_en = "Sea Yeast",
			correct_cn = "海洋酵母"
		},
		{
			ru = "Как называется органическое растение, содержащее эластин?",
			en = "What is the name of the organic plant containing elastin?",
			cn = "含有弹性蛋白的有机植物叫什么？",
			answers_ru = {"эластиновое растение", "elastin plant"},
			answers_en = {"elastin plant"},
			answers_cn = {"弹性蛋白植物"},
			correct_ru = "Эластиновое растение",
			correct_en = "Elastin Plant",
			correct_cn = "弹性蛋白植物"
		},
		{
			ru = "Как называется органическое растение, содержащее органическое волокно?",
			en = "What is the name of the organic plant containing organic fiber?",
			cn = "含有有机纤维的有机植物叫什么？",
			answers_ru = {"прядильное растение", "spinning plant"},
			answers_en = {"spinning plant"},
			answers_cn = {"纺织植物"},
			correct_ru = "Прядильное растение",
			correct_en = "Spinning Plant",
			correct_cn = "纺织植物"
		},
		{
			ru = "Как называется органическое растение, содержащее антибиотики широкого спектра?",
			en = "What is the name of the organic plant containing broad-spectrum antibiotics?",
			cn = "含有广谱抗生素的有机植物叫什么？",
			answers_ru = {"слизистые бактерии", "mucous bacteria"},
			answers_en = {"mucous bacteria"},
			answers_cn = {"黏液细菌"},
			correct_ru = "Слизистые бактерии",
			correct_en = "Mucous Bacteria",
			correct_cn = "黏液细菌"
		},
		{
			ru = "Как называется минерал, содержащий твёрдые соединения углеродов?",
			en = "What is the name of the mineral containing hard carbon compounds?",
			cn = "含有硬碳化合物的矿物叫什么？",
			answers_ru = {"алмаз", "diamond"},
			answers_en = {"diamond"},
			answers_cn = {"钻石"},
			correct_ru = "Алмаз",
			correct_en = "Diamond",
			correct_cn = "钻石"
		},
		{
			ru = "Как называется минерал, содержащий литий, алюминий и натрий?",
			en = "What is the name of the mineral containing lithium, aluminum and sodium?",
			cn = "含有锂、铝和钠的矿物叫什么？",
			answers_ru = {"амблигонит", "amblygonite"},
			answers_en = {"amblygonite"},
			answers_cn = {"磷铝锂石"},
			correct_ru = "Амблигонит",
			correct_en = "Amblygonite",
			correct_cn = "磷铝锂石"
		},
		{
			ru = "Как называется минерал, содержащий кальций?",
			en = "What is the name of the mineral containing calcium?",
			cn = "含有钙的矿物叫什么？",
			answers_ru = {"арагонит", "aragonite"},
			answers_en = {"aragonite"},
			answers_cn = {"文石"},
			correct_ru = "Арагонит",
			correct_en = "Aragonite",
			correct_cn = "文石"
		},
		{
			ru = "Как называется минерал, содержащий кальций и фосфор?",
			en = "What is the name of the mineral containing calcium and phosphorus?",
			cn = "含有钙和磷的矿物叫什么？",
			answers_ru = {"гидроксиапатит", "hydroxyapatite"},
			answers_en = {"hydroxyapatite"},
			answers_cn = {"羟基磷灰石"},
			correct_ru = "Гидроксиапатит",
			correct_en = "Hydroxyapatite",
			correct_cn = "羟基磷灰石"
		},
		{
			ru = "Как называется минерал, содержащий кристаллические углероды?",
			en = "What is the name of the mineral containing crystalline carbons?",
			cn = "含有结晶碳的矿物叫什么？",
			answers_ru = {"графит", "graphite"},
			answers_en = {"graphite"},
			answers_cn = {"石墨"},
			correct_ru = "Графит",
			correct_en = "Graphite",
			correct_cn = "石墨"
		},
		{
			ru = "Как называется минерал, содержащий титан?",
			en = "What is the name of the mineral containing titanium?",
			cn = "含有钛的矿物叫什么？",
			answers_ru = {"ильменит", "ilmenite"},
			answers_en = {"ilmenite"},
			answers_cn = {"钛铁矿"},
			correct_ru = "Ильменит",
			correct_en = "Ilmenite",
			correct_cn = "钛铁矿"
		},
		{
			ru = "Как называется минерал, содержащий олово?",
			en = "What is the name of the mineral containing tin?",
			cn = "含有锡的矿物叫什么？",
			answers_ru = {"касситерит", "cassiterite"},
			answers_en = {"cassiterite"},
			answers_cn = {"锡石"},
			correct_ru = "Касситерит",
			correct_en = "Cassiterite",
			correct_cn = "锡石"
		},
		{
			ru = "Как называется кристаллический минерал, содержащий кремний?",
			en = "What is the name of the crystalline mineral containing silicon?",
			cn = "含有硅的结晶矿物叫什么？",
			answers_ru = {"кварц", "quartz"},
			answers_en = {"quartz"},
			answers_cn = {"石英"},
			correct_ru = "Кварц",
			correct_en = "Quartz",
			correct_cn = "石英"
		},
		{
			ru = "Как называется кристаллический минерал, содержащий натрий?",
			en = "What is the name of the crystalline mineral containing sodium?",
			cn = "含有钠的结晶矿物叫什么？",
			answers_ru = {"криолит", "cryolite"},
			answers_en = {"cryolite"},
			answers_cn = {"冰晶石"},
			correct_ru = "Криолит",
			correct_en = "Cryolite",
			correct_cn = "冰晶石"
		},
		{
			ru = "Как называется минерал, содержащий кремний?",
			en = "What is the name of the mineral containing silicon?",
			cn = "含有硅的矿物叫什么？",
			answers_ru = {"хризопраз", "chrysoprase"},
			answers_en = {"chrysoprase"},
			answers_cn = {"绿玉髓"},
			correct_ru = "Хризопраз",
			correct_en = "Chrysoprase",
			correct_cn = "绿玉髓"
		},
		{
			ru = "Как называется руда, содержащая уран?",
			en = "What is the name of the ore containing uranium?",
			cn = "含有铀的矿石叫什么？",
			answers_ru = {"урановая руда", "uranium ore"},
			answers_en = {"uranium ore"},
			answers_cn = {"铀矿石"},
			correct_ru = "Урановая руда",
			correct_en = "Uranium Ore",
			correct_cn = "铀矿石"
		},
		{
			ru = "Как называется минерал, содержащий медь, железо и олово?",
			en = "What is the name of the mineral containing copper, iron and tin?",
			cn = "含有铜、铁和锡的矿物叫什么？",
			answers_ru = {"станнит", "stannite"},
			answers_en = {"stannite"},
			answers_cn = {"黄锡矿"},
			correct_ru = "Станнит",
			correct_en = "Stannite",
			correct_cn = "黄锡矿"
		},
		{
			ru = "Как называется минерал, содержащий литий?",
			en = "What is the name of the mineral containing lithium?",
			cn = "含有锂的矿物叫什么？",
			answers_ru = {"трифилин", "triphylite"},
			answers_en = {"triphylite"},
			answers_cn = {"锂磷铁石"},
			correct_ru = "Трифилин",
			correct_en = "Triphylite",
			correct_cn = "锂磷铁石"
		},
		{
			ru = "Как называется минерал, содержащий титан и железо?",
			en = "What is the name of the mineral containing titanium and iron?",
			cn = "含有钛和铁的矿物叫什么？",
			answers_ru = {"титанит", "titanite"},
			answers_en = {"titanite"},
			answers_cn = {"榍石"},
			correct_ru = "Титанит",
			correct_en = "Titanite",
			correct_cn = "榍石"
		},
		{
			ru = "Как называется минерал, содержащий преимущественно кристаллический цинк?",
			en = "What is the name of the mineral containing predominantly crystalline zinc?",
			cn = "主要含有结晶锌的矿物叫什么？",
			answers_ru = {"сфалерит", "sphalerite"},
			answers_en = {"sphalerite"},
			answers_cn = {"闪锌矿"},
			correct_ru = "Сфалерит",
			correct_en = "Sphalerite",
			correct_cn = "闪锌矿"
		},
		{
			ru = "Название нестабильного кристалла, что вырабатывает огромные, взрывообразные скопления энергии в коридорах локации Холодных пещер, представляя ЭМИ угрозу проплывающим судам?",
			en = "Name of the unstable crystal that generates huge, explosive energy accumulations in the Cold Caverns location corridors, posing an EMP threat to passing vessels?",
			cn = "在冷洞穴位置走廊中产生巨大爆炸性能量积聚，对经过船只构成电磁脉冲威胁的不稳定晶体名称？",
			answers_ru = {"пьезокристалл", "piezocrystal"},
			answers_en = {"piezocrystal"},
			answers_cn = {"压电晶体"},
			correct_ru = "Пьезокристалл",
			correct_en = "Piezocrystal",
			correct_cn = "压电晶体"
		},
		{
			ru = "Научное название 'трупного паразита'?",
			en = "Scientific name of the 'corpse parasite'?",
			cn = "'尸体寄生虫'的科学名称？",
			answers_ru = {"велонацепс каликс", "яйца велонацепса каликса"},
			answers_en = {"velonaceps calyx", "velonaceps calyx eggs"},
			answers_cn = {"杯状绒线虫", "杯状绒线虫卵"},
			correct_ru = "Велонацепс Каликс",
			correct_en = "Velonaceps Calyx",
			correct_cn = "杯状绒线虫"
		},
		{
			ru = "Название странной субстанции, вызывающей эффект онемения при непосредственном контакте с телом?",
			en = "Name of the strange substance that causes numbness effect upon direct contact with the body?",
			cn = "直接接触身体会引起麻木效果的奇怪物质名称？",
			answers_ru = {"параликс", "paralyxis"},
			answers_en = {"paralyxis"},
			answers_cn = {"麻痹物质"},
			correct_ru = "Параликс",
			correct_en = "Paralyxis",
			correct_cn = "麻痹物质"
		},
		{
			ru = "Какое вещество вызывает алкогольное опьянение при попадании в кровь организма?",
			en = "Which substance causes alcohol intoxication when entering the bloodstream?",
			cn = "哪种物质进入血液后会引起酒精中毒？",
			answers_ru = {"этанол", "брага"},
			answers_en = {"ethanol", "brew"},
			answers_cn = {"乙醇", "酿造液"},
			correct_ru = "Этанол",
			correct_en = "Ethanol",
			correct_cn = "乙醇"
		},
		{
			ru = "Как остановить финальную стадию превращения у заражённого трупным паразитом?",
			en = "How to stop the final stage of transformation in someone infected with the corpse parasite?",
			cn = "如何阻止感染尸体寄生虫的人的最后转变阶段？",
			answers_ru = {"страданит", "sufferite"},
			answers_en = {"sufferite"},
			answers_cn = {"痛苦石"},
			correct_ru = "Страданит",
			correct_en = "Sufferite",
			correct_cn = "痛苦石"
		},
		{
			ru = "Что помогает остановить развившийся процесс заражения велонацепса каликса?",
			en = "What helps stop the developed infection process of Velonaceps Calyx?",
			cn = "什么有助于阻止杯状绒线虫感染的发展过程？",
			answers_ru = {"каликсанид", "calyxanide"},
			answers_en = {"calyxanide"},
			answers_cn = {"杯状虫抑制剂"},
			correct_ru = "Каликсанид",
			correct_en = "Calyxanide",
			correct_cn = "杯状虫抑制剂"
		},
		{
			ru = "Название быстрогорящего порошка, что используется в пиротехнике и сигнальных ракетах",
			en = "Name of the fast-burning powder used in pyrotechnics and signal flares",
			cn = "用于烟火和信号弹的快速燃烧粉末名称",
			answers_ru = {"светящийся порошок", "glowing powder"},
			answers_en = {"glowing powder"},
			answers_cn = {"发光粉末"},
			correct_ru = "Светящийся порошок",
			correct_en = "Glowing Powder",
			correct_cn = "发光粉末"
		},
		{
			ru = "Как называется минерал, содержащий калий и кальций?",
			en = "What is the name of the mineral containing potassium and calcium?",
			cn = "含有钾和钙的矿物叫什么？",
			answers_ru = {"полигалит", "polyhalite"},
			answers_en = {"polyhalite"},
			answers_cn = {"杂卤石"},
			correct_ru = "Полигалит",
			correct_en = "Polyhalite",
			correct_cn = "杂卤石"
		},
		{
			ru = "Из чего синтезируется пластик?",
			en = "What is plastic synthesized from?",
			cn = "塑料是由什么合成的？",
			answers_ru = {"кремний и углерод", "silicon and carbon"},
			answers_en = {"silicon and carbon"},
			answers_cn = {"硅和碳"},
			correct_ru = "Кремний и углерод",
			correct_en = "Silicon and Carbon",
			correct_cn = "硅和碳"
		},
		{
			ru = "Как называется минерал, содержащий хлорин?",
			en = "What is the name of the mineral containing chlorine?",
			cn = "含有氯的矿物叫什么？",
			answers_ru = {"пироморфит", "pyromorphite"},
			answers_en = {"pyromorphite"},
			answers_cn = {"磷氯铅矿"},
			correct_ru = "Пироморфит",
			correct_en = "Pyromorphite",
			correct_cn = "磷氯铅矿"
		},
		{
			ru = "Название сильной кислоты, вызывающей ожоги при попадании на кожу и употреблении внутрь?",
			en = "Name of the strong acid that causes burns when contacting skin or ingested?",
			cn = "接触皮肤或摄入时会引起烧伤的强酸名称？",
			answers_ru = {"серная кислота", "sulfuric acid"},
			answers_en = {"sulfuric acid"},
			answers_cn = {"硫酸"},
			correct_ru = "Серная кислота",
			correct_en = "Sulfuric Acid",
			correct_cn = "硫酸"
		},
		{
			ru = "Название крайне неустойчивой жидкости, или же нестабильной, что при сильной тряске, нагревании, либо ударе может взорваться?",
			en = "Name of the extremely unstable liquid that can explode when shaken strongly, heated, or impacted?",
			cn = "极度不稳定液体的名称，在强烈摇晃、加热或撞击时可能爆炸？",
			answers_ru = {"нитроглицерин", "nitroglycerin"},
			answers_en = {"nitroglycerin"},
			answers_cn = {"硝化甘油"},
			correct_ru = "Нитроглицерин",
			correct_en = "Nitroglycerin",
			correct_cn = "硝化甘油"
		},
		{
			ru = "Как называется минерал, содержащий фосфор и железо?",
			en = "What is the name of the mineral containing phosphorus and iron?",
			cn = "含有磷和铁的矿物叫什么？",
			answers_ru = {"лазулит", "lazulite"},
			answers_en = {"lazulite"},
			answers_cn = {"天蓝石"},
			correct_ru = "Лазулит",
			correct_en = "Lazulite",
			correct_cn = "天蓝石"
		},
		{
			ru = "Как называется минерал, содержащий калий и магний?",
			en = "What is the name of the mineral containing potassium and magnesium?",
			cn = "含有钾和镁的矿物叫什么？",
			answers_ru = {"лангбейнит", "langbeinite"},
			answers_en = {"langbeinite"},
			answers_cn = {"无水钾镁矾"},
			correct_ru = "Лангбейнит",
			correct_en = "Langbeinite",
			correct_cn = "无水钾镁矾"
		},
		{
			ru = "Вы поддерживаете власть Европейской Коалиции и её де-факто утвержденную структуру по всей Европе?",
			en = "Do you support the power of the European Coalition and its de facto approved structure across Europa?",
			cn = "您是否支持欧洲联盟的权力及其在木卫二事实上认可的结构？",
			answers_ru = {"да", "слава коалиции"},
			answers_en = {"yes", "glory to coalition"},
			answers_cn = {"是", "联盟万岁"},
			correct_ru = "Да",
			correct_en = "Yes",
			correct_cn = "是"
		},
		{
			ru = "Название прочного волокна, извлечённого из пластика и титана?",
			en = "Name of the durable fiber extracted from plastic and titanium?",
			cn = "从塑料和钛中提取的耐用纤维名称？",
			answers_ru = {"противоударное волокно", "кевлар", "кевларовое волокно"},
			answers_en = {"impact-resistant fiber", "kevlar", "kevlar fiber"},
			answers_cn = {"防冲击纤维", "凯夫拉", "凯夫拉纤维"},
			correct_ru = "Противоударное волокно",
			correct_en = "Impact-resistant Fiber",
			correct_cn = "防冲击纤维"
		},
		{
			ru = "Какой монстр встречается чаще всего на Европе?",
			en = "Which monster is most commonly encountered on Europa?",
			cn = "木卫二上最常见的怪物是什么？",
			answers_ru = {"ползун", "crawler"},
			answers_en = {"crawler"},
			answers_cn = {"爬行者"},
			correct_ru = "Ползун",
			correct_en = "Crawler",
			correct_cn = "爬行者"
		},
		{
			ru = "У какого единственного существа есть на теле полосы?",
			en = "Which unique creature has stripes on its body?",
			cn = "哪种独特的生物身上有条纹？",
			answers_ru = {"тигровая акула", "tiger shark"},
			answers_en = {"tiger shark"},
			answers_cn = {"虎鲨"},
			correct_ru = "Тигровая акула",
			correct_en = "Tiger Shark",
			correct_cn = "虎鲨"
		},
		{
			ru = "Какая фракция самая малочисленная на Европе?",
			en = "Which faction is the smallest on Europa?",
			cn = "木卫二上哪个派系人数最少？",
			answers_ru = {"дети красного носа", "клоуны"},
			answers_en = {"red nose children", "clowns"},
			answers_cn = {"红鼻子孩子", "小丑"},
			correct_ru = "Дети красного носа",
			correct_en = "Red Nose Children",
			correct_cn = "红鼻子孩子"
		},
		{
			ru = "Какое существо вызывает визуальные и слуховые галлюцинации?",
			en = "Which creature causes visual and auditory hallucinations?",
			cn = "哪种生物会引起视觉和听觉幻觉？",
			answers_ru = {"смотритель", "watcher"},
			answers_en = {"watcher"},
			answers_cn = {"看守者"},
			correct_ru = "Смотритель",
			correct_en = "Watcher",
			correct_cn = "看守者"
		},
		{
			ru = "Из какого химического элемента преимущественно состоит панцирь существа Червь рока?",
			en = "What chemical element primarily makes up the shell of the Rockworm creature?",
			cn = "岩虫外壳主要由什么化学元素组成？",
			answers_ru = {"физикорий", "physicorium"},
			answers_en = {"physicorium"},
			answers_cn = {"物理石"},
			correct_ru = "Физикорий",
			correct_en = "Physicorium",
			correct_cn = "物理石"
		},
		{
			ru = "Какой организм отвечает за защиту организма Таламуса, являясь аналогом белых кровяных телец у земных организмов?",
			en = "Which organism is responsible for protecting the Thalamus organism, being an analogue of white blood cells in terrestrial organisms?",
			cn = "哪种生物负责保护塔拉姆斯生物体，相当于地球生物中的白细胞？",
			answers_ru = {"лейкоцит", "leukocyte"},
			answers_en = {"leukocyte"},
			answers_cn = {"白细胞"},
			correct_ru = "Лейкоцит",
			correct_en = "Leukocyte",
			correct_cn = "白细胞"
		},
		{
			ru = "В какой организм вырастает балластная флора в случае если оставить её развиваться на несколько лет?",
			en = "Which organism does the Ballast Flora grow into if left to develop for several years?",
			cn = "如果让压载植物发育几年，它会长成什么生物？",
			answers_ru = {"таламус", "thalamus"},
			answers_en = {"thalamus"},
			answers_cn = {"塔拉姆斯"},
			correct_ru = "Таламус",
			correct_en = "Thalamus",
			correct_cn = "塔拉姆斯"
		},
		{
			ru = "Какое чувство восприятия окружающего мира отсутствует у Молоха?",
			en = "Which sense of perception of the surrounding world is absent in the Moloch?",
			cn = "摩洛克缺少哪种感知周围世界的感觉？",
			answers_ru = {"зрение", "vision"},
			answers_en = {"vision"},
			answers_cn = {"视觉"},
			correct_ru = "Зрение",
			correct_en = "Vision",
			correct_cn = "视觉"
		},
		{
			ru = "Если известно, что на глубине ниже 4000 можно встретить существо Харибда, какое действие следует предпринять, чтобы избежать с ней столкновения?",
			en = "If it is known that the creature Charybdis can be encountered at depths below 4000, what action should be taken to avoid collision with it?",
			cn = "如果已知在4000以下深度可能遇到卡律布狄斯生物，应采取什么行动避免与之碰撞？",
			answers_ru = {"отключить сонар", "turn off sonar"},
			answers_en = {"turn off sonar"},
			answers_cn = {"关闭声纳"},
			correct_ru = "Отключить сонар",
			correct_en = "Turn off sonar",
			correct_cn = "关闭声纳"
		},
		{
			ru = "Какую тактику при нападение применяют существа семейства Молотоглавы (2 слова)?",
			en = "What tactic do creatures of the Hammerhead family use when attacking (2 words)?",
			cn = "锤头家族生物在攻击时采用什么战术（2个词）？",
			answers_ru = {"бей беги", "hit and run"},
			answers_en = {"hit and run"},
			answers_cn = {"打了就跑"},
			correct_ru = "Бей беги",
			correct_en = "Hit and run",
			correct_cn = "打了就跑"
		},
		{
			ru = "Для чего зараженное велонацепсом каликса существо в остаточном здравом рассудке может заразить другое существо?",
			en = "Why might a creature infected with Velonaceps Calyx, in its residual sanity, infect another creature?",
			cn = "为什么被杯状绒线虫感染的生物在残留理智状态下会感染其他生物？",
			answers_ru = {"успокоить паразит", "calm the parasite"},
			answers_en = {"calm the parasite"},
			answers_cn = {"安抚寄生虫"},
			correct_ru = "Успокоить паразит",
			correct_en = "Calm the parasite",
			correct_cn = "安抚寄生虫"
		},
		{
			ru = "Какая финальная стадия развития паразита велонацепса каликса в человеке известна на данный момент?",
			en = "What is the final known stage of development of the Velonaceps Calyx parasite in humans?",
			cn = "目前已知的杯状绒线虫寄生虫在人体内的最终发展阶段是什么？",
			answers_ru = {"химера", "chimera"},
			answers_en = {"chimera"},
			answers_cn = {"嵌合体"},
			correct_ru = "Химера",
			correct_en = "Chimera",
			correct_cn = "嵌合体"
		},
		{
			ru = "Какая часть тела Чёрного молоха имеет легкое электромагнитное воздействие?",
			en = "Which part of the Black Moloch's body has a slight electromagnetic effect?",
			cn = "黑色摩洛克身体的哪个部位具有轻微的电磁效应？",
			answers_ru = {"щупальца", "tentacles"},
			answers_en = {"tentacles"},
			answers_cn = {"触手"},
			correct_ru = "Щупальца",
			correct_en = "Tentacles",
			correct_cn = "触手"
		},
		{
			ru = "Одно из самых обсуждаемых существ, величина и сила которого превышает размеры станций?",
			en = "One of the most discussed creatures, whose size and strength exceed the dimensions of stations?",
			cn = "最受讨论的生物之一，其体型和力量超过空间站的尺寸？",
			answers_ru = {"червь рока", "rockworm"},
			answers_en = {"rockworm"},
			answers_cn = {"岩虫"},
			correct_ru = "Червь рока",
			correct_en = "Rockworm",
			correct_cn = "岩虫"
		},
		{
			ru = "У какого инопланетного организма на Европе один из самых крупных панцирей, а их уязвимая оболочка желеобразна, как у медуз?",
			en = "Which alien organism on Europa has one of the largest shells, and its vulnerable membrane is jelly-like, like that of jellyfish?",
			cn = "木卫二上哪种外星生物拥有最大的外壳之一，其脆弱膜层像水母一样呈胶状？",
			answers_ru = {"молох", "moloch"},
			answers_en = {"moloch"},
			answers_cn = {"摩洛克"},
			correct_ru = "Молох",
			correct_en = "Moloch",
			correct_cn = "摩洛克"
		},
		{
			ru = "Малоизученный механизм, симулирующий подобие жизни, задача которого за всеми наблюдать?",
			en = "A little-studied mechanism that simulates a semblance of life, whose task is to observe everyone?",
			cn = "一个研究甚少的机制，模拟生命的表象，其任务是观察所有人？",
			answers_ru = {"наблюдатель", "смотритель", "watcher"},
			answers_en = {"watcher"},
			answers_cn = {"观察者"},
			correct_ru = "Наблюдатель",
			correct_en = "Watcher",
			correct_cn = "观察者"
		},
		{
			ru = "Самое могущественное существо на спутнике Юпитера Европа?",
			en = "The most powerful creature on Jupiter's moon Europa?",
			cn = "木卫二上最强大的生物是什么？",
			answers_ru = {"псиложаба", "psyjelly"},
			answers_en = {"psyjelly"},
			answers_cn = {"心灵水母"},
			correct_ru = "Псиложаба",
			correct_en = "Psyjelly",
			correct_cn = "心灵水母"
		},
		{
			ru = "Какое самое глупое существо на Европе?",
			en = "What is the stupidest creature on Europa?",
			cn = "木卫二上最愚蠢的生物是什么？",
			answers_ru = {"человек", "human"},
			answers_en = {"human"},
			answers_cn = {"人类"},
			correct_ru = "Человек",
			correct_en = "Human",
			correct_cn = "人类"
		},
		{
			ru = "Какой медицинский термин описывает патологически низкое кровяное давление?",
			en = "What medical term describes pathologically low blood pressure?",
			cn = "哪个医学术语描述病理性低血压？",
			answers_ru = {"гипотония", "hypotension"},
			answers_en = {"hypotension"},
			answers_cn = {"低血压"},
			correct_ru = "Гипотония",
			correct_en = "Hypotension",
			correct_cn = "低血压"
		},
		{
			ru = "Какой медицинский термин описывает патологически высокое кровяное давление?",
			en = "What medical term describes pathologically high blood pressure?",
			cn = "哪个医学术语描述病理性高血压？",
			answers_ru = {"гипертония", "hypertension"},
			answers_en = {"hypertension"},
			answers_cn = {"高血压"},
			correct_ru = "Гипертония",
			correct_en = "Hypertension",
			correct_cn = "高血压"
		},
		{
			ru = "Какой симптом появляется вследствие повреждения печени или почек?",
			en = "What symptom appears as a result of liver or kidney damage?",
			cn = "肝脏或肾脏受损后会出现什么症状？",
			answers_ru = {"желтуха", "желтушная болезнь", "jaundice"},
			answers_en = {"jaundice"},
			answers_cn = {"黄疸"},
			correct_ru = "Желтуха",
			correct_en = "Jaundice",
			correct_cn = "黄疸"
		},
		{
			ru = "Какая группа крови является универсальной и подходит другим группам крови?",
			en = "Which blood type is universal and suitable for other blood types?",
			cn = "哪种血型是通用的并适合其他血型？",
			answers_ru = {"0-", "о-", "o negative"},
			answers_en = {"o negative"},
			answers_cn = {"o型阴性"},
			correct_ru = "0-",
			correct_en = "O negative",
			correct_cn = "O型阴性"
		},
		{
			ru = "Какой предмет по праву считается эталонным в представлении хирургии?",
			en = "Which item is rightly considered the benchmark in the representation of surgery?",
			cn = "哪个物品在手术领域被公认为标杆？",
			answers_ru = {"скальпель", "scalpel"},
			answers_en = {"scalpel"},
			answers_cn = {"手术刀"},
			correct_ru = "Скальпель",
			correct_en = "Scalpel",
			correct_cn = "手术刀"
		},
		{
			ru = "Какой синдром появляется вследствие инородного тела или же воспаления?",
			en = "What syndrome appears as a result of a foreign body or inflammation?",
			cn = "由于异物或炎症会出现什么综合征？",
			answers_ru = {"сепсис", "sepsis"},
			answers_en = {"sepsis"},
			answers_cn = {"败血症"},
			correct_ru = "Сепсис",
			correct_en = "Sepsis",
			correct_cn = "败血症"
		},
		{
			ru = "Какое название патологического состояния, которое характеризуется нарушением кислотно-щелочного баланса организма в сторону повышения кислотности?",
			en = "What is the name of the pathological condition characterized by a disturbance in the acid-base balance of the body towards increased acidity?",
			cn = "哪种病理状态以机体酸碱平衡向酸度增加方向紊乱为特征？",
			answers_ru = {"ацидоз", "acidosis"},
			answers_en = {"acidosis"},
			answers_cn = {"酸中毒"},
			correct_ru = "Ацидоз",
			correct_en = "Acidosis",
			correct_cn = "酸中毒"
		},
		{
			ru = "Каким состоянием сердца пациента можно описать сдавление сердца жидкостью в перикарде?",
			en = "What heart condition can describe compression of the heart by fluid in the pericardium?",
			cn = "什么心脏状况可以描述心包内液体对心脏的压迫？",
			answers_ru = {"сердечная тампонада", "cardiac tamponade"},
			answers_en = {"cardiac tamponade"},
			answers_cn = {"心脏压塞"},
			correct_ru = "Сердечная тампонада",
			correct_en = "Cardiac tamponade",
			correct_cn = "心脏压塞"
		},
		{
			ru = "Какой группой крови должен обладать пациент, что бы он мог принимать любую группу крови?",
			en = "What blood type must a patient have to be able to receive any blood type?",
			cn = "患者必须拥有什么血型才能接受任何血型？",
			answers_ru = {"ab+", "ab положительная"},
			answers_en = {"ab positive"},
			answers_cn = {"AB型阳性"},
			correct_ru = "AB+",
			correct_en = "AB positive",
			correct_cn = "AB型阳性"
		},
		{
			ru = "Какой фразой можно описать нарушение целостности артериальной стенки?",
			en = "What phrase describes a breach in the integrity of the arterial wall?",
			cn = "哪个短语描述动脉壁完整性的破坏？",
			answers_ru = {"разрыв артерии", "artery rupture"},
			answers_en = {"artery rupture"},
			answers_cn = {"动脉破裂"},
			correct_ru = "Разрыв артерии",
			correct_en = "Artery rupture",
			correct_cn = "动脉破裂"
		},
		{
			ru = "Какой фразой можно описать опасное для жизни повреждение главной артерии в области туловища?",
			en = "What phrase describes a life-threatening injury to the main artery in the torso area?",
			cn = "哪个短语描述躯干区域主要动脉的生命危险损伤？",
			answers_ru = {"разрыв аорты", "aortic rupture"},
			answers_en = {"aortic rupture"},
			answers_cn = {"主动脉破裂"},
			correct_ru = "Разрыв аорты",
			correct_en = "Aortic rupture",
			correct_cn = "主动脉破裂"
		},
		{
			ru = "Как называется органический продукт мирного европейского существа, который имеет слабое противодействие параличу и вызывает галлюцинации?",
			en = "What is the name of the organic product from a peaceful Europa creature that has weak anti-paralysis properties and causes hallucinations?",
			cn = "来自和平木卫二生物的有机产品，具有弱抗麻痹性并引起幻觉，叫什么？",
			answers_ru = {"галлюциногенный буфотоксин", "буфотоксин"},
			answers_en = {"hallucinogenic bufotoxin", "bufotoxin"},
			answers_cn = {"幻觉性蟾毒素", "蟾毒素"},
			correct_ru = "Галлюциногенный буфотоксин",
			correct_en = "Hallucinogenic Bufotoxin",
			correct_cn = "幻觉性蟾毒素"
		},
		{
			ru = "Как называется успокоительное, способное на короткое время успокоить человека?",
			en = "What is the name of the sedative that can calm a person for a short time?",
			cn = "能在短时间内镇静人的镇静剂叫什么？",
			answers_ru = {"хлоральгидрат", "chloral hydrate"},
			answers_en = {"chloral hydrate"},
			answers_cn = {"水合氯醛"},
			correct_ru = "Хлоральгидрат",
			correct_en = "Chloral Hydrate",
			correct_cn = "水合氯醛"
		},
		{
			ru = "Название медпрепарата, способного привести к депривации сознания и введения в общую анестезию при введении в человека?",
			en = "Name of the medical drug that can lead to consciousness deprivation and induction of general anesthesia when administered to a person?",
			cn = "当给人施用时可导致意识剥夺和全身麻醉诱导的医疗药物名称？",
			answers_ru = {"пропофол", "propofol"},
			answers_en = {"propofol"},
			answers_cn = {"丙泊酚"},
			correct_ru = "Пропофол",
			correct_en = "Propofol",
			correct_cn = "丙泊酚"
		},
		{
			ru = "Какой раствор изотоничен крови?",
			en = "Which solution is isotonic to blood?",
			cn = "哪种溶液与血液等渗？",
			answers_ru = {"раствор рингера", "рингер"},
			answers_en = {"ringer's solution", "ringer"},
			answers_cn = {"林格氏溶液", "林格"},
			correct_ru = "Раствор Рингера",
			correct_en = "Ringer's Solution",
			correct_cn = "林格氏溶液"
		},
		{
			ru = "Как называется устройство, что вводится в человека при повреждении артерии, или разрыве?",
			en = "What is the name of the device that is inserted into a person when an artery is damaged or ruptured?",
			cn = "当动脉受损或破裂时插入人体的设备叫什么？",
			answers_ru = {"эндоваскулярный баллон", "баллон"},
			answers_en = {"endovascular balloon", "balloon"},
			answers_cn = {"血管内球囊", "球囊"},
			correct_ru = "Эндоваскулярный баллон",
			correct_en = "Endovascular Balloon",
			correct_cn = "血管内球囊"
		},
		{
			ru = "Аналоговое название витамина B1?",
			en = "Alternative name for vitamin B1?",
			cn = "维生素B1的替代名称？",
			answers_ru = {"тиамин", "thiamine"},
			answers_en = {"thiamine"},
			answers_cn = {"硫胺素"},
			correct_ru = "Тиамин",
			correct_en = "Thiamine",
			correct_cn = "硫胺素"
		},
		{
			ru = "Какой препарат, при введении в здоровую мозговую среду способен привести к резкому скачкообразному повышению давления в черепе?",
			en = "Which drug, when introduced into a healthy brain environment, can lead to a sharp spike in intracranial pressure?",
			cn = "哪种药物在引入健康脑环境时会导致颅内压急剧飙升？",
			answers_ru = {"кортизид", "cortiside"},
			answers_en = {"cortiside"},
			answers_cn = {"皮质剂"},
			correct_ru = "Кортизид",
			correct_en = "Cortiside",
			correct_cn = "皮质剂"
		},
		{
			ru = "Название антидота против воздействия нервно-паралитических веществ?",
			en = "Name of the antidote against the effects of nerve-paralytic substances?",
			cn = "对抗神经麻痹物质作用的解毒剂名称？",
			answers_ru = {"антропин", "atropine"},
			answers_en = {"atropine"},
			answers_cn = {"阿托品"},
			correct_ru = "Антропин",
			correct_en = "Atropine",
			correct_cn = "阿托品"
		},
		{
			ru = "Что провоцирует анапарализатор, при попадании в организм человека?",
			en = "What does anaparalyzer provoke when it enters the human body?",
			cn = "当麻醉剂进入人体时会引发什么？",
			answers_ru = {"психоз", "шизофрению"},
			answers_en = {"psychosis", "schizophrenia"},
			answers_cn = {"精神病", "精神分裂症"},
			correct_ru = "Психоз",
			correct_en = "Psychosis",
			correct_cn = "精神病"
		},
		{
			ru = "Название иммуннодепрессанта для профилактики отторжения органов?",
			en = "Name of the immunosuppressant for preventing organ rejection?",
			cn = "用于预防器官排斥的免疫抑制剂名称？",
			answers_ru = {"азатиоприн", "azathioprine"},
			answers_en = {"azathioprine"},
			answers_cn = {"硫唑嘌呤"},
			correct_ru = "Азатиоприн",
			correct_en = "Azathioprine",
			correct_cn = "硫唑嘌呤"
		},
		{
			ru = "Смесь крови инопланетного происхождения и сильных кислот называется?",
			en = "What is the mixture of alien-origin blood and strong acids called?",
			cn = "外星来源血液和强酸的混合物叫什么？",
			answers_ru = {"алая кислота", "scarlet acid"},
			answers_en = {"scarlet acid"},
			answers_cn = {"猩红酸"},
			correct_ru = "Алая кислота",
			correct_en = "Scarlet Acid",
			correct_cn = "猩红酸"
		},
		{
			ru = "Как называется вид природного гормона, выделяемого организмом человека в случае опасности?",
			en = "What is the name of the type of natural hormone released by the human body in case of danger?",
			cn = "人体在危险情况下释放的天然激素类型叫什么？",
			answers_ru = {"адреналин", "adrenaline"},
			answers_en = {"adrenaline"},
			answers_cn = {"肾上腺素"},
			correct_ru = "Адреналин",
			correct_en = "Adrenaline",
			correct_cn = "肾上腺素"
		},
		{
			ru = "Название клея-гемостатика, для быстрого лечения ран в критической ситуации?",
			en = "Name of the hemostatic glue for quick wound treatment in critical situations?",
			cn = "用于在危急情况下快速处理伤口的止血胶名称？",
			answers_ru = {"антибиотический клей", "antibiotic glue"},
			answers_en = {"antibiotic glue"},
			answers_cn = {"抗生素胶"},
			correct_ru = "Антибиотический клей",
			correct_en = "Antibiotic Glue",
			correct_cn = "抗生素胶"
		},
		{
			ru = "Название препарата, что является разновидностью сахарного спирта, способного ускорить восстановление после нейротравм пациента?",
			en = "Name of the drug that is a type of sugar alcohol capable of accelerating recovery after neurotrauma in patients?",
			cn = "一种糖醇类药物，能够加速患者神经创伤后恢复的名称？",
			answers_ru = {"маннитол", "mannitol"},
			answers_en = {"mannitol"},
			answers_cn = {"甘露醇"},
			correct_ru = "Маннитол",
			correct_en = "Mannitol",
			correct_cn = "甘露醇"
		},
		{
			ru = "Инструмент с декомпрессионным клапаном для удаления жидкости или воздуха из плевральной полости или перикарда?",
			en = "Tool with a decompression valve for removing fluid or air from the pleural cavity or pericardium?",
			cn = "带有减压阀的工具，用于从胸膜腔或心包中排出液体或空气？",
			answers_ru = {"игла", "медицинская игла"},
			answers_en = {"needle", "medical needle"},
			answers_cn = {"针", "医疗针"},
			correct_ru = "Игла",
			correct_en = "Needle",
			correct_cn = "针"
		},
		{
			ru = "Медраствор, применяющийся для устранения внутренних травм, который можно употреблять перорально?",
			en = "Medical solution used to treat internal injuries that can be taken orally?",
			cn = "用于治疗内伤的可口服医疗溶液？",
			answers_ru = {"жидкий тоник", "тоник"},
			answers_en = {"liquid tonic", "tonic"},
			answers_cn = {"液体滋补剂", "滋补剂"},
			correct_ru = "Жидкий тоник",
			correct_en = "Liquid Tonic",
			correct_cn = "液体滋补剂"
		},
		{
			ru = "Химпрепарат двойного действия, что активирует иммунную систему для нейтрализации ядов, но значительно ослабляющий общий тонус организма?",
			en = "Chemical drug of dual action that activates the immune system to neutralize poisons but significantly weakens the overall body tone?",
			cn = "双重作用的化学药物，可激活免疫系统以中和毒物但显著削弱整体体力的？",
			answers_ru = {"европовка", "europovka"},
			answers_en = {"europovka"},
			answers_cn = {"欧罗巴剂"},
			correct_ru = "Европовка",
			correct_en = "Europovka",
			correct_cn = "欧罗巴剂"
		},
		{
			ru = "Хирургический инструмент, необходимый для удаления воздуха из плевральной полости во время операции при пневмотораксе?",
			en = "Surgical instrument needed to remove air from the pleural cavity during surgery for pneumothorax?",
			cn = "在气胸手术期间从胸膜腔排出空气所需的手术器械？",
			answers_ru = {"дренаж", "drain"},
			answers_en = {"drain"},
			answers_cn = {"引流管"},
			correct_ru = "Дренаж",
			correct_en = "Drain",
			correct_cn = "引流管"
		},
		{
			ru = "Мелкозернистый минеральный порошок, использумый в медицине, называется..?",
			en = "Fine-grained mineral powder used in medicine is called..?",
			cn = "医学中使用的细粒矿物粉末称为..？",
			answers_ru = {"гипс", "plaster"},
			answers_en = {"plaster"},
			answers_cn = {"石膏"},
			correct_ru = "Гипс",
			correct_en = "Plaster",
			correct_cn = "石膏"
		},
		{
			ru = "Сильный нейролептик, чаще остальных используемый на Европе в подпольных условиях?",
			en = "Strong neuroleptic most commonly used in underground conditions on Europa?",
			cn = "在木卫二地下条件下最常用的强效神经阻滞剂？",
			answers_ru = {"галоперидол", "haloperidol"},
			answers_en = {"haloperidol"},
			answers_cn = {"氟哌啶醇"},
			correct_ru = "Галоперидол",
			correct_en = "Haloperidol",
			correct_cn = "氟哌啶醇"
		},
		
		-- Вставьте этот код в конец массива questions (перед последней закрывающей скобкой "}")

		{
			ru = "Какой максимальный квиз-стрик можно получить, если на вопросы отвечать подряд и правильно?",
			en = "What is the maximum quiz streak you can get if you answer questions correctly in a row?",
			cn = "如果连续正确回答问题，可以获得的最大测验连胜次数是多少？",
			answers_ru = {"20", "двадцать"},
			answers_en = {"20", "twenty"},
			answers_cn = {"20", "二十"},
			correct_ru = "20",
			correct_en = "20",
			correct_cn = "20"
		},
		{
			ru = "Ты один?",
			en = "Are you alone?",
			cn = "你是一个人吗？",
			answers_ru = {"да"},
			answers_en = {"yes"},
			answers_cn = {"是"},
			correct_ru = "Да",
			correct_en = "Yes",
			correct_cn = "是"
		},
		{
			ru = "Существует ли в распространённой модели улучшенного КПК возможность выставить медицинский маячок на сонаре?",
			en = "Does the common model of the advanced PDA have the ability to place a medical beacon on the sonar?",
			cn = "常见的高级PDA型号是否具备在声纳上放置医疗信标的功能？",
			answers_ru = {"да"},
			answers_en = {"yes"},
			answers_cn = {"是"},
			correct_ru = "Да",
			correct_en = "Yes",
			correct_cn = "是"
		},
		{
			ru = "В каком КПК присутствует динамо-машина?",
			en = "Which PDA contains a dynamo?",
			cn = "哪种PDA包含手摇发电机？",
			answers_ru = {"инженерный", "инженерном"},
			answers_en = {"engineering"},
			answers_cn = {"工程"},
			correct_ru = "Инженерный",
			correct_en = "Engineering",
			correct_cn = "工程"
		},
		{
			ru = "Что эффективнее в ближнем бою - шокер, голые кулаки, или перцовый баллончик при условии, что некуда бежать?",
			en = "What is more effective in melee combat - a stun gun, bare fists, or a pepper spray, provided there is nowhere to run?",
			cn = "在无处可逃的情况下，什么武器在近战中更有效 - 电击枪、赤手空拳还是胡椒喷雾？",
			answers_ru = {"шокер"},
			answers_en = {"stun gun"},
			answers_cn = {"电击枪"},
			correct_ru = "Шокер",
			correct_en = "Stun gun",
			correct_cn = "电击枪"
		},
		{
			ru = "Расшифровка кода морзе: ...---...",
			en = "Decipher the Morse code: ...---...",
			cn = "解读莫尔斯电码：...---...",
			answers_ru = {"сос", "sos", "спаси наши души"},
			answers_en = {"sos", "save our souls"},
			answers_cn = {"sos", "拯救我们的灵魂"},
			correct_ru = "SOS",
			correct_en = "SOS",
			correct_cn = "SOS"
		},
		{
			ru = "По какой причине наш вид вынужден спускаться с поверхностных вод планеты, в более глубокие, опасные и неизученные?",
			en = "For what reason is our species forced to descend from the planet's surface waters into deeper, more dangerous, and unexplored areas?",
			cn = "由于什么原因，我们的物种被迫从行星表层水域下降到更深、更危险和未开发的区域？",
			answers_ru = {"радиация", "пояса радиации", "радиоактивность"},
			answers_en = {"radiation", "radiation belts"},
			answers_cn = {"辐射", "辐射带"},
			correct_ru = "Радиация",
			correct_en = "Radiation",
			correct_cn = "辐射"
		},
		{
			ru = "Средний рост человека на Европе в сантиметрах?",
			en = "Average height of a human on Europa in centimeters?",
			cn = "欧罗巴上人类的平均身高（厘米）？",
			answers_ru = {"170", "170см"},
			answers_en = {"170", "170cm"},
			answers_cn = {"170", "170厘米"},
			correct_ru = "170",
			correct_en = "170",
			correct_cn = "170"
		},
		{
			ru = "Как называлась планета, с которой мы произошли?",
			en = "What was the name of the planet we originated from?",
			cn = "我们起源于哪颗行星？",
			answers_ru = {"земля"},
			answers_en = {"earth"},
			answers_cn = {"地球"},
			correct_ru = "Земля",
			correct_en = "Earth",
			correct_cn = "地球"
		},
		{
			ru = "Как назывался спутник Земли, погружавший нас в длань ночи?",
			en = "What was the name of Earth's satellite that plunged us into the hand of night?",
			cn = "地球的卫星叫什么名字，它将我们带入黑夜的掌控？",
			answers_ru = {"луна"},
			answers_en = {"moon"},
			answers_cn = {"月亮"},
			correct_ru = "Луна",
			correct_en = "Moon",
			correct_cn = "月亮"
		},
		{
			ru = "Как называлась звезда, что грела нас по ночам?",
			en = "What was the name of the star that warmed us during the day?",
			cn = "白天温暖我们的恒星叫什么名字？",
			answers_ru = {"солнце"},
			answers_en = {"sun"},
			answers_cn = {"太阳"},
			correct_ru = "Солнце",
			correct_en = "Sun",
			correct_cn = "太阳"
		},
		{
			ru = "Самое медленное существо Бездны, известное человечеству?",
			en = "The slowest creature of the Abyss known to humanity?",
			cn = "人类已知的最慢的深渊生物？",
			answers_ru = {"блокиратор", "латчер"},
			answers_en = {"latcher"},
			answers_cn = {"拉彻"},
			correct_ru = "Латчер",
			correct_en = "Latcher",
			correct_cn = "拉彻"
		},
		{
			ru = "При встрече с особью-самкой Молотоглава-матриарха в открытой местности, будет ли она пытаться атаковать человека, или иных существ при прямой зоне видимости?",
			en = "When encountering a female Hammerhead Matriarch in an open area, will it try to attack a human or other creatures in direct line of sight?",
			cn = "在开阔地区遇到雌性锤头鲨母兽时，它会在直接视线内尝试攻击人类或其他生物吗？",
			answers_ru = {"нет", "не будет"},
			answers_en = {"no"},
			answers_cn = {"不会"},
			correct_ru = "Нет",
			correct_en = "No",
			correct_cn = "不会"
		},
		{
			ru = "В случае наличия на подлодке Артефакта Насонова, какое существо с большей вероятностью отреагирует агрессией, даже само не желая нападать, у которого провоцируемое поведение объясняется наличием более мелких организмов, что могут контролировать его поведение?",
			en = "If a Nasonov Artifact is on the submarine, which creature is more likely to react with aggression, even if it doesn't want to attack itself, whose provoked behavior is explained by the presence of smaller organisms that can control its behavior?",
			cn = "如果潜艇上有纳索诺夫神器，哪种生物更有可能表现出攻击性，即使它本身不想攻击，其被激怒的行为是由能够控制其行为的较小生物的存在来解释的？",
			answers_ru = {"молотоглав-матриарх", "матриарх-молотоглав", "матриарх"},
			answers_en = {"hammerhead matriarch", "matriarch"},
			answers_cn = {"锤头鲨母兽", "母兽"},
			correct_ru = "Молотоглав-матриарх",
			correct_en = "Hammerhead Matriarch",
			correct_cn = "锤头鲨母兽"
		},
		{
			ru = "Внутри 'головы' Молотоглава-матриарха детёныши обитают в яйцах, или в 'спящем' виде, соединённые одной общей пуповиной?",
			en = "Inside the 'head' of the Hammerhead Matriarch, do the offspring live in eggs, or in a 'dormant' state, connected by a common umbilical cord?",
			cn = "在锤头鲨母兽的“头部”内，幼体是生活在卵中，还是以“休眠”状态存在，通过一条共同的脐带连接？",
			answers_ru = {"в яйцах", "яйца"},
			answers_en = {"in eggs", "eggs"},
			answers_cn = {"在卵中", "卵"},
			correct_ru = "В яйцах",
			correct_en = "In eggs",
			correct_cn = "在卵中"
		},
		{
			ru = "Как звали самого известного представителя паствы Церкви Паразита?",
			en = "What was the name of the most famous member of the flock of the Church of the Parasite?",
			cn = "寄生虫教会最著名的信众叫什么名字？",
			answers_ru = {"яков субра", "яков", "субра"},
			answers_en = {"yakov subra", "yakov", "subra"},
			answers_cn = {"雅科夫·苏布拉", "雅科夫", "苏布拉"},
			correct_ru = "Яков Субра",
			correct_en = "Yakov Subra",
			correct_cn = "雅科夫·苏布拉"
		},
		{
			ru = "Имя печально известного Молотоглава, которого ненавидел один из капитанов на Европе?",
			en = "The name of the infamous Hammerhead that was hated by one of the captains on Europa?",
			cn = "那个被欧罗巴上一位船长憎恨的臭名昭著的锤头鲨的名字？",
			answers_ru = {"джек", "мопинг джек"},
			answers_en = {"jack", "moping jack"},
			answers_cn = {"杰克", "莫平·杰克"},
			correct_ru = "Мопинг Джек",
			correct_en = "Moping Jack",
			correct_cn = "莫平·杰克"
		},
		{
			ru = "На принципе какого устройства учёные Коалиции изобрели прототип Сканера Руин?",
			en = "On the principle of which device did the Coalition scientists invent the prototype of the Ruin Scanner?",
			cn = "联盟科学家根据什么设备的原理发明了废墟扫描仪的原型？",
			answers_ru = {"ручной сонар", "сонар"},
			answers_en = {"handheld sonar", "sonar"},
			answers_cn = {"手持声纳", "声纳"},
			correct_ru = "Ручной сонар",
			correct_en = "Handheld sonar",
			correct_cn = "手持声纳"
		},
		{
			ru = "Какая утка на Европе вызывает неприятные эмоции, вплоть до перелома черепа?",
			en = "Which duck on Europa causes unpleasant emotions, even skull fractures?",
			cn = "欧罗巴上的哪种鸭子会引起不愉快的情绪，甚至导致头骨骨折？",
			answers_ru = {"глубоководная утка", "глубоководная"},
			answers_en = {"deepwater duck"},
			answers_cn = {"深水鸭"},
			correct_ru = "Глубоководная утка",
			correct_en = "Deepwater duck",
			correct_cn = "深水鸭"
		},
		{
			ru = "Сколько всего цветов проводов выпускается для нужд подлодок?",
			en = "How many colors of wires are produced for the needs of submarines?",
			cn = "为潜艇需求生产的电线有多少种颜色？",
			answers_ru = {"7", "семь"},
			answers_en = {"7", "seven"},
			answers_cn = {"7", "七"},
			correct_ru = "7",
			correct_en = "7",
			correct_cn = "7"
		},
		{
			ru = "Сколько парней было у разработчика Animated Arms с момента добавления этого вопроса?",
			en = "How many boyfriends has the developer of Animated Arms had since the addition of this question?",
			cn = "自添加此问题以来，Animated Arms 的开发人员有过几个男朋友？",
			answers_ru = {"2", "два", "я чё ебу"},
			answers_en = {"2", "two", "the f do i know"},
			answers_cn = {"2", "二", "我他妈怎么知道"},
			correct_ru = "2",
			correct_en = "2",
			correct_cn = "2"
		},
		{
			ru = "Что добывается с одного из существ в подпольных условиях, что может использоваться как кустарный щит, если срезать его по форме?",
			en = "What is obtained from one of the creatures in underground conditions that can be used as a makeshift shield if cut into shape?",
			cn = "从某种生物身上获取的东西，在地下条件下，如果切割成形状，可以用作临时盾牌？",
			answers_ru = {"оболочка молоха"},
			answers_en = {"moloch shell"},
			answers_cn = {"莫洛克外壳"},
			correct_ru = "Оболочка Молоха",
			correct_en = "Moloch Shell",
			correct_cn = "莫洛克外壳"
		},
		{
			ru = "Голову какого существа на традициях празднества человечества как вида на Европе отрезают и одевают на голову?",
			en = "The head of which creature, in the festive traditions of humanity as a species on Europa, is cut off and worn on the head?",
			cn = "在欧罗巴上人类的节日传统中，他们会割下哪种生物的头戴在头上？",
			answers_ru = {"маска ползуна", "ползуна"},
			answers_en = {"crawler mask", "crawler"},
			answers_cn = {"爬行者面具", "爬行者"},
			correct_ru = "Маска ползуна",
			correct_en = "Crawler Mask",
			correct_cn = "爬行者面具"
		},
		{
			ru = "Из чего состоят шипы, что растут для охоты на мелкую добычу у Шипостая?",
			en = "What are the spines that grow for hunting small prey on a Spiketail made of?",
			cn = "刺尾鱼身上用于捕猎小猎物的刺是由什么构成的？",
			answers_ru = {"цинк и углерод", "углерод и цинк", "цинкуглерод", "углеродцинк"},
			answers_en = {"zinc and carbon", "carbon and zinc"},
			answers_cn = {"锌和碳", "碳和锌"},
			correct_ru = "Цинк и углерод",
			correct_en = "Zinc and carbon",
			correct_cn = "锌和碳"
		},
		{
			ru = "Редкая, изысканная часть крупного существа с панцирем, что можно задорого продать местным коллекционерам?",
			en = "A rare, exquisite part of a large shelled creature that can be sold dearly to local collectors?",
			cn = "一种稀有、精致的大型带壳生物部位，可以高价卖给当地收藏家？",
			answers_ru = {"кость молоха", "молоха кость"},
			answers_en = {"moloch bone"},
			answers_cn = {"莫洛克骨头"},
			correct_ru = "Кость Молоха",
			correct_en = "Moloch Bone",
			correct_cn = "莫洛克骨头"
		},
		{
			ru = "Изысканный мясной деликатес, который вырезают из мужских особей семейства Молотоглавов?",
			en = "An exquisite meat delicacy carved from male specimens of the Hammerhead family?",
			cn = "一种从锤头鲨家族的雄性个体身上切下的精致肉类佳肴？",
			answers_ru = {"рёбра молотоглава", "рёбра", "рёбрышки"},
			answers_en = {"hammerhead ribs", "ribs"},
			answers_cn = {"锤头鲨肋骨", "肋骨"},
			correct_ru = "Рёбра молотоглава",
			correct_en = "Hammerhead ribs",
			correct_cn = "锤头鲨肋骨"
		},
		{
			ru = "Кусок защитной оболочки существа с острым клювом синего цвета, что люди пробовали использовать в качестве полевого бронежилета?",
			en = "A piece of the protective shell of a creature with a sharp blue beak that people have tried to use as a field body armor?",
			cn = "一种拥有锋利蓝色喙的生物的保护性外壳碎片，人们曾尝试将其用作野战防弹衣？",
			answers_ru = {"оболочка раптора", "оболочка мудраптора", "кусок раптора"},
			answers_en = {"raptor shell", "mudraptor shell"},
			answers_cn = {"迅猛龙外壳", "泥浆迅猛龙外壳"},
			correct_ru = "Оболочка раптора",
			correct_en = "Raptor shell",
			correct_cn = "迅猛龙外壳"
		},
		{
			ru = "Позиция церемониального меча, при котором боец удерживает середину лезвия меча, для нанесения усиленных уколов своему оппоненту, либо для ударов эфесом?",
			en = "The ceremonial sword stance where the fighter holds the middle of the blade to deliver enhanced thrusts to their opponent, or for hilt strikes?",
			cn = "一种仪式用剑的姿势，战士握住剑刃中部，以向对手发出加强的刺击，或用剑柄击打？",
			answers_ru = {"халфсворд", "половина меча"},
			answers_en = {"half-swording", "halfsword"},
			answers_cn = {"半剑术"},
			correct_ru = "Халфсворд",
			correct_en = "Half-swording",
			correct_cn = "半剑术"
		},
		{
			ru = "На основе какого артефакта работает механизм во взгляде древних биомашин - Смотрителей?",
			en = "On which artifact is the mechanism in the gaze of the ancient biomachines - the Watchers - based?",
			cn = "古代生物机器——看守者——目光中的机制是基于哪个神器运作的？",
			answers_ru = {"психотропный артефакт", "артефакт психоза", "психический артефакт"},
			answers_en = {"psychotropic artifact", "psychosis artifact", "psychic artifact"},
			answers_cn = {"精神药物神器", "精神病神器", "精神神器"},
			correct_ru = "Психотропный артефакт",
			correct_en = "Psychotropic artifact",
			correct_cn = "精神药物神器"
		},
		{
			ru = "Сколько вариантов одежды для Сотрудников СБ Коалиции и подводников выпускается швейными и заводами?",
			en = "How many clothing variants for Coalition Security Officers and submariners are produced by sewing factories?",
			cn = "缝纫厂为联盟安全官员和潜艇船员生产多少种服装？",
			answers_ru = {"3", "три"},
			answers_en = {"3", "three"},
			answers_cn = {"3", "三"},
			correct_ru = "3",
			correct_en = "3",
			correct_cn = "3"
		},
		{
			ru = "Самая дешёвая и распространённая подлодка от цеха судостроительства Коалиции, которую выпускают под маркой 'Боевая'?",
			en = "The cheapest and most common submarine from the Coalition shipyard, produced under the 'Combat' brand?",
			cn = "联盟造船厂生产的、以“战斗”品牌发布的最便宜、最常见的潜艇？",
			answers_ru = {"барсук"},
			answers_en = {"badger"},
			answers_cn = {"獾"},
			correct_ru = "Барсук",
			correct_en = "Badger",
			correct_cn = "獾"
		},
		{
			ru = "Самая дорогая субмарина разведывательного класса, выпущенная на гражданский рынок и доступная к приобретению в доках Коалиции?",
			en = "The most expensive reconnaissance-class submarine released to the civilian market and available for purchase at Coalition docks?",
			cn = "发布到民用市场并在联盟码头可以购买到的最昂贵的侦察级潜艇？",
			answers_ru = {"винтерхалтер"},
			answers_en = {"winterhalter"},
			answers_cn = {"温特哈尔特"},
			correct_ru = "Винтерхалтер",
			correct_en = "Winterhalter",
			correct_cn = "温特哈尔特"
		},
		{
			ru = "Самое ненадёжное место внутри подлодки с двумя приметными 'горбами'?",
			en = "The most unreliable place inside a submarine with two noticeable 'humps'?",
			cn = "具有两个显著“驼峰”的潜艇内部最不可靠的地方？",
			answers_ru = {"передний балласт", "балластный бак", "балласт"},
			answers_en = {"forward ballast", "ballast tank", "ballast"},
			answers_cn = {"前部压载舱", "压载水舱", "压载"},
			correct_ru = "Передний балласт",
			correct_en = "Forward ballast",
			correct_cn = "前部压载舱"
		},
		{
			ru = "Что означает первая буква латинского алфавита в наименовании субмарины R-29?",
			en = "What does the first letter of the Latin alphabet in the name of the submarine R-29 mean?",
			cn = "潜艇R-29名称中的第一个拉丁字母是什么意思？",
			answers_ru = {"raven", "rav2n", "рейвен", "рэйвен"},
			answers_en = {"raven"},
			answers_cn = {"乌鸦"},
			correct_ru = "Raven",
			correct_en = "Raven",
			correct_cn = "乌鸦"
		},
		{
			ru = "Отгадай субмарину по описанию: 'мощная, массивная, грозно выглядящая подлодка Европы, оснащённая двумя двигателями и четырьмя балластными баками, для управления которой требуется большая команда..'",
			en = "Guess the submarine by the description: 'A powerful, massive, menacing-looking submarine of Europa, equipped with two engines and four ballast tanks, requiring a large crew to operate..'",
			cn = "根据描述猜潜艇：'欧罗巴一艘强大、庞大、外观凶猛的潜艇，配备两台发动机和四个压载舱，需要大量船员操作..'",
			answers_ru = {"бериллия"},
			answers_en = {"berilia"},
			answers_cn = {"贝里利亚"},
			correct_ru = "Бериллия",
			correct_en = "Berilia",
			correct_cn = "贝里利亚"
		},
		{
			ru = "Отгадай субмарину по описанию: 'Модель часто модифицируемой подводной лодки, выпускаемой под разными конфигурациями, где одна из самых частых - версия с отцепляемым балластом и дроном.'",
			en = "Guess the submarine by the description: 'A model of a frequently modified submarine, produced in different configurations, where one of the most common is the version with a detachable ballast and a drone.'",
			cn = "根据描述猜潜艇：'一种经常改装的潜艇型号，以不同配置生产，其中最常见的一种是可分离压载舱和无人机的版本。'",
			answers_ru = {"ремора"},
			answers_en = {"remora"},
			answers_cn = {"雷莫拉"},
			correct_ru = "Ремора",
			correct_en = "Remora",
			correct_cn = "雷莫拉"
		},
		{
			ru = "Отгадай народное прозвище субмарину по кодовому названию - 'WH4-L3'",
			en = "Guess the popular nickname for the submarine by its code name - 'WH4-L3'",
			cn = "根据代号'WH4-L3'猜潜艇的民间绰号",
			answers_ru = {"горбун"},
			answers_en = {"humpback"},
			answers_cn = {"驼背鲸"},
			correct_ru = "Горбун",
			correct_en = "Humpback",
			correct_cn = "驼背鲸"
		},
		{
			ru = "Бог - предатель?",
			en = "Is God a traitor?",
			cn = "上帝是叛徒吗？",
			answers_ru = {"да", "а кто спрашивает"},
			answers_en = {"yes", "who's asking"},
			answers_cn = {"是", "谁问的"},
			correct_ru = "Да",
			correct_en = "Yes",
			correct_cn = "是"
		},
		{
			ru = "Я хороший мальчик?",
			en = "Am I a good boy?",
			cn = "我是个好孩子吗？",
			answers_ru = {"да"},
			answers_en = {"yes"},
			answers_cn = {"是"},
			correct_ru = "Да",
			correct_en = "Yes",
			correct_cn = "是"
		},
		{
			ru = "9 знаков ПОСЛЕ числа Пи?",
			en = "9 digits AFTER the number Pi?",
			cn = "圆周率π后的9位数字？",
			answers_ru = {"159265358"},
			answers_en = {"159265358"},
			answers_cn = {"159265358"},
			correct_ru = "159265358",
			correct_en = "159265358",
			correct_cn = "159265358"
		},
		{
			ru = "Название одного из самых фатальных, наполненного эскапизмом трека на флешке и кассете, который любит создатель Animated Arms?",
			en = "The name of one of the most fatal, escapism-filled tracks on the USB drive and cassette that the creator of Animated Arms loves?",
			cn = "Animated Arms 的创建者喜欢的U盘和磁带中最具宿命感、充满逃避主义的曲目名称？",
			answers_ru = {"invisible", "невидимый"},
			answers_en = {"invisible"},
			answers_cn = {"看不见"},
			correct_ru = "INVISIBLE",
			correct_en = "INVISIBLE",
			correct_cn = "INVISIBLE"
		},
		{
			ru = "Количество инопланетных летописей, раскрытых неизвестными по всей Европе и окрестностям руин исчисляется количеством в ...?",
			en = "The number of alien chronicles, discovered by unknown parties across Europa and the surrounding ruins, amounts to ...?",
			cn = "在整个欧罗巴及周边废墟中被未知方发现的外星编年史数量为...？",
			answers_ru = {"6 записей", "6", "шесть"},
			answers_en = {"6 records", "6", "six"},
			answers_cn = {"6个记录", "6", "六"},
			correct_ru = "6",
			correct_en = "6",
			correct_cn = "6"
		},
		{
			ru = "Сколько всего известно артефактов инопланетного происхождения, от мала до велика, Научному Институту Коалиции?",
			en = "How many artifacts of alien origin, from small to large, are known to the Coalition Science Institute in total?",
			cn = "联盟科学研究所总共已知有多少外星起源的神器，从小到大？",
			answers_ru = {"8 артефактов", "восемь"},
			answers_en = {"8 artifacts", "eight"},
			answers_cn = {"8个神器", "八"},
			correct_ru = "8",
			correct_en = "8",
			correct_cn = "8"
		},
		{
			ru = "Можно ли назвать фрактальных стражей машинами, что циркулируют в экосистемах, или правильнее - мехсистемах, роботами?",
			en = "Can fractal guardians be called machines that circulate in ecosystems, or more correctly - mechanosystems, robots?",
			cn = "可以称分形守卫者为在生态系统或更正确地说机械系统中循环的机器、机器人吗？",
			answers_ru = {"да", "можно", "конечно"},
			answers_en = {"yes", "you can", "of course"},
			answers_cn = {"是", "可以", "当然"},
			correct_ru = "Да",
			correct_en = "Yes",
			correct_cn = "是"
		},
		{
			ru = "Название клоунского акустического инструмента для медитации.",
			en = "The name of the clown acoustic instrument for meditation.",
			cn = "小丑用于冥想的声学乐器名称。",
			answers_ru = {"дементонитовые цимбалы", "цимбалы"},
			answers_en = {"dementonite cymbals", "cymbals"},
			answers_cn = {"痴呆石钹", "钹"},
			correct_ru = "Дементонитовые цимбалы",
			correct_en = "Dementonite cymbals",
			correct_cn = "痴呆石钹"
		},
		{
			ru = "Одноразовое оружие, используемое для высадки десанта на вражеские корабли.",
			en = "A disposable weapon used for landing troops on enemy ships.",
			cn = "用于在敌舰上登陆部队的一次性武器。",
			answers_ru = {"абордажный модуль", "абордажная капсула", "капсула"},
			answers_en = {"boarding module", "boarding pod", "pod"},
			answers_cn = {"登船模块", "登船舱", "舱"},
			correct_ru = "Абордажный модуль",
			correct_en = "Boarding module",
			correct_cn = "登船模块"
		},
		{
			ru = "Число генетических материалов, которое современная наука Института Коалиции может синтезировать при помощи Исследовательского терминала?",
			en = "The number of genetic materials that modern science of the Coalition Institute can synthesize using the Research Terminal?",
			cn = "联盟研究所的现代科学使用研究终端可以合成的遗传材料的数量？",
			answers_ru = {"13", "тринадцать"},
			answers_en = {"13", "thirteen"},
			answers_cn = {"13", "十三"},
			correct_ru = "13",
			correct_en = "13",
			correct_cn = "13"
		},
		
		{
			ru = "Общесобирательное название токсинов, провоцирующие нарушения, что вызывают психоз?",
			en = "General collective name for toxins that provoke disorders causing psychosis?",
			cn = "导致引起精神病障碍的毒素的通用统称？",
			answers_ru = {"бредоген", "deliriogen"},
			answers_en = {"deliriogen"},
			answers_cn = {"谵妄原"},
			correct_ru = "Бредоген",
			correct_en = "Deliriogen",
			correct_cn = "谵妄原"
		}
	}
	
	-- Select random question
	local randomIndex = math.random(1, #questions)
	quizState.currentQuestion = questions[randomIndex]
	quizState.waitingForContinuation = false
	-- Display question in selected language
	self:displayCurrentQuestion(terminalId, currentLang, terminalState)
end

-- New function to display the current question
function TerminalClass:displayCurrentQuestion(terminalId, currentLang, terminalState)
	local quizState = terminalState.quiz
	if currentLang == "ru" then
		TerminalPrint("=== BAROTRAUMA ВИКТОРИНА ===")
		TerminalPrint("Вопрос:")
		TerminalPrint(quizState.currentQuestion.ru)
		TerminalPrint("")
		TerminalPrint("Введите ответ:")
	elseif currentLang == "cn" then
		TerminalPrint("=== BAROTRAUMA 测验 ===")
		TerminalPrint("问题:")
		TerminalPrint(quizState.currentQuestion.cn)
		TerminalPrint("")
		TerminalPrint("输入答案:")
	else
		TerminalPrint("=== BAROTRAUMA QUIZ ===")
		TerminalPrint("Question:")
		TerminalPrint(quizState.currentQuestion.en)
		TerminalPrint("")
		TerminalPrint("Enter your answer:")
	end
end

--prints to terminal
function TerminalPrint(arg0, arg1, arg2)
	local message = tostring(arg0)
	if(arg1~=nil) then message = message .. "    " .. tostring(arg1) end
	if(arg2~=nil) then message = message .. "    " .. tostring(arg2) end
	terminalCur.mode = TerminalMode.PRINT
	terminalCur.instance.ShowMessage = message
end

--reads input (removes player message)
function TerminalRead()
	terminalCur.mode = TerminalMode.READ
	coroutine.yield()
	terminalCur.mode = TerminalMode.NULL
	-- Clear history to remove player input
	if CLIENT == false then
		terminalCur.instance.History = {}
		terminalCur.instance.SyncHistory()
	end
	return terminalCur.read
end

local function RegisterTerminalDelay(terminal, delayEnd)
	if terminal == nil then return end

	terminal.delayEnd = delayEnd
	terminal.mode = TerminalMode.DELAY
	delayedTerminals[terminal] = true

	if nextTerminalDelayCheck == nil or delayEnd < nextTerminalDelayCheck then
		nextTerminalDelayCheck = delayEnd
	end
end

--delay without lags
function TerminalDelay(seconds)
	RegisterTerminalDelay(terminalCur, os.clock() + seconds)
	coroutine.yield()
end

-- Set language for current terminal
function TerminalSetLanguage(lang)
	local terminalId = tostring(terminalCur.instance.item.ID)
	terminalLanguages[terminalId] = lang
end

Hook.Add("item.removed", "cleanup_terminal_state", function(item)
	if item == nil then return end

	local terminalId = tostring(item.ID)
	local terminal = terminalLookup[terminalId]
	if terminal ~= nil then
		terminalLookup[terminalId] = nil
		delayedTerminals[terminal] = nil
		terminalCreationTimes[terminalId] = nil
		terminalLanguages[terminalId] = nil
		terminalStates[terminalId] = nil
		userLanguageInitialized[terminalId] = nil

		for index, trackedTerminal in ipairs(terminals) do
			if trackedTerminal == terminal then
				table.remove(terminals, index)
				break
			end
		end
	end

	if nextTerminalDelayCheck ~= nil and next(delayedTerminals) == nil then
		nextTerminalDelayCheck = nil
	end
end)

--delay processing
Hook.Add("think", "TerminalDelayThink", function()
	if nextTerminalDelayCheck == nil then
		return
	end

	local now = os.clock()
	if now < nextTerminalDelayCheck then
		return
	end

	local toRemove = {}
	local soonestNextDelay = nil

	for terminal in pairs(delayedTerminals) do
		if terminal == nil or terminal.co == nil or coroutine.status(terminal.co) == "dead" then
			table.insert(toRemove, terminal)
		elseif terminal.mode == TerminalMode.DELAY then
			if now >= terminal.delayEnd then
				table.insert(toRemove, terminal)
				terminal.mode = TerminalMode.NULL
				coroutine.resume(terminal.co)
			else
				if soonestNextDelay == nil or terminal.delayEnd < soonestNextDelay then
					soonestNextDelay = terminal.delayEnd
				end
			end
		else
			table.insert(toRemove, terminal)
		end
	end

	for _, terminal in ipairs(toRemove) do
		delayedTerminals[terminal] = nil
	end

	nextTerminalDelayCheck = soonestNextDelay
end)

function TerminalRun(input)
	--replace print and read with terminal functions
	local input = input:gsub("print%(", "TerminalPrint(")
	local input = input:gsub("io.read%(%)", "TerminalRead()")
	local input = input:gsub("TerminalDelay%(", "TerminalDelay(")
	local input = input:gsub("TerminalSetLanguage%(", "TerminalSetLanguage(")

	--execute as coroutine
	local func, error = load(input)
	if (func) then
		local ok, error2 = pcall(func)
		if (ok==false) then
			TerminalPrint(error2)
		end
	else
		TerminalPrint(error)
	end
	
	-- Сбрасываем состояние после завершения скрипта
	terminalCur.mode = TerminalMode.NULL
	terminalCur.co = nil
end