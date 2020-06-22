﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityModManagerNet;
using Harmony12;
using System.Reflection;
using DV.Logic.Job;

namespace PersistentJobsMod
{
    static class Main
    {
        private static UnityModManager.ModEntry thisModEntry;
        private static bool isModBroken = false;
        private static float initialDistanceRegular = 0f;
        private static float initialDistanceAnyJobTaken = 0f;

        static void Load(UnityModManager.ModEntry modEntry)
        {
            modEntry.Logger.Log("PersistenJobsMod.Load");
            var harmony = HarmonyInstance.Create(modEntry.Info.Id);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            modEntry.OnToggle = OnToggle;
            thisModEntry = modEntry;
        }

        static bool OnToggle(UnityModManager.ModEntry modEntry, bool isTogglingOn)
        {
            modEntry.Logger.Log("PersistenJobsMod.OnToggle: " + isTogglingOn.ToString());

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
                SingletonBehaviour<UnusedTrainCarDeleter>.Instance
                    .StartCoroutine(TrainCarsCreateJobOrDeleteCheck(Mathf.Max(carsCheckPeriod.Value, 1.0f)));
            }
            else
            {
                SingletonBehaviour<UnusedTrainCarDeleter>.Instance.StartCoroutine(
                    SingletonBehaviour<UnusedTrainCarDeleter>.Instance.TrainCarsDeleteCheck(carsCheckPeriod.Value)
                );
            }

            if (isModBroken)
            {
                return !isTogglingOn;
            }
            return true;
        }

        static void OnCriticalFailure()
        {
            isModBroken = true;
            thisModEntry.Active = false;
            thisModEntry.Logger.Critical("Deactivating mod PersistentJobs due to critical failure!");
            thisModEntry.Logger.Warning("You can reactivate PersistentJobs by restarting the game, but this failure " +
                "type likely indicates an incompatibility between the mod and a recent game update. Please search the " +
                "mod's Github issue tracker for a relevant report. If none is found, please open one. Include the" +
                "exception message printed above and your game's current build number.");
        }

        [HarmonyPatch(typeof(StationController), "ExpireAllAvailableJobsInStation")]
        class StationController_ExpireAllAvailableJobsInStation_Patch
        {
            // prevents jobs from expiring due to the player's distance from the station
            static bool Prefix()
            {
                // skips the original method entirely when this mod is active
                return !thisModEntry.Active;
            }
        }

