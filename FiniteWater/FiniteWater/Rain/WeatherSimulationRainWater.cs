using FiniteWater.blockBehaviors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace FiniteWater.Rain
{
    public class WeatherSimulationRainWater
    {

        private readonly ICoreServerAPI sapi;

        private readonly WeatherSystemServer serverWeather;

        private bool isShuttingDown = false;

        private const int chunkSize = 32;
        private int regionSize;

        internal float accum;

        public bool enabled = true;

        private IBulkBlockAccessor ba;

        private Thread rainUpdaterThread;

        private Block basicRainBlock;

        private UniqueQueue<UpdateRainChunk> updateRainQueue = new();
        public bool ProcessChunks = true;
        public WeatherSimulationRainWater(ICoreServerAPI sapi, WeatherSystemServer serverWeather)
        {
            this.sapi = sapi;
            this.serverWeather = serverWeather;

            ba = sapi.World.GetBlockAccessorBulkMinimalUpdate(true);
            initRandomShuffles();
            sapi.Event.SaveGameLoaded += Event_SaveGameLoaded;
            sapi.Event.ServerRunPhase(EnumServerRunPhase.Shutdown, () => isShuttingDown = true);
            sapi.Event.RegisterGameTickListener(OnServerTick3s, 3000);
            sapi.Event.RegisterGameTickListener(OnServerTick100ms, 100);

            var waterOneAsset = new AssetLocation("finitewater","finitewater-water-1");
            basicRainBlock = sapi.World.GetBlock(waterOneAsset);

            sapi.Event.ServerSuspend += Event_ServerSuspend;
            sapi.Event.ServerResume += () => shouldPauseThread = false;
            rainUpdaterThread = TyronThreadPool.CreateDedicatedThread(onThreadStart, "rainWaterUpdater");
        }

        private EnumSuspendState Event_ServerSuspend()
        {
            shouldPauseThread = true;
            if(isThreadPaused || !enabled)
            {
                return EnumSuspendState.Ready;
            }
            return EnumSuspendState.Wait;
        }

        private void OnServerTick100ms(float dt)
        {
            accum += dt;
            if (updateRainQueue.Count <= 0)
            {
                return;
            }

            accum = 0f;
            int count = 0;
            int max = 25;
            UpdateRainChunk[] q = new UpdateRainChunk[max];
            lock (updateRainQueue)
            {
                while (updateRainQueue.Count > 0)
                {
                    q[count] = updateRainQueue.Dequeue();
                    count++;
                    if (count >= max)
                    {
                        break;
                    }
                }
            }
            for (int i = 0; i < count; ++i)
            {
                var mc = sapi.WorldManager.GetMapChunk(q[i].Coords.X, q[i].Coords.Y);

                if (mc is not null)
                {
                    processBlockUpdates(mc, q[i]);
                }
            }
            ba.Commit();
        }


        internal void processBlockUpdates(IServerMapChunk mc, UpdateRainChunk updateChunk)
        {
            double lastRainAccumUpdateTotalHours = updateChunk.LastRainUpdateTotalHours;
            double timeNow = sapi.World.Calendar.TotalHours;
            
            if(Math.Abs(timeNow - lastRainAccumUpdateTotalHours) < 1.0)
            {
                return;
            }

            int chunkX = updateChunk.Coords.X;
            int chunkZ = updateChunk.Coords.Y;
            int regionX = chunkX * 32 / regionSize;
            int regionZ = chunkZ * 32 / regionSize;
            BlockPos pos = new BlockPos(sapi.World.DefaultSpawnPosition.Dimension);
            int[] posIndices = randomShuffles[sapi.World.Rand.Next(randomShuffles.Length)];
            int maxY = sapi.World.BlockAccessor.MapSizeY - 1;
            IWorldChunk chunk = null;

            //Is it raining?
            BlockPos chunkCorner = new BlockPos(chunkX * 32, 0, chunkZ * 32);
            var precipitationState = serverWeather.GetPrecipitationState(chunkCorner.ToVec3d());
            var region = serverWeather.getOrCreateWeatherSimForRegion(regionX, regionZ);
            if (precipitationState.Level <= 0.0f || region.weatherData.nowPrecType != EnumPrecipitationType.Rain)
            {
                return;
            }
            var rainSections = (int)Math.Ceiling(precipitationState.Level/100.0 * posIndices.Length);
            var usableIndices = posIndices.Take(Math.Min(rainSections, posIndices.Length)).ToArray();
            foreach (int posIndex in usableIndices)
            {
                //Heightmap is Z * 32 + X
                int posY = GameMath.Clamp(mc.RainHeightMap[posIndex] + 1, 0, maxY);
                //PosIndex = Z * 32 + X
                Vec2i XZPos = new();
                MapUtil.PosInt2d(posIndex, 32L, XZPos);
                pos.Set(chunkX * 32 + XZPos.X, posY, chunkZ * 32 + XZPos.Y);
                chunk = sapi.WorldManager.GetChunk(pos);
                if(chunk is null)
                {
                    continue;
                }
                //In theory this block should have air
                ba.SetBlock(basicRainBlock.Id, pos);
                basicRainBlock.OnBlockPlaced(sapi.World, pos);
            }

            //Update the last update time
            mc.SetModdata("lastRainAccumUpdateTotalHours", timeNow);
            mc.MarkDirty();
        }

        private void OnServerTick3s(float dt)
        {
            if(!ProcessChunks || !enabled)
            {
                return;
            }


            var allLoaded = sapi.WorldManager.AllLoadedMapchunks.ToArray();
            
            GameMath.Shuffle(sapi.World.Rand, allLoaded);

            foreach (var val in allLoaded)
            {
                var chunkCoord = sapi.WorldManager.MapChunkPosFromChunkIndex2D(val.Key);
                var lastUpdate = val.Value.GetModdata("lastRainAccumUpdateTotalHours", 0.0);
                var chunkData = new UpdateRainChunk
                {
                    Coords = chunkCoord,
                    LastRainUpdateTotalHours = lastUpdate
                };
                lock (updateRainQueue)
                {
                    updateRainQueue.Enqueue(chunkData);
                }
            }
        }

        private void Event_SaveGameLoaded()
        {
            regionSize = sapi.WorldManager.RegionSize;
            if (regionSize == 0) {
                sapi.Logger.Notification("Warning: region size was 0 for rain water system");
                regionSize = 16;
            }

            enabled = sapi.World.Config.GetBool("rainWaterEnabled", defaultValue: true);
            if(enabled)
            {
                //rainUpdaterThread.Start();
            }
        }


        private int[][] randomShuffles;
        private void initRandomShuffles()
        {
            randomShuffles = new int[50][];
            for (int i = 0; i < randomShuffles.Length; i++)
            {
                int[] coords = (randomShuffles[i] = new int[1024]);
                for (int j = 0; j < coords.Length; j++)
                {
                    coords[j] = j;
                }
                GameMath.Shuffle(sapi.World.Rand, coords);
            }
        }


        private bool shouldPauseThread = false;
        private bool isThreadPaused = false;

        private void onThreadStart()
        {
            FrameProfilerUtil FrameProfiler = new FrameProfilerUtil("[Thread RainWater]");

            while(!isShuttingDown)
            {
                Thread.Sleep(5);
                if(shouldPauseThread)
                {
                    isThreadPaused = true;
                    continue;
                }
                isThreadPaused = false;
                FrameProfiler.Begin(null);
                int i = 0;
                while(updateRainQueue.Count > 0 && i++ < 10)
                {
                    UpdateRainChunk q;
                    lock(updateRainQueue)
                    {
                        q = updateRainQueue.Dequeue();
                    }
                    var mc = sapi.WorldManager.GetMapChunk(q.Coords.X, q.Coords.Y);

                    if (mc is not null)
                    {
                        processBlockUpdates(mc, q);
                    }
                }
                ba.Commit();
                FrameProfiler.OffThreadEnd();
            }
        }
    }
}
