﻿using System.Collections.Generic;
using System.Linq;
using System;
using UnityEngine;
using DV.Logic.Job;
using HarmonyLib;

namespace PersistentJobsMod
{
	class JobProceduralGenerationUtilities
	{
		public static Dictionary<Trainset, List<TrainCar>> GroupTrainCarsByTrainset(List<TrainCar> trainCars)
		{
			Dictionary<Trainset, List<TrainCar>> trainCarsPerTrainSet = new Dictionary<Trainset, List<TrainCar>>();
			foreach (TrainCar tc in trainCars)
			{
				// TODO: to skip player spawned cars or to not?
				if (tc != null)
				{
					if (!trainCarsPerTrainSet.ContainsKey(tc.trainset))
					{
						trainCarsPerTrainSet.Add(tc.trainset, new List<TrainCar>());
					}
					trainCarsPerTrainSet[tc.trainset].Add(tc);
				}
			}
			return trainCarsPerTrainSet;
		}

		// cargoGroup lists will be unpopulated; use PopulateCargoGroupsPerTrainCarSet to fill in this data
		public static Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>>
			GroupTrainCarSetsByNearestStation(Dictionary<Trainset, List<TrainCar>> trainCarsPerTrainSet)
		{
			IEnumerable<StationController> stationControllers
				= SingletonBehaviour<LogicController>.Instance.YardIdToStationController.Values;
			Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc
						= new Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>>();
			float abandonmentThreshold = 1.2f * Main.DVJobDestroyDistanceRegular;
			Debug.Log(string.Format(
				"[PersistentJobs] station grouping: # of trainSets: {0}, # of stations: {1}",
				trainCarsPerTrainSet.Values.Count,
				stationControllers.Count()));
			foreach (List<TrainCar> tcs in trainCarsPerTrainSet.Values)
			{
				SortedList<float, StationController> stationsByDistance
					= new SortedList<float, StationController>();
				foreach (StationController sc in stationControllers)
				{
					// since all trainCars in the trainset are coupled,
					// use the position of the first one to approximate the position of the trainset
					Vector3 trainPosition = tcs[0].gameObject.transform.position;
					Vector3 stationPosition = sc.gameObject.transform.position;
					float distance = (trainPosition - stationPosition).sqrMagnitude;
					/*Debug.Log(string.Format(
						"[PersistentJobs] station grouping: train position {0}, station position {1}, " +
						"distance {2:F}, threshold {3:F}",
						trainPosition,
						stationPosition,
						distance,
						abandonmentThreshold));*/
					// only create jobs for trainCars within a reasonable range of a station
					if (distance < abandonmentThreshold)
					{
						stationsByDistance.Add(distance, sc);
					}
				}
				if (stationsByDistance.Count == 0)
				{
					// trainCars not near any station; abandon them
					Debug.Log("[PersistentJobs] station grouping: train not near any station; abandoning train");
					continue;
				}
				// the first station is the closest
				KeyValuePair<float, StationController> closestStation = stationsByDistance.ElementAt(0);
				if (!cgsPerTcsPerSc.ContainsKey(closestStation.Value))
				{
					cgsPerTcsPerSc.Add(closestStation.Value, new List<(List<TrainCar>, List<CargoGroup>)>());
				}
				Debug.Log(string.Format(
					"[PersistentJobs] station grouping: assigning train to {0} with distance {1:F}",
					closestStation.Value,
					closestStation.Key));
				cgsPerTcsPerSc[closestStation.Value].Add((tcs, new List<CargoGroup>()));
			}
			return cgsPerTcsPerSc;
		}

		public static void PopulateCargoGroupsPerTrainCarSet(
			Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc)
		{
			foreach (StationController sc in cgsPerTcsPerSc.Keys)
			{
				foreach ((List<TrainCar>, List<CargoGroup>) cgsPerTcs in cgsPerTcsPerSc[sc])
				{
					if (cgsPerTcs.Item2.Count > 0)
					{
						Debug.LogWarning(
							"Unexpected CargoGroup data in PopulateCargoGroupsPerTrainCarSet! Proceding to overwrite."
						);
						cgsPerTcs.Item2.Clear();
					}

					foreach (CargoGroup cg in sc.proceduralJobsRuleset.outputCargoGroups)
					{
						// ensure all trainCars will have at least one cargoType to haul
						IEnumerable<IEnumerable<CargoType>> outboundCargoTypesPerTrainCar
							= (from tc in cgsPerTcs.Item1
							   select Utilities.GetCargoTypesForCarType(tc.carType).Intersect(cg.cargoTypes));
						if (outboundCargoTypesPerTrainCar.All(cgs => cgs.Count() > 0))
						{
							cgsPerTcs.Item2.Add(cg);
						}
					}
				}
			}
		}

