#if PTG_NO_CUSTOM_SYSTEM
// <copyright file="BuildingUpkeepSystemLoggingPatches.cs" company="algernon (K. Algernon A. Sheppard)">
// Copyright (c) algernon (K. Algernon A. Sheppard). All rights reserved.
// Licensed under the Apache Licence, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// See LICENSE.txt file in the project root for full license information.
// </copyright>

namespace PlopTheGrowables
{
    using System;
    using System.Reflection;
    using Colossal.Logging;
    using Game.Buildings;
    using Game.Prefabs;
    using Game.Simulation;
    using HarmonyLib;
    using Unity.Collections;
    using Unity.Entities;

    /// <summary>
    /// Logger-only Harmony patches for <see cref="BuildingUpkeepSystem"/> used by the no-custom-system investigation build.
    /// </summary>
    [HarmonyPatch]
    internal static class BuildingUpkeepSystemLoggingPatches
    {
        private const int DetailLimit = 16;
        private static readonly FieldInfo LevelupQueueField = AccessTools.Field(typeof(BuildingUpkeepSystem), "m_LevelupQueue");
        private static readonly FieldInfo LeveldownQueueField = AccessTools.Field(typeof(BuildingUpkeepSystem), "m_LeveldownQueue");
        private static FrameSnapshot CurrentFrame;

        /// <summary>
        /// Captures queued entities before vanilla processing.
        /// </summary>
        /// <param name="__instance">Target system instance.</param>
        [HarmonyPatch(typeof(BuildingUpkeepSystem), "OnUpdate")]
        [HarmonyPrefix]
        private static void OnUpdatePrefix(BuildingUpkeepSystem __instance)
        {
            NativeQueue<Entity> levelupQueue = GetQueue(LevelupQueueField, __instance);
            NativeQueue<Entity> leveldownQueue = GetQueue(LeveldownQueueField, __instance);
            int preLevelupCount = levelupQueue.Count;
            int preLeveldownCount = leveldownQueue.Count;
            EntityManager entityManager = __instance.World.EntityManager;
            CurrentFrame = new FrameSnapshot
            {
                LevelupPreCount = preLevelupCount,
                LeveldownPreCount = preLeveldownCount,
                LevelupSnapshots = CaptureSnapshots(levelupQueue, entityManager, "levelup"),
                LeveldownSnapshots = CaptureSnapshots(leveldownQueue, entityManager, "leveldown"),
            };

            if (preLevelupCount != 0 || preLeveldownCount != 0)
            {
                Patcher.Instance.Log.Info(CompatibilityProbeLog.Format("summary", $"system=BuildingUpkeepSystem, event=vanilla_queue_pre, levelup_queue={preLevelupCount}, leveldown_queue={preLeveldownCount}, traced_levelup={CurrentFrame.LevelupSnapshots.Length}, traced_leveldown={CurrentFrame.LeveldownSnapshots.Length}"));
            }
        }

        /// <summary>
        /// Logs queue counts after vanilla processing and the observed building state changes for the traced queue samples.
        /// </summary>
        /// <param name="__instance">Target system instance.</param>
        [HarmonyPatch(typeof(BuildingUpkeepSystem), "OnUpdate")]
        [HarmonyPostfix]
        private static void OnUpdatePostfix(BuildingUpkeepSystem __instance)
        {
            if (CurrentFrame is null)
            {
                return;
            }

            ILog log = Patcher.Instance.Log;
            EntityManager entityManager = __instance.World.EntityManager;
            int observedLevelupChanges = LogSnapshots(entityManager, CurrentFrame.LevelupSnapshots, "levelup");
            int observedLeveldownChanges = LogSnapshots(entityManager, CurrentFrame.LeveldownSnapshots, "leveldown");
            NativeQueue<Entity> levelupQueue = GetQueue(LevelupQueueField, __instance);
            NativeQueue<Entity> leveldownQueue = GetQueue(LeveldownQueueField, __instance);
            int postLevelupCount = levelupQueue.Count;
            int postLeveldownCount = leveldownQueue.Count;
            if (CurrentFrame.LevelupPreCount != 0 || CurrentFrame.LeveldownPreCount != 0 || postLevelupCount != 0 || postLeveldownCount != 0)
            {
                log.Info(CompatibilityProbeLog.Format("summary", $"system=BuildingUpkeepSystem, event=vanilla_queue_post, pre_levelup_queue={CurrentFrame.LevelupPreCount}, pre_leveldown_queue={CurrentFrame.LeveldownPreCount}, post_levelup_queue={postLevelupCount}, post_leveldown_queue={postLeveldownCount}, observed_levelup_changes={observedLevelupChanges}, observed_leveldown_changes={observedLeveldownChanges}"));
            }

            if ((CurrentFrame.LevelupPreCount == 0 && postLevelupCount != 0) || (CurrentFrame.LeveldownPreCount == 0 && postLeveldownCount != 0))
            {
                LogPostQueueSamples(entityManager, levelupQueue, "levelup");
                LogPostQueueSamples(entityManager, leveldownQueue, "leveldown");
            }

            CurrentFrame = null;
        }

