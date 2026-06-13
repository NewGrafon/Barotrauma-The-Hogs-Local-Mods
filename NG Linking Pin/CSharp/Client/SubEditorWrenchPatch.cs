using System.Collections.Generic;
using Barotrauma;
using HarmonyLib;

namespace NGLinkingPin
{
    // Places a wrench in the sub-editor dummy character's hand when entering wiring mode, so panels
    // requiring a wrench can be opened alongside screwdriver ones. Ported from "Linking Pin Lua".
    // RUS: Кладёт гаечный ключ в руку редакторного манекена при входе в режим проводки. Перенесено.
    public sealed class SubEditorWrenchPatch : IAssemblyPlugin
    {
        private Harmony harmony;

        public void Initialize()
        {
            harmony = new Harmony("nglinkingpin.subeditor");
            harmony.Patch(
                original: typeof(SubEditorScreen).GetMethod("SetMode"),
                postfix: new HarmonyMethod(typeof(SubEditorWrenchPatch), nameof(SetMode_Postfix)));
        }

        public void OnLoadCompleted() { }
        public void PreInitPatching() { }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            harmony = null;
        }

        private static void SetMode_Postfix(SubEditorScreen __instance, SubEditorScreen.Mode newMode)
        {
            if (newMode != SubEditorScreen.Mode.Wiring) return;

            var dummyChar = __instance.GetType()
                .GetField("dummyCharacter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(__instance) as Character;
            if (dummyChar == null) return;

            var wrenchPrefab = MapEntityPrefab.Find(null, "wrench") as ItemPrefab;
            if (wrenchPrefab == null) return;

            var wrench = new Item(wrenchPrefab, Microsoft.Xna.Framework.Vector2.Zero, null);
            dummyChar.Inventory.TryPutItem(wrench, null, new List<InvSlotType> { InvSlotType.Any });
        }
    }
}
