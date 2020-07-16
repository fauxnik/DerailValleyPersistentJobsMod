﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Harmony12;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityModManagerNet;
using DV;
using DV.Logic.Job;
using DV.ServicePenalty;
using DV.JObjectExtstensions;

namespace PersistentJobsMod
{
	static class Main
	{
		private static UnityModManager.ModEntry thisModEntry;
		private static bool isModBroken = false;
		private static float initialDistanceRegular = 0f;
		private static float initialDistanceAnyJobTaken = 0f;
		private static List<string> stationIdSpawnBlockList = new List<string>();

		private static readonly string VERSION = "2.0.0";
		private static readonly string SAVE_DATA_PRIMARY_KEY = "Mod_Persistent_Jobs";
		private static readonly string SAVE_DATA_VERSION_KEY = "version";
		private static readonly string SAVE_DATA_SPAWN_BLOCK_KEY = "spawn_block";

#if DEBUG
		private static float PERIOD = 60f;
#else
		private static float PERIOD = 5f * 60f;
#endif
		public static float DVJobDestroyDistanceRegular { get { return initialDistanceRegular; } }

		static void Load(UnityModManager.ModEntry modEntry)
		{
			var harmony = HarmonyInstance.Create(modEntry.Info.Id);
			harmony.PatchAll(Assembly.GetExecutingAssembly());
			modEntry.OnToggle = OnToggle;
			thisModEntry = modEntry;
		}

		static bool OnToggle(UnityModManager.ModEntry modEntry, bool isTogglingOn)
		{
			bool isTogglingOff = !isTogglingOn;

			if (SingletonBehaviour<UnusedTrainCarDeleter>.Instance == null)
			{
				// delay initialization
				modEntry.OnUpdate = (entry, delta) =>
				{
					if (SingletonBehaviour<UnusedTrainCarDeleter>.Instance != null)
					{
						modEntry.OnUpdate = null;
						ReplaceCoroutine(isTogglingOn);
					}
				};
				return true;
			}
			else
			{
				ReplaceCoroutine(isTogglingOn);
			}

			if (isTogglingOff)
			{
				stationIdSpawnBlockList.Clear();
			}

			if (isModBroken)
			{
				return !isTogglingOn;
			}

			return true;
		}

		static void ReplaceCoroutine(bool isTogglingOn)
		{
			float? carsCheckPeriod = Traverse.Create(SingletonBehaviour<UnusedTrainCarDeleter>.Instance)
				.Field("DELETE_CARS_CHECK_PERIOD")
				.GetValue<float>();
			if (carsCheckPeriod == null)
			{
				carsCheckPeriod = 0.5f;
			}
			SingletonBehaviour<UnusedTrainCarDeleter>.Instance.StopAllCoroutines();
			if (isTogglingOn && !isModBroken)
			{
				thisModEntry.Logger.Log("Injected mod coroutine.");
				SingletonBehaviour<UnusedTrainCarDeleter>.Instance
					.StartCoroutine(TrainCarsCreateJobOrDeleteCheck(PERIOD, Mathf.Max(carsCheckPeriod.Value, 1.0f)));
			}
			else
			{
				thisModEntry.Logger.Log("Restored game coroutine.");
				SingletonBehaviour<UnusedTrainCarDeleter>.Instance.StartCoroutine(
					SingletonBehaviour<UnusedTrainCarDeleter>.Instance.TrainCarsDeleteCheck(carsCheckPeriod.Value)
				);
			}
		}

		static void OnCriticalFailure()
		{
			isModBroken = true;
			thisModEntry.Active = false;
			thisModEntry.Logger.Critical("Deactivating mod PersistentJobs due to critical failure!");
			thisModEntry.Logger.Warning("You can reactivate PersistentJobs by restarting the game, but this failure " +
				"type likely indicates an incompatibility between the mod and a recent game update. Please search the " +
				"mod's Github issue tracker for a relevant report. If none is found, please open one. Include the " +
				"exception message printed above and your game's current build number.");
		}

		[HarmonyPatch(typeof(SaveGameManager), "Save")]
		class SaveGameManager_Save_Patch
		{
			static void Prefix(SaveGameManager __instance)
			{
				try
				{
					JArray spawnBlockSaveData = new JArray(from id in stationIdSpawnBlockList select new JValue(id));

					JObject saveData = new JObject(
						new JProperty(SAVE_DATA_VERSION_KEY, new JValue(VERSION)),
						new JProperty(SAVE_DATA_SPAWN_BLOCK_KEY, spawnBlockSaveData));

					SaveGameManager.data.SetJObject(SAVE_DATA_PRIMARY_KEY, saveData);
				}
				catch (Exception e)
				{
					// TODO: what to do if saving fails?
					thisModEntry.Logger.Warning(string.Format("Saving mod data failed with exception:\n{0}", e));
				}
			}
		}

		[HarmonyPatch(typeof(SaveGameManager), "Load")]
		class SaveGameManager_Load_Patch
		{
			static void Postfix(SaveGameManager __instance)
			{
				try
				{
					JObject saveData = SaveGameManager.data.GetJObject(SAVE_DATA_PRIMARY_KEY);

					if (saveData == null)
					{
						thisModEntry.Logger.Log("Not loading save data: primary object was null.");
						return;
					}

					JArray spawnBlockSaveData = (JArray)saveData[SAVE_DATA_SPAWN_BLOCK_KEY];

					if (spawnBlockSaveData == null)
					{
						thisModEntry.Logger.Log("Not loading spawn block list: data was null.");
						return;
					}

					stationIdSpawnBlockList = spawnBlockSaveData.Select(id => (string)id).ToList();
					thisModEntry.Logger.Log(
						string.Format("Loaded station spawn block list: [ {0} ]",
						string.Join(", ", stationIdSpawnBlockList)));
				}
				catch (Exception e)
				{
					// TODO: what to do if loading fails?
					thisModEntry.Logger.Warning(string.Format("Loading mod data failed with exception:\n{0}", e));
				}
			}
		}

