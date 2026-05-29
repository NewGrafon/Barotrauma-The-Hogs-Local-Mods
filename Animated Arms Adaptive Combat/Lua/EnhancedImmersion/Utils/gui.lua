-- modified from easysettings.lua from Evil Factory's LuaAudioOverhaul
-- https://steamcommunity.com/sharedfiles/filedetails/?id=2868921484

local gui = {}

local GUIComponent = LuaUserData.CreateStatic('Barotrauma.GUIComponent')

function gui.Frame(parent, size, style)
   return GUI.Frame(GUI.RectTransform(size or Vector2.One * 0.95, parent.RectTransform, GUI.Anchor.Center, GUI.Pivot.Center), style)
end

function gui.LayoutGroup(parent, size, isHorizontal, anchor)
   local layout = GUI.LayoutGroup(GUI.RectTransform(size or Vector2.One, parent.RectTransform, anchor or GUI.Anchor.Center, GUI.Pivot.Center), isHorizontal or false)
   return layout
end

function gui.Label(parent, text, font, size, alignment)
   if style == nil then style = GUI.GUIStyle.Font end
   return GUI.TextBlock(GUI.RectTransform(size or Vector2(1, 0.25), parent.RectTransform, GUI.Anchor.CenterLeft), text, nil, font, alignment)
end

function gui.Button(parent, name, onClicked, style, tooltip)
   if style == nil then style = "GUIButton" end

   local button = GUI.Button(GUI.RectTransform(Vector2(1, 0.05), parent.RectTransform, GUI.Anchor.BottomCenter), name, GUI.Alignment.Center, style)
   if tooltip then
      button.ToolTip = tooltip
   end
   if onClicked then
      button.OnClicked = onClicked
   end
   return button
end

function gui.SliderLabel(parent, min, max, onSelected, value)
   local layout = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, 0.5), parent.RectTransform, GUI.Anchor.CenterLeft), true)
   local slider = GUI.ScrollBar(GUI.RectTransform(Vector2(0.8, 1), layout.RectTransform), 0.1, nil, "GUISlider")
   local label = GUI.TextBlock(GUI.RectTransform(Vector2(0.2, 0.8), layout.RectTransform), tostring(value)..'%', nil, nil, GUI.Alignment.Center)
   slider.Range = Vector2(min, max)
   slider.BarScrollValue = value or max / 2
   slider.OnMoved = function ()
      onSelected(slider.BarScrollValue)
      label.Text = tostring(math.floor(slider.BarScrollValue)) .. '%'
   end
   return {slider, label}
end

function gui.TickBox(parent, text, onSelected, state, tooltip)
   if state == nil then state = true end

   local tickBox = GUI.TickBox(GUI.RectTransform(Vector2(1, 0.2), parent.RectTransform), text)
   tickBox.Selected = state
   tickBox.OnSelected = function ()
      onSelected(tickBox.State == GUIComponent.ComponentState.Selected)
   end

   if tooltip then
      tickBox.ToolTip = tooltip
   end

   return tickBox
end


return gui
