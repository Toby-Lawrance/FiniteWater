using FiniteWater.block;
using FiniteWater.blockBehaviors;
using FiniteWater.Rain;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FiniteWater
{
    public class FiniteWaterModSystem : ModSystem
    {
        public Harmony harmony;

        // Called on server and client
        // Useful for registering block/entity classes on both sides
        public override void Start(ICoreAPI api)
        {
            Mod.Logger.Notification("Hello from template mod: " + api.Side);
            api.RegisterBlockClass(Mod.Info.ModID + ".finitewater",typeof(FiniteWaterBlock));
            api.RegisterBlockBehaviorClass(Mod.Info.ModID + ".finitespreadlevelling", typeof(FiniteSpreadLevelling));

            if (!Harmony.HasAnyPatches(Mod.Info.ModID))
            {
                harmony = new Harmony(Mod.Info.ModID);
                harmony.PatchAll();
            }
        }

        public WeatherSimulationRainWater rainWeather;

        public override void StartServerSide(ICoreServerAPI api)
        {
            Mod.Logger.Notification("Hello from template mod server side: " + Lang.Get("finitewater:hello"));
            
            var serverWeather = api.ModLoader.GetModSystem<WeatherSystemServer>();
            rainWeather = new WeatherSimulationRainWater(api, serverWeather);
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            Mod.Logger.Notification("Hello from template mod client side: " + Lang.Get("finitewater:hello"));
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(Mod.Info.ModID);
            base.Dispose();
        }

    }
}
