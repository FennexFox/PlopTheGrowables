#if PTG_NO_CUSTOM_SYSTEM
// <copyright file="VanillaLevellingObserverSystem.cs" company="algernon (K. Algernon A. Sheppard)">
// Copyright (c) algernon (K. Algernon A. Sheppard). All rights reserved.
// Licensed under the Apache Licence, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// See LICENSE.txt file in the project root for full license information.
// </copyright>

namespace PlopTheGrowables
{
    using System.Collections.Generic;
    using Game;
    using Game.Buildings;
    using Game.Common;
    using Game.Objects;
    using Game.Prefabs;
    using Game.Simulation;
    using Game.Tools;
    using Unity.Collections;
    using Unity.Entities;

    /// <summary>
    /// Investigation-only observer that logs actual growable prefab and level changes after vanilla simulation updates.
    /// </summary>
    public partial class VanillaLevellingObserverSystem : GameSystemBase
    {
        private const int DetailLimit = 16;
        private readonly Dictionary<Entity, ObservedState> _observedStates = new ();
        private readonly List<Entity> _staleEntities = new ();
        private EntityQuery _buildingQuery;
        private SimulationSystem _simulationSystem;

        /// <summary>
        /// Called when the system is created.
        /// </summary>
        protected override void OnCreate()
        {
            base.OnCreate();
            _simulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            _buildingQuery = SystemAPI.QueryBuilder().WithAll<Building, PrefabRef>().WithAny<ResidentialProperty, IndustrialProperty, CommercialProperty>().WithNone<Deleted, Temp, UnderConstruction, Signature>().Build();
            RequireForUpdate(_buildingQuery);
        }

        /// <summary>
        /// Called every update.
        /// </summary>
        protected override void OnUpdate()
        {
            using NativeArray<Entity> entities = _buildingQuery.ToEntityArray(Allocator.Temp);
            ComponentLookup<PrefabRef> prefabLookup = SystemAPI.GetComponentLookup<PrefabRef>(true);
            ComponentLookup<SpawnableBuildingData> spawnableBuildingLookup = SystemAPI.GetComponentLookup<SpawnableBuildingData>(true);
            ComponentLookup<SpawnedBuilding> spawnedBuildingLookup = SystemAPI.GetComponentLookup<SpawnedBuilding>(true);
            ComponentLookup<PloppedBuilding> ploppedBuildingLookup = SystemAPI.GetComponentLookup<PloppedBuilding>(true);

            int changedCount = 0;
            int levelupCount = 0;
            int leveldownCount = 0;
            int prefabSwapCount = 0;
            int detailCount = 0;
            uint frame = _simulationSystem.frameIndex;

            foreach (Entity entity in entities)
            {
                Entity prefab = prefabLookup[entity].m_Prefab;
                int level = TryGetLevel(spawnableBuildingLookup, prefab);
                ObservedState currentState = new ObservedState
                {
                    Prefab = prefab,
                    Level = level,
                };

                if (_observedStates.TryGetValue(entity, out ObservedState previousState) && (previousState.Prefab != currentState.Prefab || previousState.Level != currentState.Level))
                {
                    string direction = ClassifyChange(previousState, currentState);
                    changedCount++;
                    if (direction == "levelup")
                    {
                        levelupCount++;
                    }
                    else if (direction == "leveldown")
                    {
                        leveldownCount++;
                    }
                    else
                    {
                        prefabSwapCount++;
                    }

                    if (detailCount < DetailLimit)
                    {
                        Mod.Instance.Log.Info(CompatibilityProbeLog.Format("detail", $"system=VanillaLevellingObserverSystem, event=observed_level_change, frame={frame}, direction={direction}, building={CompatibilityProbeLog.FormatEntity(entity)}, before_prefab={CompatibilityProbeLog.FormatEntity(previousState.Prefab)}, before_level={previousState.Level}, after_prefab={CompatibilityProbeLog.FormatEntity(currentState.Prefab)}, after_level={currentState.Level}, spawned={CompatibilityProbeLog.FormatBool(spawnedBuildingLookup.HasComponent(entity))}, plopped={CompatibilityProbeLog.FormatBool(ploppedBuildingLookup.HasComponent(entity))}"));
                        detailCount++;
                    }
                }

                _observedStates[entity] = currentState;
            }

            if (changedCount > 0)
            {
                Mod.Instance.Log.Info(CompatibilityProbeLog.Format("summary", $"system=VanillaLevellingObserverSystem, event=observed_level_changes, frame={frame}, changed={changedCount}, levelup={levelupCount}, leveldown={leveldownCount}, prefab_swap={prefabSwapCount}, logged_details={detailCount}"));
            }

            if ((frame & 255U) == 0U)
            {
                PruneMissingEntities();
            }
        }

        /// <summary>
        /// Called when the system is destroyed.
        /// </summary>
        protected override void OnDestroy()
        {
            _observedStates.Clear();
            _staleEntities.Clear();
            base.OnDestroy();
        }

        private static string ClassifyChange(ObservedState previousState, ObservedState currentState)
        {
            if (previousState.Level >= 0 && currentState.Level >= 0)
            {
                if (currentState.Level > previousState.Level)
                {
                    return "levelup";
                }

                if (currentState.Level < previousState.Level)
                {
                    return "leveldown";
                }
            }

            return "prefab_swap";
        }

        private static int TryGetLevel(ComponentLookup<SpawnableBuildingData> spawnableBuildingLookup, Entity prefab)
        {
            return prefab != Entity.Null && spawnableBuildingLookup.HasComponent(prefab)
                ? spawnableBuildingLookup[prefab].m_Level
                : -1;
        }

        private void PruneMissingEntities()
        {
            _staleEntities.Clear();
            foreach (Entity entity in _observedStates.Keys)
            {
                if (!EntityManager.Exists(entity))
                {
                    _staleEntities.Add(entity);
                }
            }

            foreach (Entity entity in _staleEntities)
            {
                _observedStates.Remove(entity);
            }
        }

        private struct ObservedState
        {
            public Entity Prefab { get; set; }

            public int Level { get; set; }
        }
    }
}
#endif