		public static Dictionary<StationController, List<List<TrainCar>>> ExtractEmptyHaulTrainSets(
			Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc)
		{
			Dictionary<StationController, List<List<TrainCar>>> tcsPerSc
				= new Dictionary<StationController, List<List<TrainCar>>>();

			foreach (StationController sc in cgsPerTcsPerSc.Keys)
			{
				// need to copy the list for iteration b/c we'll be editing the list during iteration
				var cgsPerTcsCopy = new List<(List<TrainCar>, List<CargoGroup>)>(cgsPerTcsPerSc[sc]);
				foreach ((List<TrainCar>, List<CargoGroup>) cgsPerTcs in cgsPerTcsCopy)
				{
					// no cargo groups indicates a train car type that cannot carry cargo from its nearest station
					// extract it to have an empty haul job generated for it
					if (cgsPerTcs.Item2.Count == 0)
					{
						if (!tcsPerSc.ContainsKey(sc))
						{
							tcsPerSc.Add(sc, new List<List<TrainCar>>());
						}

						tcsPerSc[sc].Add(cgsPerTcs.Item1);
						cgsPerTcsPerSc[sc].Remove(cgsPerTcs);
					}
				}
			}

			return tcsPerSc;
		}

		public static void PopulateCargoGroupsPerLoadedTrainCarSet(
			Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> cgsPerTcsPerSc)
		{
			foreach (StationController sc in cgsPerTcsPerSc.Keys)
			{
				foreach ((List<TrainCar>, List<CargoGroup>) cgsPerTcs in cgsPerTcsPerSc[sc])
				{
					if (cgsPerTcs.Item2.Count > 0)
					{
						Debug.LogWarning(
							"Unexpected CargoGroup data in PopulateCargoGroupsPerTrainCarSet! Proceding to overwrite."
						);
						cgsPerTcs.Item2.Clear();
					}

					// transport jobs
					foreach (CargoGroup cg in sc.proceduralJobsRuleset.outputCargoGroups)
					{
						// ensure all trainCars are loaded with a cargoType from the cargoGroup
						if (cgsPerTcs.Item1.All(tc => cg.cargoTypes.Contains(tc.logicCar.CurrentCargoTypeInCar)))
						{
							cgsPerTcs.Item2.Add(cg);
						}
					}

					// it shouldn't happen that both input and output cargo groups match loaded cargo
					// but, just in case, skip trying input groups if any output groups have been found
					if (cgsPerTcs.Item2.Count > 0)
					{
						continue;
					}

					// shunting unload jobs
					foreach (CargoGroup cg in sc.proceduralJobsRuleset.inputCargoGroups)
					{
						// ensure all trainCars are loaded with a cargoType from the cargoGroup
						if (cgsPerTcs.Item1.All(tc => cg.cargoTypes.Contains(tc.logicCar.CurrentCargoTypeInCar)))
						{
							cgsPerTcs.Item2.Add(cg);
						}
					}
				}
			}
		}

		public static List<JobChainController> TryToGeneratePassengerJobs(Dictionary<StationController, List<(List<TrainCar>, List<CargoGroup>)>> tcsPerSc)
        {
			if (Main.paxEntry?.Active != true) { return new List<JobChainController>(); }

			var created = new List<JobChainController>();
			foreach (StationController sc in tcsPerSc.Keys)
			{
				var generator = sc.ProceduralJobsController.gameObject
					.GetComponent(Main.paxEntry.Assembly.GetType("PassengerJobsMod.PassengerJobGenerator"));
				var generatorTraverse = Traverse.Create(generator);
				// TODO: create a logistic haul to a randomly selected passenger station instead
				if (generator == null) { continue; }

				foreach (var cgsPerTcs in tcsPerSc[sc])
                {
					try
					{
						var tcs = cgsPerTcs.Item1;
						var track = tcs[0].logicCar.CurrentTrack;
						object tcsPerLt = AccessTools.Constructor(
								Main.paxEntry.Assembly.GetType("PassengerJobsMod.TrainCarsPerLogicTrack"),
								new Type[] { typeof(Track), typeof(IEnumerable<TrainCar>) })
							.Invoke(new object[] { track, tcs });
						var isCommuter = tcs.Count <= generatorTraverse.Field("MAX_CARS_COMMUTE").GetValue<int>();

						// convert player spawned cars; DV will complain if we don't do this
						// unfortunately we must do this before attempting generation b/c the generate methods call jcc.finalize
						foreach (var tc in tcs)
                        {
							Utilities.ConvertPlayerSpawnedTrainCar(tc);
                        }

						JobChainController jcc;
						if (isCommuter)
						{
							jcc = generatorTraverse.Method("GenerateNewCommuterRun", new object[] { tcsPerLt }).GetValue<JobChainController>();
						}
						else
						{
							jcc = generatorTraverse.Method("GenerateNewTransportJob", new object[] { tcsPerLt }).GetValue<JobChainController>();
						}

						if (jcc != null) { created.Add(jcc); }
					}
					catch (Exception e)
                    {
						Main.modEntry.Logger.Error($"Error while trying to create passenger job:\n{e}");
                    }
                }
			}

			return created;
		}
	}
}
