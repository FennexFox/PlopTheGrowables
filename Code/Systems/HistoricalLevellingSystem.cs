// <copyright file="HistoricalLevellingSystem.cs" company="algernon (K. Algernon A. Sheppard)">
// Copyright (c) algernon (K. Algernon A. Sheppard). All rights reserved.
// Licensed under the Apache Licence, Version 2.0 (the "License"); you may not use this file except in compliance with the License.
// See LICENSE.txt file in the project root for full license information.
// </copyright>

namespace PlopTheGrowables
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Reflection;
    using Colossal.Collections;
    using Colossal.Mathematics;
    using Game;
    using Game.Buildings;
    using Game.Common;
    using Game.Notifications;
    using Game.Objects;
    using Game.Prefabs;
    using Game.Simulation;
    using Game.Triggers;
    using Game.Zones;
    using HarmonyLib;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;
    using Unity.Jobs;
    using Unity.Mathematics;
    using BuildingFlags = Game.Prefabs.BuildingFlags;

    /// <summary>
    /// Custom building levelling system to implement optional level locking for growables.
    /// </summary>
    public partial class HistoricalLevellingSystem : GameSystemBase
    {
        private const int ProbeDetailLimit = 32;

        // Building levelling queues.
        private NativeQueue<Entity> _levelupQueue;
        private NativeQueue<Entity> _leveldownQueue;
        private NativeQueue<ProbeRecord> _probeQueue;

        // System references.
        private SimulationSystem _simulationSystem;
        private TriggerSystem _triggerSystem;
        private Game.Zones.SearchSystem _zoneSearchSystem;
        private ZoneBuiltRequirementSystem _zoneBuiltRequirementSystem;
        private IconCommandSystem _iconCommandSystem;
        private ElectricityRoadConnectionGraphSystem _electricityRoadConnectionGraphSystem;
        private WaterPipeRoadConnectionGraphSystem _waterPipeRoadConnectionGraphSystem;

        // Queries.
        private EntityQuery _buildingPrefabGroupQuery;
        private EntityQuery _buildingSettingsQuery;

        // Frame barrier.
        private EndFrameBarrier _endFrameBarrier;

        /// <summary>
        /// Gets the active instance.
        /// </summary>
        public static HistoricalLevellingSystem Instance { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether building level changes should be disabled.
        /// </summary>
        public bool DisableLevelling { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether building abandonment should be prevented.
        /// </summary>
        public bool DisableAbandonment { get; set; } = false;

        /// <summary>
        /// Gets or sets a value indicating whether the game level-up household check should be disabled.
        /// This check prevents a residential building levelling up to a building with higher household capacity than the current building.
        /// Disabling this check is required for campatibility with mods that alter household counts, such as 'Realistic Workplaces and Households'.
        /// </summary>
        public bool IgnoreHouseholdCount { get; set; } = false;

        private enum ProbeDirection : byte
        {
            Levelup = 1,
            Leveldown = 2,
        }

        private enum ProbeDecisionKind : byte
        {
            Dequeued = 1,
            Skip = 2,
            Success = 3,
        }

        private enum ProbeReason : byte
        {
            None = 0,
            LevelLocked = 1,
            NotSpawnable = 2,
            ZoneDisabled = 3,
            SelectSpawnableFailed = 4,
            DisableLevellingGlobal = 5,
            DisableAbandonmentGlobal = 6,
            Other = 7,
        }

        private enum ProbeAreaClass : byte
        {
            Unknown = 0,
            Residential = 1,
            Commercial = 2,
            Industrial = 3,
            Office = 4,
        }

        private enum ProbeCandidateFilterStage : byte
        {
            None = 0,
            ZoneMatch = 1,
            LevelMatch = 2,
            LotMatch = 3,
            HeightMatch = 4,
            AccessMatch = 5,
            HouseholdOrPropertyMatch = 6,
            AllowedManufacturedMatch = 7,
            AllowedSoldMatch = 8,
            AllowedStoredMatch = 9,
        }

        private struct ProbeCandidateSample
        {
            public Entity m_Prefab;
            public int2 m_LotSize;
            public float m_Height;
            public BuildingFlags m_AccessFlags;
            public ProbeCandidateFilterStage m_RejectStage;
        }

        private struct ProbeCandidateSamples
        {
            public byte m_Count;
            public ProbeCandidateSample m_Sample1;
            public ProbeCandidateSample m_Sample2;
            public ProbeCandidateSample m_Sample3;
        }

        private struct SelectionTrace
        {
            public int m_CandidateCountZoneMatch;
            public int m_CandidateCountLevelMatch;
            public int m_CandidateCountLotMatch;
            public int m_CandidateCountHeightMatch;
            public int m_CandidateCountAccessMatch;
            public int m_CandidateCountHouseholdOrPropertyMatch;
            public int m_CandidateCountAllowedManufacturedMatch;
            public int m_CandidateCountAllowedSoldMatch;
            public int m_CandidateCountAllowedStoredMatch;
            public ProbeCandidateSamples m_LevelRejectSamples;
            public ProbeCandidateSamples m_LotRejectSamples;
            public ProbeCandidateSamples m_HeightRejectSamples;
            public ProbeCandidateSamples m_AccessRejectSamples;
            public ProbeCandidateSamples m_HouseholdOrPropertyRejectSamples;
            public ProbeCandidateSamples m_AllowedManufacturedRejectSamples;
            public ProbeCandidateSamples m_AllowedSoldRejectSamples;
            public ProbeCandidateSamples m_AllowedStoredRejectSamples;
        }

        private struct ProbeRecord
        {
            public ProbeDirection m_Direction;
            public ProbeDecisionKind m_DecisionKind;
            public ProbeReason m_Reason;
            public ProbeAreaClass m_AreaClass;
            public ProbeCandidateFilterStage m_CandidateZeroStage;
            public Entity m_Building;
            public Entity m_CurrentPrefab;
            public Entity m_SelectedNextPrefab;
            public int m_CurrentLevel;
            public int m_TargetLevel;
            public byte m_Spawned;
            public byte m_Plopped;
            public byte m_Signature;
            public byte m_LevelLocked;
            public byte m_PropertyOnMarket;
            public byte m_PropertyToBeOnMarket;
            public byte m_UnderConstruction;
            public byte m_SelectFailedNoCandidate;
            public byte m_HasSelectionBreakdown;
            public byte m_IsDetail;
            public int m_RenterCount;
            public ZoneType m_ZoneType;
            public int2 m_LotSize;
            public float m_MaxHeight;
            public BuildingFlags m_AccessFlags;
            public int m_CandidateCountZoneMatch;
            public int m_CandidateCountLevelMatch;
            public int m_CandidateCountLotMatch;
            public int m_CandidateCountHeightMatch;
            public int m_CandidateCountAccessMatch;
            public int m_CandidateCountHouseholdOrPropertyMatch;
            public int m_CandidateCountAllowedManufacturedMatch;
            public int m_CandidateCountAllowedSoldMatch;
            public int m_CandidateCountAllowedStoredMatch;
            public int m_CandidateCountFinal;
            public ProbeCandidateSample m_SelectionSample1;
            public ProbeCandidateSample m_SelectionSample2;
            public ProbeCandidateSample m_SelectionSample3;
        }

        private struct ProbeCounters
        {
            public int m_Processed;
            public int m_Success;
            public int m_LevelLocked;
            public int m_NotSpawnable;
            public int m_ZoneDisabled;
            public int m_SelectSpawnableFailed;
            public int m_DisableLevellingGlobal;
            public int m_DisableAbandonmentGlobal;
            public int m_Other;
        }

        /// <summary>
        /// Updates the active level up and level down queues to the provided values.
        /// </summary>
        /// <param name="levelUpQueue">Level up queue to set.</param>
        /// <param name="levelDownQueue">Level down queue to set.</param>
        internal void SetLevelQueues(NativeQueue<Entity> levelUpQueue, NativeQueue<Entity> levelDownQueue)
        {
            Mod.Instance.Log.Info("Updating level queues");
            _levelupQueue = levelUpQueue;
            _leveldownQueue = levelDownQueue;
        }

        /// <summary>
        /// Called when the system is created.
        /// </summary>
        protected override void OnCreate()
        {
            Instance = this;

            base.OnCreate();

            // Set references.
            _simulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            _triggerSystem = World.GetOrCreateSystemManaged<TriggerSystem>();
            _zoneSearchSystem = World.GetOrCreateSystemManaged<Game.Zones.SearchSystem>();
            _zoneBuiltRequirementSystem = World.GetOrCreateSystemManaged<ZoneBuiltRequirementSystem>();
            _iconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
            _electricityRoadConnectionGraphSystem = World.GetOrCreateSystemManaged<ElectricityRoadConnectionGraphSystem>();
            _waterPipeRoadConnectionGraphSystem = World.GetOrCreateSystemManaged<WaterPipeRoadConnectionGraphSystem>();
            _buildingPrefabGroupQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingData>(), ComponentType.ReadOnly<BuildingSpawnGroupData>(), ComponentType.ReadOnly<PrefabData>());
            _buildingSettingsQuery = GetEntityQuery(ComponentType.ReadOnly<BuildingConfigurationData>());
            _endFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            _probeQueue = new NativeQueue<ProbeRecord>(Allocator.Persistent);
            RequireForUpdate(_buildingSettingsQuery);

            // Reflect level up queue.
            FieldInfo m_LevelupQueueField = AccessTools.Field(typeof(BuildingUpkeepSystem), "m_LevelupQueue");
            if (m_LevelupQueueField is null)
            {
                Mod.Instance.Log.Error("Unable to get LevelupQueue FieldInfo");
                Enabled = false;
                return;
            }

            _levelupQueue = (NativeQueue<Entity>)m_LevelupQueueField.GetValue(World.GetOrCreateSystemManaged<BuildingUpkeepSystem>());

            // Reflect level down queue.
            FieldInfo m_LeveldownQueueField = AccessTools.Field(typeof(BuildingUpkeepSystem), "m_LeveldownQueue");
            if (m_LeveldownQueueField is null)
            {
                Mod.Instance.Log.Error("Unable to get LeveldownQueue FieldInfo");
                Enabled = false;
                return;
            }

            _leveldownQueue = (NativeQueue<Entity>)m_LeveldownQueueField.GetValue(World.GetOrCreateSystemManaged<BuildingUpkeepSystem>());

            // Set state from current settings.
            if (Mod.Instance.ActiveSettings is ModSettings activeSettings)
            {
                DisableLevelling = activeSettings.DisableLevelling;
                DisableAbandonment = activeSettings.NoAbandonment;
            }
        }

        /// <summary>
        /// Called every update.
        /// </summary>
        protected override void OnUpdate()
        {
            int levelupQueueCount = _levelupQueue.Count;
            int leveldownQueueCount = _leveldownQueue.Count;
            bool hasLevelupWork = levelupQueueCount != 0;
            bool hasLeveldownWork = leveldownQueueCount != 0;
            if (!hasLevelupWork && !hasLeveldownWork)
            {
                return;
            }

            ClearProbeQueue();

            if (DisableLevelling)
            {
                ProbeCounters levelupCounters = default;
                ProbeCounters leveldownCounters = default;
                levelupCounters.m_Processed = levelupQueueCount;
                levelupCounters.m_DisableLevellingGlobal = levelupQueueCount;
                leveldownCounters.m_Processed = leveldownQueueCount;
                leveldownCounters.m_DisableLevellingGlobal = leveldownQueueCount;
                LogHistoricalSummary(_simulationSystem.frameIndex, levelupQueueCount, leveldownQueueCount, levelupCounters, leveldownCounters);
                _levelupQueue.Clear();
                _leveldownQueue.Clear();
                return;
            }

            bool scheduledWork = false;

            if (hasLevelupWork)
            {
                LevelupJob levelupJob = default;
                levelupJob.m_IgnoreHouseholdCount = IgnoreHouseholdCount;
                levelupJob.m_ProbeDetailLimit = ProbeDetailLimit;
                levelupJob.m_LevelLockedData = SystemAPI.GetComponentLookup<LevelLocked>(true);
                levelupJob.m_EntityType = SystemAPI.GetEntityTypeHandle();
                levelupJob.m_SpawnableBuildingType = SystemAPI.GetComponentTypeHandle<SpawnableBuildingData>(true);
                levelupJob.m_BuildingType = SystemAPI.GetComponentTypeHandle<BuildingData>(true);
                levelupJob.m_BuildingPropertyType = SystemAPI.GetComponentTypeHandle<BuildingPropertyData>(true);
                levelupJob.m_ObjectGeometryType = SystemAPI.GetComponentTypeHandle<ObjectGeometryData>(true);
                levelupJob.m_BuildingSpawnGroupType = SystemAPI.GetSharedComponentTypeHandle<BuildingSpawnGroupData>();
                levelupJob.m_TransformData = SystemAPI.GetComponentLookup<Game.Objects.Transform>(true);
                levelupJob.m_BlockData = SystemAPI.GetComponentLookup<Block>(true);
                levelupJob.m_ValidAreaData = SystemAPI.GetComponentLookup<ValidArea>(true);
                levelupJob.m_Prefabs = SystemAPI.GetComponentLookup<PrefabRef>(true);
                levelupJob.m_PrefabDatas = SystemAPI.GetComponentLookup<PrefabData>(true);
                levelupJob.m_SpawnableBuildings = SystemAPI.GetComponentLookup<SpawnableBuildingData>(true);
                levelupJob.m_Buildings = SystemAPI.GetComponentLookup<BuildingData>(true);
                levelupJob.m_BuildingPropertyDatas = SystemAPI.GetComponentLookup<BuildingPropertyData>(true);
                levelupJob.m_OfficeBuilding = SystemAPI.GetComponentLookup<OfficeBuilding>(true);
                levelupJob.m_ZoneData = SystemAPI.GetComponentLookup<ZoneData>(true);
                levelupJob.m_SpawnedBuildings = SystemAPI.GetComponentLookup<SpawnedBuilding>(true);
                levelupJob.m_PloppedBuildings = SystemAPI.GetComponentLookup<PloppedBuilding>(true);
                levelupJob.m_SignatureBuildings = SystemAPI.GetComponentLookup<Signature>(true);
                levelupJob.m_PropertyOnMarket = SystemAPI.GetComponentLookup<PropertyOnMarket>(true);
                levelupJob.m_PropertyToBeOnMarket = SystemAPI.GetComponentLookup<PropertyToBeOnMarket>(true);
                levelupJob.m_UnderConstructionData = SystemAPI.GetComponentLookup<UnderConstruction>(true);
                levelupJob.m_Renters = SystemAPI.GetBufferLookup<Renter>(true);
                levelupJob.m_Cells = SystemAPI.GetBufferLookup<Cell>(true);
                levelupJob.m_BuildingConfigurationData = _buildingSettingsQuery.GetSingleton<BuildingConfigurationData>();
                levelupJob.m_SpawnableBuildingChunks = _buildingPrefabGroupQuery.ToArchetypeChunkListAsync(World.UpdateAllocator.ToAllocator, out _);
                levelupJob.m_ZoneSearchTree = _zoneSearchSystem.GetSearchTree(readOnly: true, out _);
                levelupJob.m_RandomSeed = RandomSeed.Next();
                levelupJob.m_IconCommandBuffer = _iconCommandSystem.CreateCommandBuffer();
                levelupJob.m_LevelupQueue = _levelupQueue;
                levelupJob.m_ProbeQueue = _probeQueue;
                levelupJob.m_CommandBuffer = _endFrameBarrier.CreateCommandBuffer();
                levelupJob.m_TriggerBuffer = _triggerSystem.CreateActionBuffer();
                levelupJob.m_ZoneBuiltLevelQueue = _zoneBuiltRequirementSystem.GetZoneBuiltLevelQueue(out _);
                JobHandle jobHandle = IJobExtensions.Schedule(levelupJob, Dependency);
                _zoneSearchSystem.AddSearchTreeReader(jobHandle);
                _zoneBuiltRequirementSystem.AddWriter(jobHandle);
                _endFrameBarrier.AddJobHandleForProducer(jobHandle);
                _triggerSystem.AddActionBufferWriter(jobHandle);
                Dependency = jobHandle;
                scheduledWork = true;
            }

            if (hasLeveldownWork)
            {
                LeveldownJob leveldownJob = default;
                leveldownJob.m_DisableAbandonment = DisableAbandonment;
                leveldownJob.m_LevelLockedData = SystemAPI.GetComponentLookup<LevelLocked>(true);
                leveldownJob.m_BuildingDatas = __TypeHandle.__Game_Prefabs_BuildingData_RO_ComponentLookup;
                leveldownJob.m_Prefabs = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup;
                leveldownJob.m_SpawnableBuildings = SystemAPI.GetComponentLookup<SpawnableBuildingData>(true);
                leveldownJob.m_Buildings = SystemAPI.GetComponentLookup<Building>(false);
                leveldownJob.m_ElectricityConsumers = SystemAPI.GetComponentLookup<ElectricityConsumer>(true);
                leveldownJob.m_GarbageProducers = SystemAPI.GetComponentLookup<GarbageProducer>(true);
                leveldownJob.m_MailProducers = SystemAPI.GetComponentLookup<MailProducer>(true);
                leveldownJob.m_WaterConsumers = SystemAPI.GetComponentLookup<WaterConsumer>(true);
                leveldownJob.m_BuildingPropertyDatas = __TypeHandle.__Game_Prefabs_BuildingPropertyData_RO_ComponentLookup;
                leveldownJob.m_OfficeBuilding = __TypeHandle.__Game_Prefabs_OfficeBuilding_RO_ComponentLookup;
                leveldownJob.m_TriggerBuffer = _triggerSystem.CreateActionBuffer();
                leveldownJob.m_CrimeProducers = SystemAPI.GetComponentLookup<CrimeProducer>(false);
                leveldownJob.m_Renters = SystemAPI.GetBufferLookup<Renter>(false);
                leveldownJob.m_BuildingConfigurationData = _buildingSettingsQuery.GetSingleton<BuildingConfigurationData>();
                leveldownJob.m_LeveldownQueue = _leveldownQueue;
                leveldownJob.m_ProbeQueue = _probeQueue;
                leveldownJob.m_CommandBuffer = _endFrameBarrier.CreateCommandBuffer();
                leveldownJob.m_UpdatedElectricityRoadEdges = _electricityRoadConnectionGraphSystem.GetEdgeUpdateQueue(out _);
                leveldownJob.m_UpdatedWaterPipeRoadEdges = _waterPipeRoadConnectionGraphSystem.GetEdgeUpdateQueue(out _);
                leveldownJob.m_IconCommandBuffer = _iconCommandSystem.CreateCommandBuffer();
                leveldownJob.m_SimulationFrame = _simulationSystem.frameIndex;
                JobHandle jobHandle = IJobExtensions.Schedule(leveldownJob, Dependency);
                _endFrameBarrier.AddJobHandleForProducer(jobHandle);
                _electricityRoadConnectionGraphSystem.AddQueueWriter(jobHandle);
                _iconCommandSystem.AddCommandBufferWriter(jobHandle);
                _triggerSystem.AddActionBufferWriter(jobHandle);
                Dependency = jobHandle;
                scheduledWork = true;
            }

            if (scheduledWork)
            {
                Dependency.Complete();
                LogHistoricalProbeRecords(_simulationSystem.frameIndex, levelupQueueCount, leveldownQueueCount);
            }
        }

        /// <summary>
        /// Called when the system is destroyed.
        /// </summary>
        protected override void OnDestroy()
        {
            Instance = null;

            // The level up and level down queues belong to PropertyRenterSystem, so we don't dispose of them here.
            if (_probeQueue.IsCreated)
            {
                _probeQueue.Dispose();
            }

            base.OnDestroy();
        }

        /// <summary>
        /// Job to level up buildings.
        /// Derived from game code.
        /// </summary>
        [BurstCompile]
        private struct LevelupJob : IJob
        {
            [ReadOnly]
            public bool m_IgnoreHouseholdCount;
            [ReadOnly]
            public int m_ProbeDetailLimit;
            [ReadOnly]
            public ComponentLookup<LevelLocked> m_LevelLockedData;
            [ReadOnly]
            public EntityTypeHandle m_EntityType;
            [ReadOnly]
            public ComponentTypeHandle<SpawnableBuildingData> m_SpawnableBuildingType;
            [ReadOnly]
            public ComponentTypeHandle<BuildingData> m_BuildingType;
            [ReadOnly]
            public ComponentTypeHandle<BuildingPropertyData> m_BuildingPropertyType;
            [ReadOnly]
            public ComponentTypeHandle<ObjectGeometryData> m_ObjectGeometryType;
            [ReadOnly]
            public SharedComponentTypeHandle<BuildingSpawnGroupData> m_BuildingSpawnGroupType;
            [ReadOnly]
            public ComponentLookup<Game.Objects.Transform> m_TransformData;
            [ReadOnly]
            public ComponentLookup<Block> m_BlockData;
            [ReadOnly]
            public ComponentLookup<ValidArea> m_ValidAreaData;
            [ReadOnly]
            public ComponentLookup<PrefabRef> m_Prefabs;
            [ReadOnly]
            public ComponentLookup<PrefabData> m_PrefabDatas;
            [ReadOnly]
            public ComponentLookup<SpawnableBuildingData> m_SpawnableBuildings;
            [ReadOnly]
            public ComponentLookup<BuildingData> m_Buildings;
            [ReadOnly]
            public ComponentLookup<BuildingPropertyData> m_BuildingPropertyDatas;
            [ReadOnly]
            public ComponentLookup<OfficeBuilding> m_OfficeBuilding;
            [ReadOnly]
            public ComponentLookup<ZoneData> m_ZoneData;
            [ReadOnly]
            public ComponentLookup<SpawnedBuilding> m_SpawnedBuildings;
            [ReadOnly]
            public ComponentLookup<PloppedBuilding> m_PloppedBuildings;
            [ReadOnly]
            public ComponentLookup<Signature> m_SignatureBuildings;
            [ReadOnly]
            public ComponentLookup<PropertyOnMarket> m_PropertyOnMarket;
            [ReadOnly]
            public ComponentLookup<PropertyToBeOnMarket> m_PropertyToBeOnMarket;
            [ReadOnly]
            public ComponentLookup<UnderConstruction> m_UnderConstructionData;
            [ReadOnly]
            public BufferLookup<Renter> m_Renters;
            [ReadOnly]
            public BufferLookup<Cell> m_Cells;
            public BuildingConfigurationData m_BuildingConfigurationData;
            [ReadOnly]
            public NativeList<ArchetypeChunk> m_SpawnableBuildingChunks;
            [ReadOnly]
            public NativeQuadTree<Entity, Bounds2> m_ZoneSearchTree;
            [ReadOnly]
            public RandomSeed m_RandomSeed;
            public IconCommandBuffer m_IconCommandBuffer;
            public NativeQueue<Entity> m_LevelupQueue;
            public NativeQueue<ProbeRecord> m_ProbeQueue;
            public EntityCommandBuffer m_CommandBuffer;
            public NativeQueue<TriggerAction> m_TriggerBuffer;
            public NativeQueue<ZoneBuiltLevelUpdate> m_ZoneBuiltLevelQueue;

            /// <summary>
            /// Job execution.
            /// </summary>
            public void Execute()
            {
                Random random = m_RandomSeed.GetRandom(0);
                int detailCount = 0;
                while (m_LevelupQueue.TryDequeue(out Entity item))
                {
                    bool isDetail = detailCount < m_ProbeDetailLimit;
                    ProbeRecord probeRecord = CreateProbeRecord(item, isDetail);
                    Entity prefab = m_Prefabs[item].m_Prefab;
                    probeRecord.m_CurrentPrefab = prefab;
                    if (isDetail)
                    {
                        detailCount++;
                    }

                    if (!m_SpawnableBuildings.HasComponent(prefab))
                    {
                        probeRecord.m_DecisionKind = ProbeDecisionKind.Skip;
                        probeRecord.m_Reason = ProbeReason.NotSpawnable;
                        m_ProbeQueue.Enqueue(probeRecord);
                        continue;
                    }

                    if (m_LevelLockedData.HasComponent(item))
                    {
                        probeRecord.m_DecisionKind = ProbeDecisionKind.Skip;
                        probeRecord.m_Reason = ProbeReason.LevelLocked;
                        m_ProbeQueue.Enqueue(probeRecord);
                        continue;
                    }

                    SpawnableBuildingData spawnableBuildingData = m_SpawnableBuildings[prefab];
                    probeRecord.m_CurrentLevel = spawnableBuildingData.m_Level;
                    probeRecord.m_AreaClass = GetAreaClass(m_BuildingPropertyDatas[prefab], prefab);
                    if (!m_PrefabDatas.IsComponentEnabled(spawnableBuildingData.m_ZonePrefab))
                    {
                        probeRecord.m_DecisionKind = ProbeDecisionKind.Skip;
                        probeRecord.m_Reason = ProbeReason.ZoneDisabled;
                        m_ProbeQueue.Enqueue(probeRecord);
                        continue;
                    }

                    BuildingData prefabBuildingData = m_Buildings[prefab];
                    BuildingPropertyData buildingPropertyData = m_BuildingPropertyDatas[prefab];
                    ZoneData zoneData = m_ZoneData[spawnableBuildingData.m_ZonePrefab];
                    float maxHeight = GetMaxHeight(item, prefabBuildingData);
                    int targetLevel = spawnableBuildingData.m_Level + 1;
                    bool captureSelectionTrace = isDetail || IsPrioritySelectionProbe(probeRecord.m_AreaClass, targetLevel);
                    Entity entity = SelectSpawnableBuilding(zoneData.m_ZoneType, targetLevel, prefabBuildingData.m_LotSize, maxHeight, prefabBuildingData.m_Flags & (BuildingFlags.LeftAccess | BuildingFlags.RightAccess), buildingPropertyData, captureSelectionTrace, ref probeRecord, ref random);

                    if (entity == Entity.Null)
                    {
                        probeRecord.m_DecisionKind = ProbeDecisionKind.Skip;
                        probeRecord.m_Reason = ProbeReason.SelectSpawnableFailed;
                        probeRecord.m_SelectFailedNoCandidate = 1;
                        m_ProbeQueue.Enqueue(probeRecord);
                        continue;
                    }

                    probeRecord.m_DecisionKind = ProbeDecisionKind.Success;
                    probeRecord.m_SelectedNextPrefab = entity;
                    m_ProbeQueue.Enqueue(probeRecord);

                    m_CommandBuffer.AddComponent(item, new UnderConstruction
                    {
                        m_NewPrefab = entity,
                        m_Progress = byte.MaxValue,
                    });

                    if (buildingPropertyData.CountProperties(AreaType.Residential) > 0)
                    {
                        m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelUpResidentialBuilding, Entity.Null, item, item));
                    }

                    if (buildingPropertyData.CountProperties(AreaType.Commercial) > 0)
                    {
                        m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelUpCommercialBuilding, Entity.Null, item, item));
                    }

                    if (buildingPropertyData.CountProperties(AreaType.Industrial) > 0)
                    {
                        if (m_OfficeBuilding.HasComponent(prefab))
                        {
                            m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelUpOfficeBuilding, Entity.Null, item, item));
                        }
                        else
                        {
                            m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelUpIndustrialBuilding, Entity.Null, item, item));
                        }
                    }

                    m_ZoneBuiltLevelQueue.Enqueue(new ZoneBuiltLevelUpdate
                    {
                        m_Zone = spawnableBuildingData.m_ZonePrefab,
                        m_FromLevel = spawnableBuildingData.m_Level,
                        m_ToLevel = spawnableBuildingData.m_Level + 1,
                        m_Squares = prefabBuildingData.m_LotSize.x * prefabBuildingData.m_LotSize.y,
                    });

                    m_IconCommandBuffer.Add(item, m_BuildingConfigurationData.m_LevelUpNotification, IconPriority.Info, IconClusterLayer.Transaction);
                }
            }

            private ProbeRecord CreateProbeRecord(Entity building, bool isDetail)
            {
                ProbeRecord probeRecord = default;
                probeRecord.m_Direction = ProbeDirection.Levelup;
                probeRecord.m_DecisionKind = ProbeDecisionKind.Dequeued;
                probeRecord.m_Reason = ProbeReason.None;
                probeRecord.m_AreaClass = ProbeAreaClass.Unknown;
                probeRecord.m_CandidateZeroStage = ProbeCandidateFilterStage.None;
                probeRecord.m_Building = building;
                probeRecord.m_CurrentPrefab = m_Prefabs[building].m_Prefab;
                probeRecord.m_TargetLevel = -1;
                probeRecord.m_CandidateCountFinal = -1;
                probeRecord.m_IsDetail = (byte)(isDetail ? 1 : 0);
                if (!isDetail)
                {
                    return probeRecord;
                }

                probeRecord.m_Spawned = (byte)(m_SpawnedBuildings.HasComponent(building) ? 1 : 0);
                probeRecord.m_Plopped = (byte)(m_PloppedBuildings.HasComponent(building) ? 1 : 0);
                probeRecord.m_Signature = (byte)(m_SignatureBuildings.HasComponent(building) ? 1 : 0);
                probeRecord.m_LevelLocked = (byte)(m_LevelLockedData.HasComponent(building) ? 1 : 0);
                probeRecord.m_PropertyOnMarket = (byte)(m_PropertyOnMarket.HasComponent(building) ? 1 : 0);
                probeRecord.m_PropertyToBeOnMarket = (byte)(m_PropertyToBeOnMarket.HasComponent(building) ? 1 : 0);
                probeRecord.m_UnderConstruction = (byte)(m_UnderConstructionData.HasComponent(building) ? 1 : 0);
                probeRecord.m_RenterCount = m_Renters.HasBuffer(building) ? m_Renters[building].Length : 0;
                return probeRecord;
            }

            private ProbeAreaClass GetAreaClass(BuildingPropertyData buildingPropertyData, Entity prefab)
            {
                if (buildingPropertyData.CountProperties(AreaType.Residential) > 0)
                {
                    return ProbeAreaClass.Residential;
                }

                if (buildingPropertyData.CountProperties(AreaType.Commercial) > 0)
                {
                    return ProbeAreaClass.Commercial;
                }

                if (buildingPropertyData.CountProperties(AreaType.Industrial) > 0)
                {
                    return m_OfficeBuilding.HasComponent(prefab) ? ProbeAreaClass.Office : ProbeAreaClass.Industrial;
                }

                return ProbeAreaClass.Unknown;
            }

            private static bool IsPrioritySelectionProbe(ProbeAreaClass areaClass, int targetLevel) => areaClass == ProbeAreaClass.Office && targetLevel == 5;

            /// <summary>
            /// Selects a building to spawn.
            /// </summary>
            /// <param name="zoneType">Zone type.</param>
            /// <param name="level">Target building level.</param>
            /// <param name="lotSize">Lot size.</param>
            /// <param name="maxHeight">Building maximum height.</param>
            /// <param name="accessFlags">Building access flags.</param>
            /// <param name="buildingPropertyData">Building property data.</param>
            /// <param name="captureSelectionTrace">True to capture selection trace data.</param>
            /// <param name="probeRecord">Probe record to update with trace data.</param>
            /// <param name="random">Random struct.</param>
            /// <returns>Selected building entity.</returns>
            private Entity SelectSpawnableBuilding(ZoneType zoneType, int level, int2 lotSize, float maxHeight, BuildingFlags accessFlags, BuildingPropertyData buildingPropertyData, bool captureSelectionTrace, ref ProbeRecord probeRecord, ref Random random)
            {
                int num = 0;
                Entity result = Entity.Null;
                SelectionTrace selectionTrace = default;
                for (int i = 0; i < m_SpawnableBuildingChunks.Length; i++)
                {
                    ArchetypeChunk archetypeChunk = m_SpawnableBuildingChunks[i];
                    if (!archetypeChunk.GetSharedComponent(m_BuildingSpawnGroupType).m_ZoneType.Equals(zoneType))
                    {
                        continue;
                    }

                    NativeArray<Entity> nativeArray = archetypeChunk.GetNativeArray(m_EntityType);
                    NativeArray<SpawnableBuildingData> nativeArray2 = archetypeChunk.GetNativeArray(ref m_SpawnableBuildingType);
                    NativeArray<BuildingData> nativeArray3 = archetypeChunk.GetNativeArray(ref m_BuildingType);
                    NativeArray<BuildingPropertyData> nativeArray4 = archetypeChunk.GetNativeArray(ref m_BuildingPropertyType);
                    NativeArray<ObjectGeometryData> nativeArray5 = archetypeChunk.GetNativeArray(ref m_ObjectGeometryType);
                    for (int j = 0; j < archetypeChunk.Count; j++)
                    {
                        SpawnableBuildingData spawnableBuildingData = nativeArray2[j];
                        BuildingData buildingData = nativeArray3[j];
                        BuildingPropertyData buildingPropertyData2 = nativeArray4[j];
                        ObjectGeometryData objectGeometryData = nativeArray5[j];
                        BuildingFlags candidateAccessFlags = buildingData.m_Flags & (BuildingFlags.LeftAccess | BuildingFlags.RightAccess);

                        if (captureSelectionTrace)
                        {
                            selectionTrace.m_CandidateCountZoneMatch++;
                        }

                        // Added toogle (m_IgnoreHouseholdCount) to check for buildingPropertyData.m_ResidentialProperties <= buildingPropertyData2.m_ResidentialProperties from here.
                        // This is to permit buildings to level up with more households than the previous building if the toggle is set, specifically making it compatible
                        // with the 'Realistic Households and Workplaces' mod.
                        if (level != spawnableBuildingData.m_Level)
                        {
                            if (captureSelectionTrace)
                            {
                                CaptureRejectSample(ref selectionTrace.m_LevelRejectSamples, nativeArray[j], objectGeometryData, buildingData, ProbeCandidateFilterStage.LevelMatch);
                            }

                            continue;
                        }

                        if (captureSelectionTrace)
                        {
                            selectionTrace.m_CandidateCountLevelMatch++;
                        }

                        if (!lotSize.Equals(buildingData.m_LotSize))
                        {
                            if (captureSelectionTrace)
                            {
                                CaptureRejectSample(ref selectionTrace.m_LotRejectSamples, nativeArray[j], objectGeometryData, buildingData, ProbeCandidateFilterStage.LotMatch);
                            }

                            continue;
                        }

                        if (captureSelectionTrace)
                        {
                            selectionTrace.m_CandidateCountLotMatch++;
                        }

                        if (objectGeometryData.m_Size.y > maxHeight)
                        {
                            if (captureSelectionTrace)
                            {
                                CaptureRejectSample(ref selectionTrace.m_HeightRejectSamples, nativeArray[j], objectGeometryData, buildingData, ProbeCandidateFilterStage.HeightMatch);
                            }

                            continue;
                        }

                        if (captureSelectionTrace)
                        {
                            selectionTrace.m_CandidateCountHeightMatch++;
                        }

                        if (candidateAccessFlags != accessFlags)
                        {
                            if (captureSelectionTrace)
                            {
                                CaptureRejectSample(ref selectionTrace.m_AccessRejectSamples, nativeArray[j], objectGeometryData, buildingData, ProbeCandidateFilterStage.AccessMatch);
                            }

                            continue;
                        }

                        if (captureSelectionTrace)
                        {
                            selectionTrace.m_CandidateCountAccessMatch++;
                        }

                        if (!m_IgnoreHouseholdCount && buildingPropertyData.m_ResidentialProperties > buildingPropertyData2.m_ResidentialProperties)
                        {
                            if (captureSelectionTrace)
                            {
                                CaptureRejectSample(ref selectionTrace.m_HouseholdOrPropertyRejectSamples, nativeArray[j], objectGeometryData, buildingData, ProbeCandidateFilterStage.HouseholdOrPropertyMatch);
                            }

                            continue;
                        }

                        if (captureSelectionTrace)
                        {
                            selectionTrace.m_CandidateCountHouseholdOrPropertyMatch++;
                        }

                        if (buildingPropertyData.m_AllowedManufactured != buildingPropertyData2.m_AllowedManufactured)
                        {
                            if (captureSelectionTrace)
                            {
                                CaptureRejectSample(ref selectionTrace.m_AllowedManufacturedRejectSamples, nativeArray[j], objectGeometryData, buildingData, ProbeCandidateFilterStage.AllowedManufacturedMatch);
                            }

                            continue;
                        }

                        if (captureSelectionTrace)
                        {
                            selectionTrace.m_CandidateCountAllowedManufacturedMatch++;
                        }

                        if (buildingPropertyData.m_AllowedSold != buildingPropertyData2.m_AllowedSold)
                        {
                            if (captureSelectionTrace)
                            {
                                CaptureRejectSample(ref selectionTrace.m_AllowedSoldRejectSamples, nativeArray[j], objectGeometryData, buildingData, ProbeCandidateFilterStage.AllowedSoldMatch);
                            }

                            continue;
                        }

                        if (captureSelectionTrace)
                        {
                            selectionTrace.m_CandidateCountAllowedSoldMatch++;
                        }

                        if (buildingPropertyData.m_AllowedStored != buildingPropertyData2.m_AllowedStored)
                        {
                            if (captureSelectionTrace)
                            {
                                CaptureRejectSample(ref selectionTrace.m_AllowedStoredRejectSamples, nativeArray[j], objectGeometryData, buildingData, ProbeCandidateFilterStage.AllowedStoredMatch);
                            }

                            continue;
                        }

                        if (captureSelectionTrace)
                        {
                            selectionTrace.m_CandidateCountAllowedStoredMatch++;
                        }

                        int num2 = 100;
                        num += num2;
                        if (random.NextInt(num) < num2)
                        {
                            result = nativeArray[j];
                        }
                    }
                }

                if (captureSelectionTrace)
                {
                    ApplySelectionTrace(ref probeRecord, zoneType, level, lotSize, maxHeight, accessFlags, ref selectionTrace, result == Entity.Null);
                }

                return result;
            }

            private static void CaptureRejectSample(ref ProbeCandidateSamples samples, Entity prefab, ObjectGeometryData objectGeometryData, BuildingData buildingData, ProbeCandidateFilterStage rejectStage)
            {
                if (samples.m_Count >= 3)
                {
                    return;
                }

                ProbeCandidateSample sample = default;
                sample.m_Prefab = prefab;
                sample.m_Height = objectGeometryData.m_Size.y;
                sample.m_LotSize = buildingData.m_LotSize;
                sample.m_AccessFlags = buildingData.m_Flags & (BuildingFlags.LeftAccess | BuildingFlags.RightAccess);
                sample.m_RejectStage = rejectStage;

                switch (samples.m_Count)
                {
                    case 0:
                        samples.m_Sample1 = sample;
                        break;
                    case 1:
                        samples.m_Sample2 = sample;
                        break;
                    default:
                        samples.m_Sample3 = sample;
                        break;
                }

                samples.m_Count++;
            }

            private static void ApplySelectionTrace(ref ProbeRecord probeRecord, ZoneType zoneType, int level, int2 lotSize, float maxHeight, BuildingFlags accessFlags, ref SelectionTrace selectionTrace, bool selectionFailed)
            {
                probeRecord.m_ZoneType = zoneType;
                probeRecord.m_TargetLevel = level;
                probeRecord.m_LotSize = lotSize;
                probeRecord.m_MaxHeight = maxHeight;
                probeRecord.m_AccessFlags = accessFlags;
                probeRecord.m_CandidateCountZoneMatch = selectionTrace.m_CandidateCountZoneMatch;
                probeRecord.m_CandidateCountLevelMatch = selectionTrace.m_CandidateCountLevelMatch;
                probeRecord.m_CandidateCountLotMatch = selectionTrace.m_CandidateCountLotMatch;
                probeRecord.m_CandidateCountHeightMatch = selectionTrace.m_CandidateCountHeightMatch;
                probeRecord.m_CandidateCountAccessMatch = selectionTrace.m_CandidateCountAccessMatch;
                probeRecord.m_CandidateCountHouseholdOrPropertyMatch = selectionTrace.m_CandidateCountHouseholdOrPropertyMatch;
                probeRecord.m_CandidateCountAllowedManufacturedMatch = selectionTrace.m_CandidateCountAllowedManufacturedMatch;
                probeRecord.m_CandidateCountAllowedSoldMatch = selectionTrace.m_CandidateCountAllowedSoldMatch;
                probeRecord.m_CandidateCountAllowedStoredMatch = selectionTrace.m_CandidateCountAllowedStoredMatch;
                probeRecord.m_CandidateCountFinal = selectionTrace.m_CandidateCountAllowedStoredMatch;

                if (!selectionFailed)
                {
                    return;
                }

                probeRecord.m_HasSelectionBreakdown = 1;
                probeRecord.m_CandidateZeroStage = DetermineCandidateZeroStage(selectionTrace);
                CopySelectionSamples(ref probeRecord, GetRejectSamples(selectionTrace, probeRecord.m_CandidateZeroStage));
            }

            private static ProbeCandidateFilterStage DetermineCandidateZeroStage(SelectionTrace selectionTrace)
            {
                if (selectionTrace.m_CandidateCountZoneMatch == 0)
                {
                    return ProbeCandidateFilterStage.ZoneMatch;
                }

                if (selectionTrace.m_CandidateCountLevelMatch == 0)
                {
                    return ProbeCandidateFilterStage.LevelMatch;
                }

                if (selectionTrace.m_CandidateCountLotMatch == 0)
                {
                    return ProbeCandidateFilterStage.LotMatch;
                }

                if (selectionTrace.m_CandidateCountHeightMatch == 0)
                {
                    return ProbeCandidateFilterStage.HeightMatch;
                }

                if (selectionTrace.m_CandidateCountAccessMatch == 0)
                {
                    return ProbeCandidateFilterStage.AccessMatch;
                }

                if (selectionTrace.m_CandidateCountHouseholdOrPropertyMatch == 0)
                {
                    return ProbeCandidateFilterStage.HouseholdOrPropertyMatch;
                }

                if (selectionTrace.m_CandidateCountAllowedManufacturedMatch == 0)
                {
                    return ProbeCandidateFilterStage.AllowedManufacturedMatch;
                }

                if (selectionTrace.m_CandidateCountAllowedSoldMatch == 0)
                {
                    return ProbeCandidateFilterStage.AllowedSoldMatch;
                }

                if (selectionTrace.m_CandidateCountAllowedStoredMatch == 0)
                {
                    return ProbeCandidateFilterStage.AllowedStoredMatch;
                }

                return ProbeCandidateFilterStage.None;
            }

            private static ProbeCandidateSamples GetRejectSamples(SelectionTrace selectionTrace, ProbeCandidateFilterStage zeroStage)
            {
                return zeroStage switch
                {
                    ProbeCandidateFilterStage.LevelMatch => selectionTrace.m_LevelRejectSamples,
                    ProbeCandidateFilterStage.LotMatch => selectionTrace.m_LotRejectSamples,
                    ProbeCandidateFilterStage.HeightMatch => selectionTrace.m_HeightRejectSamples,
                    ProbeCandidateFilterStage.AccessMatch => selectionTrace.m_AccessRejectSamples,
                    ProbeCandidateFilterStage.HouseholdOrPropertyMatch => selectionTrace.m_HouseholdOrPropertyRejectSamples,
                    ProbeCandidateFilterStage.AllowedManufacturedMatch => selectionTrace.m_AllowedManufacturedRejectSamples,
                    ProbeCandidateFilterStage.AllowedSoldMatch => selectionTrace.m_AllowedSoldRejectSamples,
                    ProbeCandidateFilterStage.AllowedStoredMatch => selectionTrace.m_AllowedStoredRejectSamples,
                    _ => default,
                };
            }

            private static void CopySelectionSamples(ref ProbeRecord probeRecord, ProbeCandidateSamples samples)
            {
                probeRecord.m_SelectionSample1 = samples.m_Sample1;
                probeRecord.m_SelectionSample2 = samples.m_Sample2;
                probeRecord.m_SelectionSample3 = samples.m_Sample3;
            }

            /// <summary>
            /// Gets a building's maximum height.
            /// </summary>
            /// <param name="building">Building entity.</param>
            /// <param name="prefabBuildingData">Building prefab data.</param>
            /// <returns>Building maximum height, in metres.</returns>
            private float GetMaxHeight(Entity building, BuildingData prefabBuildingData)
            {
                Game.Objects.Transform transform = m_TransformData[building];
                float2 xz = math.rotate(transform.m_Rotation, new float3(8f, 0f, 0f)).xz;
                float2 xz2 = math.rotate(transform.m_Rotation, new float3(0f, 0f, 8f)).xz;
                float2 @float = xz * ((prefabBuildingData.m_LotSize.x * 0.5f) - 0.5f);
                float2 float2 = xz2 * ((prefabBuildingData.m_LotSize.y * 0.5f) - 0.5f);
                float2 float3 = math.abs(float2) + math.abs(@float);
                Iterator iterator = default;
                iterator.m_Bounds = new Bounds2(transform.m_Position.xz - float3, transform.m_Position.xz + float3);
                iterator.m_LotSize = prefabBuildingData.m_LotSize;
                iterator.m_StartPosition = transform.m_Position.xz + float2 + @float;
                iterator.m_Right = xz;
                iterator.m_Forward = xz2;
                iterator.m_MaxHeight = int.MaxValue;
                iterator.m_BlockData = m_BlockData;
                iterator.m_ValidAreaData = m_ValidAreaData;
                iterator.m_Cells = m_Cells;
                m_ZoneSearchTree.Iterate(ref iterator);
                return iterator.m_MaxHeight - transform.m_Position.y;
            }

            /// <summary>
            /// Zone search tree iterator.
            /// </summary>
            private struct Iterator : INativeQuadTreeIterator<Entity, Bounds2>, IUnsafeQuadTreeIterator<Entity, Bounds2>
            {
                public Bounds2 m_Bounds;
                public int2 m_LotSize;
                public float2 m_StartPosition;
                public float2 m_Right;
                public float2 m_Forward;
                public int m_MaxHeight;
                public ComponentLookup<Block> m_BlockData;
                public ComponentLookup<ValidArea> m_ValidAreaData;
                public BufferLookup<Cell> m_Cells;

                public readonly bool Intersect(Bounds2 bounds)
                {
                    return MathUtils.Intersect(bounds, m_Bounds);
                }

                public void Iterate(Bounds2 bounds, Entity blockEntity)
                {
                    if (!MathUtils.Intersect(bounds, m_Bounds))
                    {
                        return;
                    }

                    ValidArea validArea = m_ValidAreaData[blockEntity];
                    if (validArea.m_Area.y <= validArea.m_Area.x)
                    {
                        return;
                    }

                    Block block = m_BlockData[blockEntity];
                    DynamicBuffer<Cell> dynamicBuffer = m_Cells[blockEntity];
                    float2 startPosition = m_StartPosition;
                    int2 @int = default;
                    @int.y = 0;
                    while (@int.y < m_LotSize.y)
                    {
                        float2 position = startPosition;
                        @int.x = 0;
                        while (@int.x < m_LotSize.x)
                        {
                            int2 cellIndex = ZoneUtils.GetCellIndex(block, position);
                            if (math.all((cellIndex >= validArea.m_Area.xz) & (cellIndex < validArea.m_Area.yw)))
                            {
                                int index = (cellIndex.y * block.m_Size.x) + cellIndex.x;
                                Cell cell = dynamicBuffer[index];
                                if ((cell.m_State & CellFlags.Visible) != 0)
                                {
                                    m_MaxHeight = math.min(m_MaxHeight, cell.m_Height);
                                }
                            }

                            position -= m_Right;
                            @int.x++;
                        }

                        startPosition -= m_Forward;
                        @int.y++;
                    }
                }
            }
        }

        /// <summary>
        /// Job to level down buildings.
        /// Derived from game code.
        /// </summary>
        [BurstCompile]
        private struct LeveldownJob : IJob
        {
            [ReadOnly]
            public bool m_DisableAbandonment;
            [ReadOnly]
            public ComponentLookup<LevelLocked> m_LevelLockedData;
            [ReadOnly]
            public ComponentLookup<PrefabRef> m_Prefabs;
            [ReadOnly]
            public ComponentLookup<SpawnableBuildingData> m_SpawnableBuildings;
            [ReadOnly]
            public ComponentLookup<BuildingData> m_BuildingDatas;
            [ReadOnly]
            public ComponentLookup<Building> m_Buildings;
            [ReadOnly]
            public ComponentLookup<ElectricityConsumer> m_ElectricityConsumers;
            [ReadOnly]
            public ComponentLookup<WaterConsumer> m_WaterConsumers;
            [ReadOnly]
            public ComponentLookup<GarbageProducer> m_GarbageProducers;
            [ReadOnly]
            public ComponentLookup<MailProducer> m_MailProducers;
            [ReadOnly]
            public ComponentLookup<BuildingPropertyData> m_BuildingPropertyDatas;
            [ReadOnly]
            public ComponentLookup<OfficeBuilding> m_OfficeBuilding;
            public NativeQueue<TriggerAction> m_TriggerBuffer;
            public ComponentLookup<CrimeProducer> m_CrimeProducers;
            public BufferLookup<Renter> m_Renters;
            [ReadOnly]
            public BuildingConfigurationData m_BuildingConfigurationData;
            public NativeQueue<Entity> m_LeveldownQueue;
            public NativeQueue<ProbeRecord> m_ProbeQueue;
            public EntityCommandBuffer m_CommandBuffer;
            public NativeQueue<Entity> m_UpdatedElectricityRoadEdges;
            public NativeQueue<Entity> m_UpdatedWaterPipeRoadEdges;
            public IconCommandBuffer m_IconCommandBuffer;
            public uint m_SimulationFrame;

            /// <summary>
            /// Job execution.
            /// </summary>
            public void Execute()
            {
                while (m_LeveldownQueue.TryDequeue(out Entity item))
                {
                    ProbeRecord probeRecord = default;
                    probeRecord.m_Direction = ProbeDirection.Leveldown;
                    probeRecord.m_DecisionKind = ProbeDecisionKind.Dequeued;
                    probeRecord.m_Reason = ProbeReason.None;
                    probeRecord.m_Building = item;
                    probeRecord.m_CurrentPrefab = m_Prefabs.HasComponent(item) ? m_Prefabs[item].m_Prefab : Entity.Null;

                    if (!m_Prefabs.HasComponent(item))
                    {
                        probeRecord.m_DecisionKind = ProbeDecisionKind.Skip;
                        probeRecord.m_Reason = ProbeReason.Other;
                        m_ProbeQueue.Enqueue(probeRecord);
                        continue;
                    }

                    if (m_DisableAbandonment)
                    {
                        probeRecord.m_DecisionKind = ProbeDecisionKind.Skip;
                        probeRecord.m_Reason = ProbeReason.DisableAbandonmentGlobal;
                        m_ProbeQueue.Enqueue(probeRecord);
                        continue;
                    }

                    if (m_LevelLockedData.HasComponent(item))
                    {
                        probeRecord.m_DecisionKind = ProbeDecisionKind.Skip;
                        probeRecord.m_Reason = ProbeReason.LevelLocked;
                        m_ProbeQueue.Enqueue(probeRecord);
                        continue;
                    }

                    Entity prefab = m_Prefabs[item].m_Prefab;
                    probeRecord.m_CurrentPrefab = prefab;
                    if (!m_SpawnableBuildings.HasComponent(prefab))
                    {
                        probeRecord.m_DecisionKind = ProbeDecisionKind.Skip;
                        probeRecord.m_Reason = ProbeReason.NotSpawnable;
                        m_ProbeQueue.Enqueue(probeRecord);
                        continue;
                    }

                    probeRecord.m_DecisionKind = ProbeDecisionKind.Success;
                    m_ProbeQueue.Enqueue(probeRecord);

                    BuildingPropertyData buildingPropertyData = m_BuildingPropertyDatas[prefab];
                    m_CommandBuffer.AddComponent(item, new Abandoned
                    {
                        m_AbandonmentTime = m_SimulationFrame,
                    });

                    m_CommandBuffer.AddComponent(item, default(Updated));
                    if (m_ElectricityConsumers.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent<ElectricityConsumer>(item);
                        Entity roadEdge = m_Buildings[item].m_RoadEdge;
                        if (roadEdge != Entity.Null)
                        {
                            m_UpdatedElectricityRoadEdges.Enqueue(roadEdge);
                        }
                    }

                    if (m_WaterConsumers.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent<WaterConsumer>(item);
                        Entity roadEdge2 = m_Buildings[item].m_RoadEdge;
                        if (roadEdge2 != Entity.Null)
                        {
                            m_UpdatedWaterPipeRoadEdges.Enqueue(roadEdge2);
                        }
                    }

                    if (m_GarbageProducers.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent<GarbageProducer>(item);
                    }

                    if (m_MailProducers.HasComponent(item))
                    {
                        m_CommandBuffer.RemoveComponent<MailProducer>(item);
                    }

                    if (m_CrimeProducers.HasComponent(item))
                    {
                        CrimeProducer crimeProducer = m_CrimeProducers[item];
                        m_CommandBuffer.SetComponent(item, new CrimeProducer
                        {
                            m_Crime = crimeProducer.m_Crime * 2f,
                            m_PatrolRequest = crimeProducer.m_PatrolRequest,
                        });
                    }

                    if (m_Renters.HasBuffer(item))
                    {
                        DynamicBuffer<Renter> dynamicBuffer = m_Renters[item];
                        for (int num = dynamicBuffer.Length - 1; num >= 0; num--)
                        {
                            m_CommandBuffer.RemoveComponent<PropertyRenter>(dynamicBuffer[num].m_Renter);
                            dynamicBuffer.RemoveAt(num);
                        }
                    }

                    if ((m_Buildings[item].m_Flags & Game.Buildings.BuildingFlags.HighRentWarning) != Game.Buildings.BuildingFlags.None)
                    {
                        Building value = m_Buildings[item];
                        m_IconCommandBuffer.Remove(item, m_BuildingConfigurationData.m_HighRentNotification);
                        value.m_Flags &= ~Game.Buildings.BuildingFlags.HighRentWarning;
                        m_Buildings[item] = value;
                    }

                    m_IconCommandBuffer.Remove(item, IconPriority.Problem);
                    m_IconCommandBuffer.Remove(item, IconPriority.FatalProblem);
                    m_IconCommandBuffer.Add(item, m_BuildingConfigurationData.m_AbandonedNotification, IconPriority.FatalProblem);
                    if (buildingPropertyData.CountProperties(AreaType.Commercial) > 0)
                    {
                        m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelDownCommercialBuilding, Entity.Null, item, item));
                    }

                    if (buildingPropertyData.CountProperties(AreaType.Industrial) > 0)
                    {
                        if (m_OfficeBuilding.HasComponent(prefab))
                        {
                            m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelDownOfficeBuilding, Entity.Null, item, item));
                        }
                        else
                        {
                            m_TriggerBuffer.Enqueue(new TriggerAction(TriggerType.LevelDownIndustrialBuilding, Entity.Null, item, item));
                        }
                    }
                }
            }
        }

        private void ClearProbeQueue()
        {
            while (_probeQueue.TryDequeue(out _))
            {
            }
        }

        private void LogHistoricalProbeRecords(uint frame, int levelupQueueCount, int leveldownQueueCount)
        {
            ProbeCounters levelupCounters = default;
            ProbeCounters leveldownCounters = default;
            List<ProbeRecord> detailRecords = new (ProbeDetailLimit);
            List<ProbeRecord> selectionBreakdownRecords = new ();

            while (_probeQueue.TryDequeue(out ProbeRecord record))
            {
                if (record.m_Direction == ProbeDirection.Levelup)
                {
                    AccumulateRecord(ref levelupCounters, record);
                    if (record.m_IsDetail != 0 && detailRecords.Count < ProbeDetailLimit)
                    {
                        detailRecords.Add(record);
                    }

                    if (record.m_HasSelectionBreakdown != 0)
                    {
                        selectionBreakdownRecords.Add(record);
                    }
                }
                else
                {
                    AccumulateRecord(ref leveldownCounters, record);
                }
            }

            LogHistoricalSummary(frame, levelupQueueCount, leveldownQueueCount, levelupCounters, leveldownCounters);
            foreach (ProbeRecord detailRecord in detailRecords)
            {
                LogHistoricalDetail(detailRecord);
            }

            foreach (ProbeRecord breakdownRecord in selectionBreakdownRecords)
            {
                LogHistoricalSelectionBreakdown(breakdownRecord);
            }
        }

        private void LogHistoricalSummary(uint frame, int levelupQueueCount, int leveldownQueueCount, ProbeCounters levelupCounters, ProbeCounters leveldownCounters)
        {
            Mod.Instance.Log.Info(CompatibilityProbeLog.Format("summary", $"system=HistoricalLevellingSystem, frame={frame}, levelup_queue={levelupQueueCount}, leveldown_queue={leveldownQueueCount}, levelup_processed={levelupCounters.m_Processed}, levelup_success={levelupCounters.m_Success}, levelup_skip_level_locked={levelupCounters.m_LevelLocked}, levelup_skip_not_spawnable={levelupCounters.m_NotSpawnable}, levelup_skip_zone_disabled={levelupCounters.m_ZoneDisabled}, levelup_skip_select_spawnable_failed={levelupCounters.m_SelectSpawnableFailed}, levelup_skip_disable_levelling_global={levelupCounters.m_DisableLevellingGlobal}, levelup_skip_other={levelupCounters.m_Other}, leveldown_processed={leveldownCounters.m_Processed}, leveldown_success={leveldownCounters.m_Success}, leveldown_skip_level_locked={leveldownCounters.m_LevelLocked}, leveldown_skip_not_spawnable={leveldownCounters.m_NotSpawnable}, leveldown_skip_disable_levelling_global={leveldownCounters.m_DisableLevellingGlobal}, leveldown_skip_disable_abandonment_global={leveldownCounters.m_DisableAbandonmentGlobal}, leveldown_skip_other={leveldownCounters.m_Other})"));
        }

        private void LogHistoricalDetail(ProbeRecord record)
        {
            Mod.Instance.Log.Info(GetHistoricalDetailLogLine(record));
        }

        private void LogHistoricalSelectionBreakdown(ProbeRecord record)
        {
            Mod.Instance.Log.Info(GetHistoricalSelectionBreakdownLogLine(record));
        }

        private static string GetHistoricalDetailLogLine(ProbeRecord record)
        {
            return CompatibilityProbeLog.Format("detail", $"system=HistoricalLevellingSystem, decision={GetDecisionLabel(record.m_DecisionKind)}, skip_reason={GetReasonLabel(record.m_Reason)}, building={CompatibilityProbeLog.FormatEntity(record.m_Building)}, current_prefab={CompatibilityProbeLog.FormatEntity(record.m_CurrentPrefab)}, current_level={record.m_CurrentLevel}, area_class={GetAreaClassLabel(record.m_AreaClass)}, spawned={FormatBool(record.m_Spawned)}, plopped={FormatBool(record.m_Plopped)}, signature={FormatBool(record.m_Signature)}, level_locked={FormatBool(record.m_LevelLocked)}, property_on_market={FormatBool(record.m_PropertyOnMarket)}, property_to_be_on_market={FormatBool(record.m_PropertyToBeOnMarket)}, under_construction={FormatBool(record.m_UnderConstruction)}, renter_count={record.m_RenterCount}, selected_next_prefab={CompatibilityProbeLog.FormatEntity(record.m_SelectedNextPrefab)}, select_failed_no_candidate={FormatBool(record.m_SelectFailedNoCandidate)}, candidate_count_final={FormatCandidateCount(record.m_CandidateCountFinal)})");
        }

        private static string GetHistoricalSelectionBreakdownLogLine(ProbeRecord record)
        {
            return CompatibilityProbeLog.Format("selection_breakdown", $"system=HistoricalLevellingSystem, building={CompatibilityProbeLog.FormatEntity(record.m_Building)}, current_prefab={CompatibilityProbeLog.FormatEntity(record.m_CurrentPrefab)}, current_level={record.m_CurrentLevel}, area_class={GetAreaClassLabel(record.m_AreaClass)}, target_level={record.m_TargetLevel}, zone_type={CompatibilityProbeLog.FormatZoneType(record.m_ZoneType)}, lot_size={CompatibilityProbeLog.FormatInt2(record.m_LotSize)}, max_height={CompatibilityProbeLog.FormatFloat(record.m_MaxHeight)}, access_flags={CompatibilityProbeLog.FormatAccessFlags(record.m_AccessFlags)}, candidate_zero_stage={GetCandidateFilterStageLabel(record.m_CandidateZeroStage)}, candidate_count_zone_match={record.m_CandidateCountZoneMatch}, candidate_count_level_match={record.m_CandidateCountLevelMatch}, candidate_count_lot_match={record.m_CandidateCountLotMatch}, candidate_count_height_match={record.m_CandidateCountHeightMatch}, candidate_count_access_match={record.m_CandidateCountAccessMatch}, candidate_count_household_or_property_match={record.m_CandidateCountHouseholdOrPropertyMatch}, candidate_count_allowed_manufactured_match={record.m_CandidateCountAllowedManufacturedMatch}, candidate_count_allowed_sold_match={record.m_CandidateCountAllowedSoldMatch}, candidate_count_allowed_stored_match={record.m_CandidateCountAllowedStoredMatch}, candidate_count_final={FormatCandidateCount(record.m_CandidateCountFinal)}, sample1_prefab={CompatibilityProbeLog.FormatEntity(record.m_SelectionSample1.m_Prefab)}, sample1_height={FormatSampleHeight(record.m_SelectionSample1)}, sample1_lot_size={FormatSampleLotSize(record.m_SelectionSample1)}, sample1_access_flags={FormatSampleAccessFlags(record.m_SelectionSample1)}, sample1_reject_reason={GetCandidateRejectReasonLabel(record.m_SelectionSample1.m_RejectStage)}, sample2_prefab={CompatibilityProbeLog.FormatEntity(record.m_SelectionSample2.m_Prefab)}, sample2_height={FormatSampleHeight(record.m_SelectionSample2)}, sample2_lot_size={FormatSampleLotSize(record.m_SelectionSample2)}, sample2_access_flags={FormatSampleAccessFlags(record.m_SelectionSample2)}, sample2_reject_reason={GetCandidateRejectReasonLabel(record.m_SelectionSample2.m_RejectStage)}, sample3_prefab={CompatibilityProbeLog.FormatEntity(record.m_SelectionSample3.m_Prefab)}, sample3_height={FormatSampleHeight(record.m_SelectionSample3)}, sample3_lot_size={FormatSampleLotSize(record.m_SelectionSample3)}, sample3_access_flags={FormatSampleAccessFlags(record.m_SelectionSample3)}, sample3_reject_reason={GetCandidateRejectReasonLabel(record.m_SelectionSample3.m_RejectStage)})");
        }

        private static void AccumulateRecord(ref ProbeCounters counters, ProbeRecord record)
        {
            counters.m_Processed++;
            if (record.m_DecisionKind == ProbeDecisionKind.Success)
            {
                counters.m_Success++;
                return;
            }

            switch (record.m_Reason)
            {
                case ProbeReason.LevelLocked:
                    counters.m_LevelLocked++;
                    break;
                case ProbeReason.NotSpawnable:
                    counters.m_NotSpawnable++;
                    break;
                case ProbeReason.ZoneDisabled:
                    counters.m_ZoneDisabled++;
                    break;
                case ProbeReason.SelectSpawnableFailed:
                    counters.m_SelectSpawnableFailed++;
                    break;
                case ProbeReason.DisableLevellingGlobal:
                    counters.m_DisableLevellingGlobal++;
                    break;
                case ProbeReason.DisableAbandonmentGlobal:
                    counters.m_DisableAbandonmentGlobal++;
                    break;
                case ProbeReason.Other:
                    counters.m_Other++;
                    break;
            }
        }

        private static string GetAreaClassLabel(ProbeAreaClass areaClass)
        {
            return areaClass switch
            {
                ProbeAreaClass.Residential => "residential",
                ProbeAreaClass.Commercial => "commercial",
                ProbeAreaClass.Industrial => "industrial",
                ProbeAreaClass.Office => "office",
                _ => "unknown",
            };
        }

        private static string GetDecisionLabel(ProbeDecisionKind decisionKind)
        {
            return decisionKind switch
            {
                ProbeDecisionKind.Dequeued => "dequeued",
                ProbeDecisionKind.Skip => "skip",
                ProbeDecisionKind.Success => "success",
                _ => "dequeued",
            };
        }

        private static string GetReasonLabel(ProbeReason reason)
        {
            return reason switch
            {
                ProbeReason.LevelLocked => "level_locked",
                ProbeReason.NotSpawnable => "not_spawnable",
                ProbeReason.ZoneDisabled => "zone_disabled",
                ProbeReason.SelectSpawnableFailed => "select_spawnable_failed",
                ProbeReason.DisableLevellingGlobal => "disable_levelling_global",
                ProbeReason.DisableAbandonmentGlobal => "disable_abandonment_global",
                ProbeReason.Other => "other",
                _ => "none",
            };
        }

        private static string GetCandidateFilterStageLabel(ProbeCandidateFilterStage stage)
        {
            return stage switch
            {
                ProbeCandidateFilterStage.ZoneMatch => "zone_match",
                ProbeCandidateFilterStage.LevelMatch => "level_match",
                ProbeCandidateFilterStage.LotMatch => "lot_match",
                ProbeCandidateFilterStage.HeightMatch => "height_match",
                ProbeCandidateFilterStage.AccessMatch => "access_match",
                ProbeCandidateFilterStage.HouseholdOrPropertyMatch => "household_or_property_match",
                ProbeCandidateFilterStage.AllowedManufacturedMatch => "allowed_manufactured_match",
                ProbeCandidateFilterStage.AllowedSoldMatch => "allowed_sold_match",
                ProbeCandidateFilterStage.AllowedStoredMatch => "allowed_stored_match",
                _ => "none",
            };
        }

        private static string GetCandidateRejectReasonLabel(ProbeCandidateFilterStage stage)
        {
            return stage switch
            {
                ProbeCandidateFilterStage.LevelMatch => "level_mismatch",
                ProbeCandidateFilterStage.LotMatch => "lot_size_mismatch",
                ProbeCandidateFilterStage.HeightMatch => "height_mismatch",
                ProbeCandidateFilterStage.AccessMatch => "access_flags_mismatch",
                ProbeCandidateFilterStage.HouseholdOrPropertyMatch => "household_or_property_mismatch",
                ProbeCandidateFilterStage.AllowedManufacturedMatch => "allowed_manufactured_mismatch",
                ProbeCandidateFilterStage.AllowedSoldMatch => "allowed_sold_mismatch",
                ProbeCandidateFilterStage.AllowedStoredMatch => "allowed_stored_mismatch",
                _ => "none",
            };
        }

        private static string FormatCandidateCount(int count) => count < 0 ? "unknown" : count.ToString(CultureInfo.InvariantCulture);

        private static string FormatSampleHeight(ProbeCandidateSample sample) => sample.m_Prefab == Entity.Null ? "null" : CompatibilityProbeLog.FormatFloat(sample.m_Height);

        private static string FormatSampleLotSize(ProbeCandidateSample sample) => sample.m_Prefab == Entity.Null ? "null" : CompatibilityProbeLog.FormatInt2(sample.m_LotSize);

        private static string FormatSampleAccessFlags(ProbeCandidateSample sample) => sample.m_Prefab == Entity.Null ? "null" : CompatibilityProbeLog.FormatAccessFlags(sample.m_AccessFlags);

        private static string FormatBool(byte value) => value == 0 ? "false" : "true";
    }
}