        [HarmonyPatch(typeof(StationJobGenerationRange))]
        [HarmonyPatchAll]
        class StationJobGenerationRange_AllMethods_Patch
        {
            // expands the distance at which the job generation trigger is rearmed
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
                        e.Message
                    ));
                    OnCriticalFailure();
                }
            }
        }

        [HarmonyPatch(typeof(JobValidator), "ProcessJobOverview")]
        class JobValidator_ProcessJobOverview_Patch
        {
            // expires a job if none of its cars are in range of the starting station on job start attempt
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
                    Task taskWithCarsInRangeOfStation = job.tasks.FirstOrDefault((Task t) =>
                    {
                        List<Car> cars = Traverse.Create(t).Field("cars").GetValue<List<Car>>();
                        Car carInRangeOfStation = cars.FirstOrDefault((Car c) =>
                        {
                            TrainCar trainCar = TrainCar.GetTrainCarByCarGuid(c.carGuid);
                            float distance = 
                                (trainCar.transform.position - stationRange.stationCenterAnchor.position).sqrMagnitude;
                            return trainCar != null && distance <= initialDistanceRegular;
                        });
                        return carInRangeOfStation != null;
                    });
                    if (taskWithCarsInRangeOfStation == null)
                    {
                        job.ExpireJob();
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during JobValidator.ProcessJobOverview prefix patch:\n{0}",
                        e.Message
                    ));
                    OnCriticalFailure();
                }
            }
        }

        [HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateJobChain")]
        class StationProceduralJobGenerator_GenerateJobChain_Patch
        {
            // copied from the patched method; may help keep mod stable across game updates
            static bool Prefix(
                System.Random rng,
                bool forceJobWithLicenseRequirementFulfilled,
                ref StationProceduralJobGenerator __instance,
                ref JobChainController __result,
                StationProceduralJobsRuleset ___generationRuleset,
                ref System.Random ___currentRng,
                YardTracksOrganizer ___yto,
                Yard ___stYard)
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        if (
                            !___generationRuleset.loadStartingJobSupported && 
                            !___generationRuleset.haulStartingJobSupported && 
                            !___generationRuleset.unloadStartingJobSupported && 
                            !___generationRuleset.emptyHaulStartingJobSupported)
                        {
                            return true;
                        }
                        ___currentRng = rng;
                        List<JobType> spawnableJobTypes = new List<JobType>();
                        if (___generationRuleset.loadStartingJobSupported)
                        {
                            spawnableJobTypes.Add(JobType.ShuntingLoad);
                        }
                        if (___generationRuleset.emptyHaulStartingJobSupported)
                        {
                            spawnableJobTypes.Add(JobType.EmptyHaul);
                        }
                        int countOutTracksAvailable = ___yto.FilterOutOccupiedTracks(___stYard.TransferOutTracks).Count;
                        if (___generationRuleset.haulStartingJobSupported && countOutTracksAvailable > 0)
                        {
                            spawnableJobTypes.Add(JobType.Transport);
                        }
                        int countInTracksAvailable = ___yto.FilterOutReservedTracks(
                            ___yto.FilterOutOccupiedTracks(___stYard.TransferInTracks)
                        ).Count;
                        if (___generationRuleset.unloadStartingJobSupported && countInTracksAvailable > 0)
                        {
                            spawnableJobTypes.Add(JobType.ShuntingUnload);
                        }
                        JobChainController jobChainController = null;
                        if (forceJobWithLicenseRequirementFulfilled)
                        {
                            if (
                                spawnableJobTypes.Contains(JobType.Transport) && 
                                LicenseManager.IsJobLicenseAcquired(JobLicenses.FreightHaul))
                            {
                                jobChainController = Traverse.Create(__instance)
                                    .Method("GenerateOutChainJob")
                                    .GetValue<JobChainController>(JobType.Transport, true);
                                if (jobChainController != null)
                                {
                                    __result = jobChainController;
                                    return false;
                                }
                            }
                            if (
                                spawnableJobTypes.Contains(JobType.EmptyHaul) && 
                                LicenseManager.IsJobLicenseAcquired(JobLicenses.LogisticalHaul))
                            {
                                jobChainController = Traverse.Create(__instance)
                                    .Method("GenerateEmptyHaul")
                                    .GetValue<JobChainController>(true);
                                if (jobChainController != null)
                                {
                                    __result = jobChainController;
                                    return false;
                                }
                            }
                            if (
                                spawnableJobTypes.Contains(JobType.ShuntingLoad) && 
                                LicenseManager.IsJobLicenseAcquired(JobLicenses.Shunting))
                            {
                                jobChainController = Traverse.Create(__instance)
                                    .Method("GenerateOutChainJob")
                                    .GetValue<JobChainController>(JobType.ShuntingLoad, true);
                                if (jobChainController != null)
                                {
                                    __result = jobChainController;
                                    return false;
                                }
                            }
                            if (
                                spawnableJobTypes.Contains(JobType.ShuntingUnload) && 
                                LicenseManager.IsJobLicenseAcquired(JobLicenses.Shunting))
                            {
                                jobChainController = Traverse.Create(__instance)
                                    .Method("GenerateInChainJob")
                                    .GetValue<JobChainController>(JobType.ShuntingUnload, true);
                                if (jobChainController != null)
                                {
                                    __result = jobChainController;
                                    return false;
                                }
                            }
                            __result = null;
                            return false;
                        }
                        if (
                            spawnableJobTypes.Contains(JobType.Transport) && 
                            countOutTracksAvailable > Mathf.FloorToInt(0.399999976f * (float)___stYard.TransferOutTracks.Count))
                        {
                            JobType startingJobType = JobType.Transport;
                            jobChainController = Traverse.Create(__instance)
                                .Method("GenerateOutChainJob")
                                .GetValue<JobChainController>(startingJobType, false);
                        }
                        else
                        {
                            if (spawnableJobTypes.Count == 0)
                            {
                                __result = null;
                                return false;
                            }
                            JobType startingJobType = Traverse.Create(__instance)
                                .Method("GetRandomFromList")
                                .GetValue<JobType>(spawnableJobTypes);
                            switch (startingJobType)
                            {
                                case JobType.ShuntingLoad:
                                case JobType.Transport:
                                    jobChainController = Traverse.Create(__instance)
                                        .Method("GenerateOutChainJob")
                                        .GetValue<JobChainController>(startingJobType, false);
                                    break;
                                case JobType.ShuntingUnload:
                                    jobChainController = Traverse.Create(__instance)
                                        .Method("GenerateInChainJob")
                                        .GetValue<JobChainController>(startingJobType, false);
                                    break;
                                case JobType.EmptyHaul:
                                    jobChainController = Traverse.Create(__instance)
                                        .Method("GenerateEmptyHaul")
                                        .GetValue<JobChainController>(false);
                                    break;
                            }
                        }
                        ___currentRng = null;
                        __result = jobChainController;
                        return false;
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "StationProceduralJobGenerator",
                            "GenerateJobChain",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateInChainJob")]
        class StationProceduralJobGenerator_GenerateInChainJob_Patch
        {
            // generates shunting unload jobs
            static bool Prefix()
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        // TODO: implement this!
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "StationProceduralJobGenerator",
                            "GenerateInChainJob",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateOutChainJob")]
        class StationProceduralJobGenerator_GenerateOutChainJob_Patch
        {
            // generates shunting load jobs & freight haul jobs
            static bool Prefix()
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        // TODO: implement this!
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "StationProceduralJobGenerator",
                            "GenerateOutChainJob",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(StationProceduralJobGenerator), "GenerateEmptyHaul")]
        class StationProceduralJobGenerator_GenerateEmptyHaul_Patch
        {
            // generates logistical haul jobs
            static bool Prefix()
            {
                if (thisModEntry.Active)
                {
                    try
                    {
                        // TODO: implement this!
                    }
                    catch (Exception e)
                    {
                        thisModEntry.Logger.Error(string.Format(
                            "Exception thrown during {0}.{1} {2} patch:\n{3}",
                            "StationProceduralJobGenerator",
                            "GenerateEmptyHaul",
                            "prefix",
                            e.Message
                        ));
                        OnCriticalFailure();
                    }
                }
                return true;
            }
        }

        // override/replacement for UnusedTrainCarDeleter.TrainCarsDeleteCheck coroutine
        public static IEnumerator TrainCarsCreateJobOrDeleteCheck(float period)
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
                AreDeleteConditionsFulfilledMethod = unusedTrainCarDeleterTraverser.Method("AreDeleteConditionsFulfilled");
            }
            catch (Exception e)
            {
                thisModEntry.Logger.Error(string.Format(
                    "Exception thrown during TrainCarsCreateJobOrDeleteCheck setup:\n{0}",
                    e.Message
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
                        e.Message
                    ));
                    OnCriticalFailure();
                }

                try
                {
                    // TODO: add job creation logic
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during TrainCarsCreateJobOrDeleteCheck job creation:\n{0}",
                        e.Message
                    ));
                    OnCriticalFailure();
                }

                yield return WaitFor.SecondsRealtime(period);

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
                    if (trainCarCandidatesForDelete.Count == 0)
                    {
                        continue;
                    }
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during TrainCarsCreateJobOrDeleteCheck delete candidate collection:\n{0}",
                        e.Message
                    ));
                    OnCriticalFailure();
                }

                yield return WaitFor.SecondsRealtime(period);

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
                }
                catch (Exception e)
                {
                    thisModEntry.Logger.Error(string.Format(
                        "Exception thrown during TrainCarsCreateJobOrDeleteCheck car deletion:\n{0}",
                        e.Message
                    ));
                    OnCriticalFailure();
                }
            }
        }
    }
}