		// prevents jobs from expiring due to the player's distance from the station
		[HarmonyPatch(typeof(StationController), "ExpireAllAvailableJobsInStation")]
		class StationController_ExpireAllAvailableJobsInStation_Patch
		{
			static bool Prefix()
			{
				// skips the original method entirely when this mod is active
				return !thisModEntry.Active;
			}
		}

		// expands the distance at which the job generation trigger is rearmed
		[HarmonyPatch(typeof(StationJobGenerationRange))]
		[HarmonyPatchAll]
		class StationJobGenerationRange_AllMethods_Patch
		{
			static void Prefix(StationJobGenerationRange __instance, MethodBase __originalMethod)
			{
				try
				{
					// backup existing values before overwriting
					if (initialDistanceRegular < 1f)
					{
						initialDistanceRegular = __instance.destroyGeneratedJobsSqrDistanceRegular;
					}
					if (initialDistanceAnyJobTaken < 1f)
					{
						initialDistanceAnyJobTaken = __instance.destroyGeneratedJobsSqrDistanceAnyJobTaken;
					}

					if (thisModEntry.Active)
					{
						if (__instance.destroyGeneratedJobsSqrDistanceAnyJobTaken < 4000000f)
						{
							__instance.destroyGeneratedJobsSqrDistanceAnyJobTaken = 4000000f;
						}
						__instance.destroyGeneratedJobsSqrDistanceRegular =
							__instance.destroyGeneratedJobsSqrDistanceAnyJobTaken;
					}
					else
					{
						__instance.destroyGeneratedJobsSqrDistanceRegular = initialDistanceRegular;
						__instance.destroyGeneratedJobsSqrDistanceAnyJobTaken = initialDistanceAnyJobTaken;
					}
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during StationJobGenerationRange.{0} prefix patch:\n{1}",
						__originalMethod.Name,
						e.ToString()
					));
					OnCriticalFailure();
				}
			}
		}

		// expires a job if none of its cars are in range of the starting station on job start attempt
		[HarmonyPatch(typeof(JobValidator), "ProcessJobOverview")]
		class JobValidator_ProcessJobOverview_Patch
		{
			static void Prefix(
				List<StationController> ___allStations,
				DV.Printers.PrinterController ___bookletPrinter,
				JobOverview jobOverview)
			{
				try
				{
					if (!thisModEntry.Active)
					{
						return;
					}

					Job job = jobOverview.job;
					StationController stationController = ___allStations.FirstOrDefault(
						(StationController st) => st.logicStation.availableJobs.Contains(job)
					);

					if (___bookletPrinter.IsOnCooldown || job.State != JobState.Available || stationController == null)
					{
						return;
					}

					// expire the job if all associated cars are outside the job destruction range
					// the base method's logic will handle generating the expired report
					StationJobGenerationRange stationRange = Traverse.Create(stationController)
						.Field("stationRange")
						.GetValue<StationJobGenerationRange>();
					if (!job.tasks.Any(CheckTaskForCarsInRange(stationRange)))
					{
						job.ExpireJob();
					}
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during JobValidator.ProcessJobOverview prefix patch:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
			}

			private static Func<Task, bool> CheckTaskForCarsInRange(StationJobGenerationRange stationRange)
			{
				return (Task t) =>
				{
					if (t is ParallelTasks || t is SequentialTasks)
					{
						return Traverse.Create(t)
							.Field("tasks")
							.GetValue<IEnumerable<Task>>()
							.Any(CheckTaskForCarsInRange(stationRange));
					}
					List<Car> cars = Traverse.Create(t).Field("cars").GetValue<List<Car>>();
					Car carInRangeOfStation = cars.FirstOrDefault((Car c) =>
					{
						TrainCar trainCar = TrainCar.GetTrainCarByCarGuid(c.carGuid);
						float distance =
							(trainCar.transform.position - stationRange.stationCenterAnchor.position).sqrMagnitude;
						return trainCar != null && distance <= initialDistanceRegular;
					});
					return carInRangeOfStation != null;
				};
			}
		}

		[HarmonyPatch(typeof(StationProceduralJobsController), "TryToGenerateJobs")]
		class StationProceduralJobsController_TryToGenerateJobs_Patch
		{
			static bool Prefix(StationProceduralJobsController __instance)
			{
				if (thisModEntry.Active)
				{
					return !stationIdSpawnBlockList.Contains(__instance.stationController.logicStation.ID);
				}
				return true;
			}

			static void Postfix(StationProceduralJobsController __instance)
			{
				string stationId = __instance.stationController.logicStation.ID;
				if (!stationIdSpawnBlockList.Contains(stationId))
				{
					stationIdSpawnBlockList.Add(stationId);
				}
			}
		}

		// generates shunting unload jobs
		[HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateInChainJob")]
		class StationProceduralJobGenerator_GenerateInChainJob_Patch
		{
			static bool Prefix(
				ref JobChainController __result,
				StationController ___stationController,
				JobType startingJobType,
				bool forceFulfilledLicenseRequirements = false)
			{
				if (thisModEntry.Active)
				{
					try
					{
						if (startingJobType == JobType.ShuntingUnload)
						{
							Debug.Log("[PersistentJobs] gen in shunting unload");
							__result = ShuntingUnloadJobProceduralGenerator.GenerateShuntingUnloadJobWithCarSpawning(
								___stationController,
								forceFulfilledLicenseRequirements,
								new System.Random(Environment.TickCount));
							if (__result != null)
							{
								Debug.Log("[PersistentJobs] finalize in shunting unload");
								__result.FinalizeSetupAndGenerateFirstJob();
							}
							return false;
						}
						Debug.LogWarning(string.Format(
							"[PersistentJobs] Got unexpected JobType.{0} in {1}.{2} {3} patch. Falling back to base method.",
							startingJobType.ToString(),
							"StationProceduralJobGenerator",
							"GenerateInChainJob",
							"prefix"
						));
					}
					catch (Exception e)
					{
						thisModEntry.Logger.Error(string.Format(
							"Exception thrown during {0}.{1} {2} patch:\n{3}",
							"StationProceduralJobGenerator",
							"GenerateInChainJob",
							"prefix",
							e.ToString()
						));
						OnCriticalFailure();
					}
				}
				return true;
			}
		}

		// generates shunting load jobs & freight haul jobs
		[HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateOutChainJob")]
		class StationProceduralJobGenerator_GenerateOutChainJob_Patch
		{
			static bool Prefix(
				ref JobChainController __result,
				StationController ___stationController,
				JobType startingJobType,
				bool forceFulfilledLicenseRequirements = false)
			{
				if (thisModEntry.Active)
				{
					try
					{
						if (startingJobType == JobType.ShuntingLoad)
						{
							Debug.Log("[PersistentJobs] gen out shunting load");
							__result = ShuntingLoadJobProceduralGenerator.GenerateShuntingLoadJobWithCarSpawning(
								___stationController,
								forceFulfilledLicenseRequirements,
								new System.Random(Environment.TickCount));
							if (__result != null)
							{
								Debug.Log("[PersistentJobs] finalize out shunting load");
								__result.FinalizeSetupAndGenerateFirstJob();
							}
							return false;
						}
						else if (startingJobType == JobType.Transport)
						{
							Debug.Log("[PersistentJobs] gen out transport");
							__result = TransportJobProceduralGenerator.GenerateTransportJobWithCarSpawning(
								___stationController,
								forceFulfilledLicenseRequirements,
								new System.Random(Environment.TickCount));
							if (__result != null)
							{
								Debug.Log("[PersistentJobs] finalize out transport");
								__result.FinalizeSetupAndGenerateFirstJob();
							}
							return false;
						}
						Debug.LogWarning(string.Format(
							"[PersistentJobs] Got unexpected JobType.{0} in {1}.{2} {3} patch. Falling back to base method.",
							startingJobType.ToString(),
							"StationProceduralJobGenerator",
							"GenerateOutChainJob",
							"prefix"
						));
					}
					catch (Exception e)
					{
						thisModEntry.Logger.Error(string.Format(
							"Exception thrown during {0}.{1} {2} patch:\n{3}",
							"StationProceduralJobGenerator",
							"GenerateOutChainJob",
							"prefix",
							e.ToString()
						));
						OnCriticalFailure();
					}
				}
				return true;
			}
		}

		// unload: divert cars that can be loaded at the current station for later generation of ShuntingLoad jobs
		// load: generates a corresponding transport job
		// transport: generates a corresponding unload job
		[HarmonyPatch(typeof(JobChainControllerWithEmptyHaulGeneration), "OnLastJobInChainCompleted")]
		class JobChainControllerWithEmptyHaulGeneration_OnLastJobInChainCompleted_Patch
		{
			static void Prefix(
				JobChainControllerWithEmptyHaulGeneration __instance,
				List<StaticJobDefinition> ___jobChain,
				Job lastJobInChain)
			{
				Debug.Log("[PersistentJobs] last job chain empty haul gen");
				try
				{
					StaticJobDefinition lastJobDef = ___jobChain[___jobChain.Count - 1];
					if (lastJobDef.job != lastJobInChain)
					{
						Debug.LogError(string.Format(
							"[PersistentJobs] lastJobInChain ({0}) does not match lastJobDef.job ({1})",
							lastJobInChain.ID,
							lastJobDef.job.ID));
					}
					else if (lastJobInChain.jobType == JobType.ShuntingUnload)
					{
						Debug.Log("[PersistentJobs] checking static definition type");
						StaticShuntingUnloadJobDefinition unloadJobDef = lastJobDef as StaticShuntingUnloadJobDefinition;
						if (unloadJobDef != null)
						{
							StationController station = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[lastJobInChain.chainData.chainDestinationYardId];
							List<CargoGroup> availableCargoGroups = station.proceduralJobsRuleset.outputCargoGroups;

							Debug.Log("[PersistentJobs] diverting trainCars");
							int countCarsDiverted = 0;
							// if a trainCar set can be reused at the current station, keep them there
							for (int i = unloadJobDef.carsPerDestinationTrack.Count - 1; i >= 0; i--)
							{
								CarsPerTrack cpt = unloadJobDef.carsPerDestinationTrack[i];
								// check if there is any cargoGroup that satisfies all the cars
								if (availableCargoGroups.Any(
									cg => cpt.cars.All(
										c => Utilities.GetCargoTypesForCarType(c.carType)
											.Intersect(cg.cargoTypes)
											.Any())))
								{
									// registering the cars as jobless & removing them from carsPerDestinationTrack
									// prevents base method from generating an EmptyHaul job for them
									// they will be candidates for new jobs once the player leaves the area
									List<TrainCar> tcsToDivert = new List<TrainCar>();
									foreach(Car c in cpt.cars)
									{
										tcsToDivert.Add(TrainCar.logicCarToTrainCar[c]);
										tcsToDivert[tcsToDivert.Count - 1].UpdateJobIdOnCarPlates(string.Empty);
									}
									JobDebtController.RegisterJoblessCars(tcsToDivert);
									countCarsDiverted += tcsToDivert.Count;
									unloadJobDef.carsPerDestinationTrack.Remove(cpt);
								}
							}
							Debug.Log(string.Format("[PersistentJobs] diverted {0} trainCars", countCarsDiverted));
						}
						else
						{
							Debug.LogError("[PersistentJobs] Couldn't convert lastJobDef to " +
								"StaticShuntingUnloadJobDefinition. EmptyHaul jobs won't be generated.");
						}
					}
					else if (lastJobInChain.jobType == JobType.ShuntingLoad)
					{
						StaticShuntingLoadJobDefinition loadJobDef = lastJobDef as StaticShuntingLoadJobDefinition;
						if (loadJobDef != null)
						{
							StationController startingStation = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[loadJobDef.logicStation.ID];
							StationController destStation = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[loadJobDef.chainData.chainDestinationYardId];
							Track startingTrack = loadJobDef.destinationTrack;
							List<TrainCar> trainCars = new List<TrainCar>(__instance.trainCarsForJobChain);
							System.Random rng = new System.Random(Environment.TickCount);
							JobChainController jobChainController
								= TransportJobProceduralGenerator.GenerateTransportJobWithExistingCars(
									startingStation,
									startingTrack,
									destStation,
									trainCars,
									trainCars.Select<TrainCar, CargoType>(tc => tc.logicCar.CurrentCargoTypeInCar)
										.ToList(),
									rng
								);
							if (jobChainController != null)
							{
								foreach (TrainCar tc in jobChainController.trainCarsForJobChain)
								{
									__instance.trainCarsForJobChain.Remove(tc);
								}
								jobChainController.FinalizeSetupAndGenerateFirstJob();
								Debug.Log(string.Format(
									"[PersistentJobs] Generated job chain [{0}]: {1}",
									jobChainController.jobChainGO.name,
									jobChainController.jobChainGO));
							}
						}
						else
						{
							Debug.LogError(
								"[PersistentJobs] Couldn't convert lastJobDef to StaticShuntingLoadDefinition." +
								" Transport jobs won't be generated."
							);
						}
					}
					else if (lastJobInChain.jobType == JobType.Transport)
					{
						StaticTransportJobDefinition loadJobDef = lastJobDef as StaticTransportJobDefinition;
						if (loadJobDef != null)
						{
							StationController startingStation = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[loadJobDef.logicStation.ID];
							StationController destStation = SingletonBehaviour<LogicController>.Instance
								.YardIdToStationController[loadJobDef.chainData.chainDestinationYardId];
							Track startingTrack = loadJobDef.destinationTrack;
							List<TrainCar> trainCars = new List<TrainCar>(__instance.trainCarsForJobChain);
							System.Random rng = new System.Random(Environment.TickCount);
							JobChainController jobChainController
								= ShuntingUnloadJobProceduralGenerator.GenerateShuntingUnloadJobWithExistingCars(
									startingStation,
									startingTrack,
									destStation,
									trainCars,
									trainCars.Select<TrainCar, CargoType>(tc => tc.logicCar.CurrentCargoTypeInCar)
										.ToList(),
									rng
								);
							if (jobChainController != null)
							{
								foreach (TrainCar tc in jobChainController.trainCarsForJobChain)
								{
									__instance.trainCarsForJobChain.Remove(tc);
								}
								jobChainController.FinalizeSetupAndGenerateFirstJob();
								Debug.Log(string.Format(
									"[PersistentJobs] Generated job chain [{0}]: {1}",
									jobChainController.jobChainGO.name,
									jobChainController.jobChainGO));
							}
						}
						else
						{
							Debug.LogError(
								"[PersistentJobs] Couldn't convert lastJobDef to StaticTransportDefinition." +
								" ShuntingUnload jobs won't be generated."
							);
						}
					}
					else
					{
						Debug.LogError(string.Format(
							"[PersistentJobs] Unexpected job type: {0}. The last job in chain must be " +
							"ShuntingLoad, Transport, or ShuntingUnload for JobChainControllerWithEmptyHaulGeneration patch! " +
							"New jobs won't be generated.",
							lastJobInChain.jobType));
					}
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
							"Exception thrown during {0}.{1} {2} patch:\n{3}",
							"JobChainControllerWithEmptyHaulGeneration",
							"OnLastJobInChainCompleted",
							"prefix",
							e.ToString()));
					OnCriticalFailure();
				}
			}
		}

		// tries to generate new shunting load jobs for the train cars marked for deletion
		// failing that, the train cars are deleted
		[HarmonyPatch(typeof(UnusedTrainCarDeleter), "InstantConditionalDeleteOfUnusedCars")]
		class UnusedTrainCarDeleter_InstantConditionalDeleteOfUnusedCars_Patch
		{
			static bool Prefix(
				UnusedTrainCarDeleter __instance,
				List<TrainCar> ___unusedTrainCarsMarkedForDelete,
				Dictionary<TrainCar, CarVisitChecker> ___carVisitCheckersMap)
			{
				if (thisModEntry.Active)
				{
					try
					{
						if (___unusedTrainCarsMarkedForDelete.Count == 0)
						{
							return false;
						}

						Debug.Log("[PersistentJobs] collecting deletion candidates");
						List<TrainCar> trainCarsToDelete = new List<TrainCar>();
						for (int i = ___unusedTrainCarsMarkedForDelete.Count - 1; i >= 0; i--)
						{
							TrainCar trainCar = ___unusedTrainCarsMarkedForDelete[i];
							if (trainCar == null)
							{
								___unusedTrainCarsMarkedForDelete.RemoveAt(i);
								continue;
							}
							bool areDeleteConditionsFulfilled = Traverse.Create(__instance)
								.Method("AreDeleteConditionsFulfilled", new Type[] { typeof(TrainCar) })
								.GetValue<bool>(trainCar);
							if (areDeleteConditionsFulfilled)
							{
								trainCarsToDelete.Add(trainCar);
							}
						}
						Debug.Log(string.Format(
							"[PersistentJobs] found {0} cars marked for deletion",
							trainCarsToDelete.Count));
						if (trainCarsToDelete.Count == 0)
						{
							return false;
						}

						// ------ BEGIN JOB GENERATION ------
						// group trainCars by trainset
						Debug.Log("[PersistentJobs] grouping trainCars by trainSet");
						List<TrainCar> nonLocoTrainCarsToDelete
							= trainCarsToDelete.Where(tc => !CarTypes.IsAnyLocomotiveOrTender(tc.carType)).ToList();
						List<TrainCar> emptyNonLocoTrainCarsToDelete = nonLocoTrainCarsToDelete
							.Where(tc => tc.logicCar.CurrentCargoTypeInCar == CargoType.None
								|| tc.logicCar.LoadedCargoAmount < 0.001f)
							.ToList();
						List<TrainCar> loadedNonLocoTrainCarsToDelete = nonLocoTrainCarsToDelete
							.Where(tc => tc.logicCar.CurrentCargoTypeInCar != CargoType.None
								&& tc.logicCar.LoadedCargoAmount >= 0.001f)
							.ToList();
						Dictionary<Trainset, List<TrainCar>> emptyTrainCarsPerTrainSet
								= JobProceduralGenerationUtilities.GroupTrainCarsByTrainset(emptyNonLocoTrainCarsToDelete);
						Dictionary<Trainset, List<TrainCar>> loadedTrainCarsPerTrainSet = JobProceduralGenerationUtilities
							.GroupTrainCarsByTrainset(loadedNonLocoTrainCarsToDelete);
						Debug.Log(string.Format(
							"[PersistentJobs] found {0} empty trainSets\n" +
							"    and {1} loaded trainSets",
							emptyTrainCarsPerTrainSet.Count,
							loadedTrainCarsPerTrainSet.Count));

						// group trainCars sets by nearest stationController
						Debug.Log("[PersistentJobs] grouping trainSets by nearest station");
						Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> emptyCgsPerTcsPerSc
							= JobProceduralGenerationUtilities.GroupTrainCarSetsByNearestStation(emptyTrainCarsPerTrainSet);
						Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> loadedCgsPerTcsPerSc
							= JobProceduralGenerationUtilities.GroupTrainCarSetsByNearestStation(loadedTrainCarsPerTrainSet);
						Debug.Log(string.Format(
							"[PersistentJobs] found {0} stations for empty trainSets\n" +
							"    and {1} stations for loaded trainSets",
							emptyCgsPerTcsPerSc.Count));

						// populate possible cargoGroups per group of trainCars
						Debug.Log("[PersistentJobs] populating cargoGroups");
						JobProceduralGenerationUtilities.PopulateCargoGroupsPerTrainCarSet(emptyCgsPerTcsPerSc);
						JobProceduralGenerationUtilities.PopulateCargoGroupsPerLoadedTrainCarSet(loadedCgsPerTcsPerSc);

						// pick new jobs for the trainCars at each station
						Debug.Log("[PersistentJobs] picking jobs");
						System.Random rng = new System.Random(Environment.TickCount);
						List<(StationController, List<CarsPerTrack>, StationController, List<TrainCar>, List<CargoType>)>
							shuntingLoadJobInfos = ShuntingLoadJobProceduralGenerator
								.ComputeJobInfosFromCargoGroupsPerTrainCarSetPerStation(emptyCgsPerTcsPerSc, rng);
						List<(StationController, Track, StationController, List<TrainCar>, List<CargoType>)>
							transportJobInfos = TransportJobProceduralGenerator
							.ComputeJobInfosFromCargoGroupsPerTrainCarSetPerStation(
								loadedCgsPerTcsPerSc.Select(kv => (
										kv.Key,
										kv.Value.Where(tpl => {
											CargoGroup cg0 = tpl.Item2.FirstOrDefault();
											return cg0 != null && kv.Key.proceduralJobsRuleset.outputCargoGroups.Contains(cg0);
										}).ToList()))
									.Where(tpl => tpl.Item2.Count > 0)
									.ToDictionary(tpl => tpl.Item1, tpl => tpl.Item2),
								rng);
						List<(StationController, Track, StationController, List<TrainCar>, List<CargoType>)>
							shuntingUnloadJobInfos = ShuntingUnloadJobProceduralGenerator
							.ComputeJobInfosFromCargoGroupsPerTrainCarSetPerStation(
								loadedCgsPerTcsPerSc.Select(kv => (
										kv.Key,
										kv.Value.Where(tpl => {
											CargoGroup cg0 = tpl.Item2.FirstOrDefault();
											return cg0 != null && kv.Key.proceduralJobsRuleset.inputCargoGroups.Contains(cg0);
										}).ToList()))
									.Where(tpl => tpl.Item2.Count > 0)
									.ToDictionary(tpl => tpl.Item1, tpl => tpl.Item2),
								rng);
						Debug.Log(string.Format(
							"[PersistentJobs] chose {0} shunting load jobs,\n" +
							"    {1} transport jobs,\n" +
							"    and {2} shunting unload jobs",
							shuntingLoadJobInfos.Count,
							transportJobInfos.Count,
							shuntingUnloadJobInfos.Count));

						// try to generate jobs
						Debug.Log("[PersistentJobs] generating jobs");
						IEnumerable<JobChainController> shuntingLoadJobChainControllers
							= ShuntingLoadJobProceduralGenerator.doJobGeneration(shuntingLoadJobInfos, rng);
						IEnumerable<JobChainController> transportJobChainControllers
							= TransportJobProceduralGenerator.doJobGeneration(transportJobInfos, rng);
						IEnumerable<JobChainController> shuntingUnloadJobChainControllers
							= ShuntingUnloadJobProceduralGenerator.doJobGeneration(shuntingUnloadJobInfos, rng);
						Debug.Log(string.Format(
							"[PersistentJobs] generated {0} shunting load jobs,\n" +
							"    {1} transport jobs,\n" +
							"    and {2} shunting unload jobs",
							shuntingLoadJobChainControllers.Where(jcc => jcc != null).Count(),
							transportJobChainControllers.Where(jcc => jcc != null).Count(),
							shuntingUnloadJobChainControllers.Where(jcc => jcc != null).Count()));

						// finalize jobs & preserve job train cars
						Debug.Log("[PersistentJobs] finalizing jobs");
						int totalCarsPreserved = 0;
						foreach (JobChainController jcc in shuntingLoadJobChainControllers)
						{
							if (jcc != null)
							{
								jcc.trainCarsForJobChain.ForEach(tc => trainCarsToDelete.Remove(tc));
								totalCarsPreserved += jcc.trainCarsForJobChain.Count;
								jcc.FinalizeSetupAndGenerateFirstJob();
							}
						}
						foreach (JobChainController jcc in transportJobChainControllers)
						{
							if (jcc != null)
							{
								jcc.trainCarsForJobChain.ForEach(tc => trainCarsToDelete.Remove(tc));
								totalCarsPreserved += jcc.trainCarsForJobChain.Count;
								jcc.FinalizeSetupAndGenerateFirstJob();
							}
						}
						foreach (JobChainController jcc in shuntingUnloadJobChainControllers)
						{
							if (jcc != null)
							{
								jcc.trainCarsForJobChain.ForEach(tc => trainCarsToDelete.Remove(tc));
								totalCarsPreserved += jcc.trainCarsForJobChain.Count;
								jcc.FinalizeSetupAndGenerateFirstJob();
							}
						}

						// preserve all trainCars that are not locos
						Debug.Log("[PersistentJobs] preserving cars");
						foreach (TrainCar tc in new List<TrainCar>(trainCarsToDelete))
						{
							if (tc.playerSpawnedCar || !CarTypes.IsAnyLocomotiveOrTender(tc.carType))
							{
								trainCarsToDelete.Remove(tc);
								totalCarsPreserved += 1;
							}
						}
						Debug.Log(string.Format("[PersistentJobs] preserved {0} cars", totalCarsPreserved));
						// ------ END JOB GENERATION ------

						foreach (TrainCar tc in trainCarsToDelete)
						{
							___unusedTrainCarsMarkedForDelete.Remove(tc);
							___carVisitCheckersMap.Remove(tc);
						}
						SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(trainCarsToDelete, true);
						Debug.Log(string.Format("[PersistentJobs] deleted {0} cars", trainCarsToDelete.Count));
						return false;
					}
					catch (Exception e)
					{
						thisModEntry.Logger.Error(string.Format(
							"Exception thrown during {0}.{1} {2} patch:\n{3}",
							"UnusedTrainCarDeleter",
							"InstantConditionalDeleteOfUnusedCars",
							"prefix",
							e.ToString()
						));
						OnCriticalFailure();
					}
				}
				return true;
			}
		}

		// override/replacement for UnusedTrainCarDeleter.TrainCarsDeleteCheck coroutine
		// tries to generate new shunting load jobs for the train cars marked for deletion
		// failing that, the train cars are deleted
		public static IEnumerator TrainCarsCreateJobOrDeleteCheck(float period, float interopPeriod)
		{
			List<TrainCar> trainCarsToDelete = null;
			List<TrainCar> trainCarCandidatesForDelete = null;
			Traverse unusedTrainCarDeleterTraverser = null;
			List<TrainCar> unusedTrainCarsMarkedForDelete = null;
			Dictionary<TrainCar, DV.CarVisitChecker> carVisitCheckersMap = null;
			Traverse AreDeleteConditionsFulfilledMethod = null;
			try
			{
				trainCarsToDelete = new List<TrainCar>();
				trainCarCandidatesForDelete = new List<TrainCar>();
				unusedTrainCarDeleterTraverser = Traverse.Create(SingletonBehaviour<UnusedTrainCarDeleter>.Instance);
				unusedTrainCarsMarkedForDelete = unusedTrainCarDeleterTraverser
					.Field("unusedTrainCarsMarkedForDelete")
					.GetValue<List<TrainCar>>();
				carVisitCheckersMap = unusedTrainCarDeleterTraverser
					.Field("carVisitCheckersMap")
					.GetValue<Dictionary<TrainCar, DV.CarVisitChecker>>();
				AreDeleteConditionsFulfilledMethod
					= unusedTrainCarDeleterTraverser.Method("AreDeleteConditionsFulfilled", new Type[] { typeof(TrainCar) });
			}
			catch (Exception e)
			{
				thisModEntry.Logger.Error(string.Format(
					"Exception thrown during TrainCarsCreateJobOrDeleteCheck setup:\n{0}",
					e.ToString()
				));
				OnCriticalFailure();
			}
			for (; ; )
			{
				yield return WaitFor.SecondsRealtime(period);

				try
				{
					if (PlayerManager.PlayerTransform == null || FastTravelController.IsFastTravelling)
					{
						continue;
					}

					if (unusedTrainCarsMarkedForDelete.Count == 0)
					{
						if (carVisitCheckersMap.Count != 0)
						{
							carVisitCheckersMap.Clear();
						}
						continue;
					}
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck skip checks:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}

				Debug.Log("[PersistentJobs] collecting deletion candidates (coroutine)");
				try
				{
					trainCarCandidatesForDelete.Clear();
					for (int i = unusedTrainCarsMarkedForDelete.Count - 1; i >= 0; i--)
					{
						TrainCar trainCar = unusedTrainCarsMarkedForDelete[i];
						if (trainCar == null)
						{
							unusedTrainCarsMarkedForDelete.RemoveAt(i);
						}
						else if (AreDeleteConditionsFulfilledMethod.GetValue<bool>(trainCar))
						{
							unusedTrainCarsMarkedForDelete.RemoveAt(i);
							trainCarCandidatesForDelete.Add(trainCar);
						}
					}
					Debug.Log(string.Format(
						"[PersistentJobs] found {0} cars marked for deletion (coroutine)",
						trainCarCandidatesForDelete.Count));
					if (trainCarCandidatesForDelete.Count == 0)
					{
						continue;
					}
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck delete candidate collection:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// ------ BEGIN JOB GENERATION ------
				// group trainCars by trainset
				Debug.Log("[PersistentJobs] grouping trainCars by trainSet (coroutine)");
				Dictionary<Trainset, List<TrainCar>> emptyTrainCarsPerTrainSet = null;
				Dictionary<Trainset, List<TrainCar>> loadedTrainCarsPerTrainSet = null;
				try
				{
					List<TrainCar> nonLocoTrainCarCandidatesForDelete = trainCarCandidatesForDelete
						.Where(tc => !CarTypes.IsAnyLocomotiveOrTender(tc.carType))
						.ToList();
					List<TrainCar> emptyNonLocoTrainCarCandidatesForDelete= nonLocoTrainCarCandidatesForDelete
						.Where(tc => tc.logicCar.CurrentCargoTypeInCar == CargoType.None
							|| tc.logicCar.LoadedCargoAmount < 0.001f)
						.ToList();
					List<TrainCar> loadedNonLocoTrainCarCandidatesForDelete = nonLocoTrainCarCandidatesForDelete
						.Where(tc => tc.logicCar.CurrentCargoTypeInCar != CargoType.None
							&& tc.logicCar.LoadedCargoAmount >= 0.001f)
						.ToList();

					emptyTrainCarsPerTrainSet = JobProceduralGenerationUtilities
						.GroupTrainCarsByTrainset(emptyNonLocoTrainCarCandidatesForDelete);
					loadedTrainCarsPerTrainSet = JobProceduralGenerationUtilities
						.GroupTrainCarsByTrainset(loadedNonLocoTrainCarCandidatesForDelete);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck trainset grouping:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				Debug.Log(string.Format(
					"[PersistentJobs] found {0} empty trainSets\n" +
					"    and {1} loaded trainSets (coroutine)",
					emptyTrainCarsPerTrainSet.Count,
					loadedTrainCarsPerTrainSet.Count));

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// group trainCars sets by nearest stationController
				Debug.Log("[PersistentJobs] grouping trainSets by nearest station (coroutine)");
				Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> emptyCgsPerTcsPerSc = null;
				Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> loadedCgsPerTcsPerSc = null;
				try
				{
					emptyCgsPerTcsPerSc = JobProceduralGenerationUtilities
						.GroupTrainCarSetsByNearestStation(emptyTrainCarsPerTrainSet);
					loadedCgsPerTcsPerSc = JobProceduralGenerationUtilities
						.GroupTrainCarSetsByNearestStation(loadedTrainCarsPerTrainSet);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck station grouping:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				Debug.Log(string.Format(
					"[PersistentJobs] found {0} stations for empty trainSets\n" +
					"    and {1} stations for loaded trainSets (coroutine)",
					emptyCgsPerTcsPerSc.Count,
					loadedCgsPerTcsPerSc.Count));

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// populate possible cargoGroups per group of trainCars
				Debug.Log("[PersistentJobs] populating cargoGroups (coroutine)");
				try
				{
					JobProceduralGenerationUtilities.PopulateCargoGroupsPerTrainCarSet(emptyCgsPerTcsPerSc);
					JobProceduralGenerationUtilities.PopulateCargoGroupsPerLoadedTrainCarSet(loadedCgsPerTcsPerSc);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck cargoGroup population:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// pick new jobs for the trainCars at each station
				Debug.Log("[PersistentJobs] picking jobs (coroutine)");
				System.Random rng = new System.Random(Environment.TickCount);
				List<(StationController, List<CarsPerTrack>, StationController, List<TrainCar>, List<CargoType>)>
					shuntingLoadJobInfos = null;
				List<(StationController, Track, StationController, List<TrainCar>, List<CargoType>)>
					transportJobInfos = null;
				List<(StationController, Track, StationController, List<TrainCar>, List<CargoType>)>
					shuntingUnloadJobInfos = null;
				try
				{
					shuntingLoadJobInfos = ShuntingLoadJobProceduralGenerator
						.ComputeJobInfosFromCargoGroupsPerTrainCarSetPerStation(emptyCgsPerTcsPerSc, rng);

					transportJobInfos = TransportJobProceduralGenerator
						.ComputeJobInfosFromCargoGroupsPerTrainCarSetPerStation(
							loadedCgsPerTcsPerSc.Select(kv => (
									kv.Key,
									kv.Value.Where(tpl => {
										CargoGroup cg0 = tpl.Item2.FirstOrDefault();
										return cg0 != null && kv.Key.proceduralJobsRuleset.outputCargoGroups.Contains(cg0);
									}).ToList()))
								.Where(tpl => tpl.Item2.Count > 0)
								.ToDictionary(tpl => tpl.Item1, tpl => tpl.Item2),
							rng);

					shuntingUnloadJobInfos = ShuntingUnloadJobProceduralGenerator
						.ComputeJobInfosFromCargoGroupsPerTrainCarSetPerStation(
							loadedCgsPerTcsPerSc.Select(kv => (
									kv.Key,
									kv.Value.Where(tpl => {
										CargoGroup cg0 = tpl.Item2.FirstOrDefault();
										return cg0 != null && kv.Key.proceduralJobsRuleset.inputCargoGroups.Contains(cg0);
									}).ToList()))
								.Where(tpl => tpl.Item2.Count > 0)
								.ToDictionary(tpl => tpl.Item1, tpl => tpl.Item2),
							rng);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck job info selection:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				Debug.Log(string.Format(
					"[PersistentJobs] chose {0} shunting load jobs,\n" +
					"    {1} transport jobs,\n" +
					"    and {2} shunting unload jobs (coroutine)",
					shuntingLoadJobInfos.Count,
					transportJobInfos.Count,
					shuntingUnloadJobInfos.Count));

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// try to generate jobs
				Debug.Log("[PersistentJobs] generating jobs (coroutine)");
				IEnumerable<JobChainController> shuntingLoadJobChainControllers = null;
				IEnumerable<JobChainController> transportJobChainControllers = null;
				IEnumerable<JobChainController> shuntingUnloadJobChainControllers = null;
				try
				{
					shuntingLoadJobChainControllers
						= ShuntingLoadJobProceduralGenerator.doJobGeneration(shuntingLoadJobInfos, rng);
					transportJobChainControllers
						= TransportJobProceduralGenerator.doJobGeneration(transportJobInfos, rng);
					shuntingUnloadJobChainControllers
						= ShuntingUnloadJobProceduralGenerator.doJobGeneration(shuntingUnloadJobInfos, rng);
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck job generation:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				Debug.Log(string.Format(
					"[PersistentJobs] generated {0} shunting load jobs,\n" +
					"    {1} transport jobs,\n" +
					"    and {2} shunting unload jobs (coroutine)",
					shuntingLoadJobChainControllers.Where(jcc => jcc != null).Count(),
					transportJobChainControllers.Where(jcc => jcc != null).Count(),
					shuntingUnloadJobChainControllers.Where(jcc => jcc != null).Count()));

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// finalize jobs & preserve job train cars
				Debug.Log("[PersistentJobs] finalizing jobs (coroutine)");
				int totalCarsPreserved = 0;
				try
				{
					foreach (JobChainController jcc in shuntingLoadJobChainControllers)
					{
						if (jcc != null)
						{
							jcc.trainCarsForJobChain.ForEach(tc =>
							{
								// force job's train cars to not be treated as player spawned
								// DV will complain if we don't do this
								Utilities.ConvertPlayerSpawnedTrainCar(tc);
								trainCarCandidatesForDelete.Remove(tc);
							});
							totalCarsPreserved += jcc.trainCarsForJobChain.Count;
							jcc.FinalizeSetupAndGenerateFirstJob();
						}
					}

					foreach (JobChainController jcc in transportJobChainControllers)
					{
						if (jcc != null)
						{
							jcc.trainCarsForJobChain.ForEach(tc =>
							{
								// force job's train cars to not be treated as player spawned
								// DV will complain if we don't do this
								Utilities.ConvertPlayerSpawnedTrainCar(tc);
								trainCarCandidatesForDelete.Remove(tc);
							});
							totalCarsPreserved += jcc.trainCarsForJobChain.Count;
							jcc.FinalizeSetupAndGenerateFirstJob();
						}
					}

					foreach (JobChainController jcc in shuntingUnloadJobChainControllers)
					{
						if (jcc != null)
						{
							jcc.trainCarsForJobChain.ForEach(tc =>
							{
								// force job's train cars to not be treated as player spawned
								// DV will complain if we don't do this
								Utilities.ConvertPlayerSpawnedTrainCar(tc);
								trainCarCandidatesForDelete.Remove(tc);
							});
							totalCarsPreserved += jcc.trainCarsForJobChain.Count;
							jcc.FinalizeSetupAndGenerateFirstJob();
						}
					}
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck trainCar preservation:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}

				yield return WaitFor.SecondsRealtime(interopPeriod);

				// preserve all trainCars that are not locomotives
				Debug.Log("[PersistentJobs] preserving cars (coroutine)");
				try
				{
					foreach (TrainCar tc in new List<TrainCar>(trainCarCandidatesForDelete))
					{
						if (tc.playerSpawnedCar || !CarTypes.IsAnyLocomotiveOrTender(tc.carType))
						{
							trainCarCandidatesForDelete.Remove(tc);
							unusedTrainCarsMarkedForDelete.Add(tc);
							totalCarsPreserved += 1;
						}
					}
					Debug.Log(string.Format("[PersistentJobs] preserved {0} cars (coroutine)", totalCarsPreserved));
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck trainCar preservation:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
				// ------ END JOB GENERATION ------

				yield return WaitFor.SecondsRealtime(interopPeriod);

				Debug.Log("[PersistentJobs] deleting cars (coroutine)");
				try
				{
					trainCarsToDelete.Clear();
					for (int j = trainCarCandidatesForDelete.Count - 1; j >= 0; j--)
					{
						TrainCar trainCar2 = trainCarCandidatesForDelete[j];
						if (trainCar2 == null)
						{
							trainCarCandidatesForDelete.RemoveAt(j);
						}
						else if (AreDeleteConditionsFulfilledMethod.GetValue<bool>(trainCar2))
						{
							trainCarCandidatesForDelete.RemoveAt(j);
							carVisitCheckersMap.Remove(trainCar2);
							trainCarsToDelete.Add(trainCar2);
						}
						else
						{
							Debug.LogWarning(string.Format(
								"Returning {0} to unusedTrainCarsMarkedForDelete list. PlayerTransform was outside" +
								" of DELETE_SQR_DISTANCE_FROM_TRAINCAR range of train car, but after short period it" +
								" was back in range!",
								trainCar2.name
							));
							trainCarCandidatesForDelete.RemoveAt(j);
							unusedTrainCarsMarkedForDelete.Add(trainCar2);
						}
					}
					if (trainCarsToDelete.Count != 0)
					{
						SingletonBehaviour<CarSpawner>.Instance
							.DeleteTrainCars(new List<TrainCar>(trainCarsToDelete), false);
					}
					Debug.Log(string.Format("[PersistentJobs] deleted {0} cars (coroutine)", trainCarsToDelete.Count));
				}
				catch (Exception e)
				{
					thisModEntry.Logger.Error(string.Format(
						"Exception thrown during TrainCarsCreateJobOrDeleteCheck car deletion:\n{0}",
						e.ToString()
					));
					OnCriticalFailure();
				}
			}
		}

		// chooses the shortest track with enough space (instead of the first track found)
		[HarmonyPatch(typeof(YardTracksOrganizer), "GetTrackThatHasEnoughFreeSpace")]
		class YardTracksOrganizer_GetTrackThatHasEnoughFreeSpace_Patch
		{
			static bool Prefix(YardTracksOrganizer __instance, ref Track __result, List<Track> tracks, float requiredLength)
			{
				if (thisModEntry.Active)
				{
					Debug.Log("[PersistentJobs] getting random track with free space");
					try
					{
						__result = null;
						List<Track> tracksWithFreeSpace = new List<Track>();
						foreach (Track track in tracks)
						{
							double freeSpaceOnTrack = __instance.GetFreeSpaceOnTrack(track);
							if (freeSpaceOnTrack > (double)requiredLength)
							{
								tracksWithFreeSpace.Add(track);
							}
						}
						if (tracksWithFreeSpace.Count > 0)
						{
							__result = Utilities.GetRandomFromEnumerable(
								tracksWithFreeSpace,
								new System.Random(Environment.TickCount));
						}
						return false;
					}
					catch (Exception e)
					{
						Debug.LogWarning(string.Format(
							"[PersistentJobs] Exception thrown during {0}.{1} {2} patch:\n{3}\nFalling back on base method.",
							"YardTracksOrganizer",
							"GetTrackThatHasEnoughFreeSpace",
							"prefix",
							e.ToString()));
					}
				}
				return true;
			}
		}
	}
}