        private static Snapshot[] CaptureSnapshots(NativeQueue<Entity> queue, EntityManager entityManager, string direction)
        {
            if (queue.Count == 0)
            {
                return Array.Empty<Snapshot>();
            }

            using NativeArray<Entity> queuedEntities = queue.ToArray(Allocator.Temp);
            int snapshotCount = Math.Min(queuedEntities.Length, DetailLimit);
            Snapshot[] snapshots = new Snapshot[snapshotCount];
            for (int i = 0; i < snapshotCount; i++)
            {
                Entity building = queuedEntities[i];
                Entity prefab = TryGetPrefab(entityManager, building);
                snapshots[i] = new Snapshot
                {
                    Direction = direction,
                    Building = building,
                    BeforePrefab = prefab,
                    BeforeLevel = TryGetLevel(entityManager, prefab),
                };
            }

            return snapshots;
        }

        private static int LogSnapshots(EntityManager entityManager, Snapshot[] snapshots, string direction)
        {
            int changedCount = 0;
            ILog log = Patcher.Instance.Log;
            foreach (Snapshot snapshot in snapshots)
            {
                bool entityExists = entityManager.Exists(snapshot.Building);
                Entity afterPrefab = entityExists ? TryGetPrefab(entityManager, snapshot.Building) : Entity.Null;
                int afterLevel = TryGetLevel(entityManager, afterPrefab);
                bool changed = snapshot.BeforePrefab != afterPrefab || snapshot.BeforeLevel != afterLevel;
                if (changed)
                {
                    changedCount++;
                }

                log.Info(CompatibilityProbeLog.Format("detail", $"system=BuildingUpkeepSystem, event=vanilla_queue_result, direction={direction}, building={CompatibilityProbeLog.FormatEntity(snapshot.Building)}, before_prefab={CompatibilityProbeLog.FormatEntity(snapshot.BeforePrefab)}, before_level={snapshot.BeforeLevel}, after_prefab={CompatibilityProbeLog.FormatEntity(afterPrefab)}, after_level={afterLevel}, changed={CompatibilityProbeLog.FormatBool(changed)}, entity_exists={CompatibilityProbeLog.FormatBool(entityExists)}"));
            }

            return changedCount;
        }

        private static void LogPostQueueSamples(EntityManager entityManager, NativeQueue<Entity> queue, string direction)
        {
            if (queue.Count == 0)
            {
                return;
            }

            using NativeArray<Entity> queuedEntities = queue.ToArray(Allocator.Temp);
            int sampleCount = Math.Min(queuedEntities.Length, DetailLimit);
            for (int i = 0; i < sampleCount; i++)
            {
                Entity building = queuedEntities[i];
                Entity prefab = TryGetPrefab(entityManager, building);
                int level = TryGetLevel(entityManager, prefab);
                Patcher.Instance.Log.Info(CompatibilityProbeLog.Format("detail", $"system=BuildingUpkeepSystem, event=vanilla_queue_after_update, direction={direction}, building={CompatibilityProbeLog.FormatEntity(building)}, current_prefab={CompatibilityProbeLog.FormatEntity(prefab)}, current_level={level}"));
            }
        }

        private static NativeQueue<Entity> GetQueue(FieldInfo field, BuildingUpkeepSystem instance)
        {
            if (field is null)
            {
                throw new MissingFieldException(typeof(BuildingUpkeepSystem).FullName, "m_LevelupQueue/m_LeveldownQueue");
            }

            return (NativeQueue<Entity>)field.GetValue(instance);
        }

        private static Entity TryGetPrefab(EntityManager entityManager, Entity building)
        {
            return entityManager.Exists(building) && entityManager.HasComponent<PrefabRef>(building)
                ? entityManager.GetComponentData<PrefabRef>(building).m_Prefab
                : Entity.Null;
        }

        private static int TryGetLevel(EntityManager entityManager, Entity prefab)
        {
            return prefab != Entity.Null && entityManager.HasComponent<SpawnableBuildingData>(prefab)
                ? entityManager.GetComponentData<SpawnableBuildingData>(prefab).m_Level
                : -1;
        }

        private sealed class FrameSnapshot
        {
            public int LevelupPreCount { get; set; }

            public int LeveldownPreCount { get; set; }

            public Snapshot[] LevelupSnapshots { get; set; }

            public Snapshot[] LeveldownSnapshots { get; set; }
        }

        private sealed class Snapshot
        {
            public string Direction { get; set; }

            public Entity Building { get; set; }

            public Entity BeforePrefab { get; set; }

            public int BeforeLevel { get; set; }
        }
    }
}
#endif