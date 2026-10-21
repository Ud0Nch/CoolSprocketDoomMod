using System;
using Sprocket.DamageModelling;
using Sprocket.Landmines;
using Sprocket.Vehicles;
using UnityEngine;

namespace SprocketDoomMod;

internal static class DoomBallistics
{
    private const float MaxRayDistance = 2500f;
    private const float MuzzleOffsetMeters = 0.45f;
    private const float MinHitDistanceMeters = 0.15f;

    private const int BuckshotPellets = 8;
    private const float BuckshotSpreadDegrees = 1.65f;
    private const float BuckshotCaliberScale = 0.65f;
    private const float BuckshotMinCaliberMm = 90f;

    private const float ShellMinDiameterMeters = 0.02f;
    private const float ShellDensityKgPerM3 = 14800f;
    private const float ShellLengthCalibers = 3.8f;
    private const float ShellMinMassKg = 25f;
    private const float ShellMaxNormalizationDegrees = 14f;
    private const ushort ShellPenetratorConstant = 1800;
    private const float ShellStartOffsetMeters = 0.35f;

    private const float BlastPowerPerCaliberSquared = 0.22f;
    private const float BlastMinPower = 500f;
    private const float BlastStandoffMeters = 0.06f;
    private const float BlastPiercingModifier = 12f;
    private const float BlastBluntModifier = 10f;
    private const float BlastThermalModifier = 9f;
    private const float BlastPressureModifier = 10f;

    private const float ImpulseReferenceCaliberMm = 200f;
    private const float ImpulseReferenceVelocityMs = 2000f;
    private const float ImpulseBase = 165000f;
    private const float ImpulseMinScale = 0.2f;
    private const float ImpulseMaxScale = 5f;

    private const string UnknownTargetName = "ARMOR_UNIT";

    internal static void FireSuperShotgun(
        Camera doomCamera,
        Collider? ignoreCollider,
        float caliberMm,
        float muzzleVelocityMs,
        bool buckshotMode)
    {
        var origin = doomCamera.transform.position + doomCamera.transform.forward * MuzzleOffsetMeters;
        var baseForward = doomCamera.transform.forward;

        var anyTankHit = false;
        var bestHitDistance = float.MaxValue;
        VehicleBehaviour? bestHitTank = null;
        var bestHitName = UnknownTargetName;

        if (!buckshotMode)
        {
            if (FireSingleRay(origin, baseForward, ignoreCollider, caliberMm, muzzleVelocityMs, true,
                out var hitTank, out var hitName, out var hitDistance))
            {
                anyTankHit = true;
                bestHitDistance = hitDistance;
                bestHitTank = hitTank;
                bestHitName = hitName;
            }
        }
        else
        {
            var pelletCaliberMm = Mathf.Max(BuckshotMinCaliberMm, caliberMm * BuckshotCaliberScale);
            for (var pellet = 0; pellet < BuckshotPellets; pellet++)
            {
                var direction = baseForward;
                if (pellet > 0)
                {
                    var circle = UnityEngine.Random.insideUnitCircle * BuckshotSpreadDegrees;
                    var spread =
                        Quaternion.AngleAxis(circle.x, doomCamera.transform.up) *
                        Quaternion.AngleAxis(circle.y, doomCamera.transform.right);
                    direction = (spread * baseForward).normalized;
                }

                if (FireSingleRay(origin, direction, ignoreCollider, pelletCaliberMm, muzzleVelocityMs, pellet == 0,
                    out var hitTank, out var hitName, out var hitDistance))
                {
                    anyTankHit = true;
                    if (hitDistance < bestHitDistance || bestHitTank == null)
                    {
                        bestHitDistance = hitDistance;
                        bestHitTank = hitTank;
                        bestHitName = hitName;
                    }
                }
            }
        }

        if (!anyTankHit)
            return;

        DoomController.NotifyTankHit(bestHitDistance, caliberMm, bestHitName, bestHitTank);
        if (bestHitTank != null)
            DoomController.CheckAndNotifyTankDestroyed(bestHitTank);
    }

    private static bool FireSingleRay(
        Vector3 origin,
        Vector3 direction,
        Collider? ignoreCollider,
        float caliberMm,
        float muzzleVelocityMs,
        bool isPrimaryRay,
        out VehicleBehaviour? hitTank,
        out string hitVehicleName,
        out float hitDistance)
    {
        hitTank = null;
        hitVehicleName = UnknownTargetName;
        hitDistance = 0f;

        var hits = Physics.RaycastAll(origin, direction, MaxRayDistance, ~0, QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0)
            return false;

        RaycastHit? closest = null;
        var closestDistance = float.MaxValue;
        var playerVehicle = DoomController.PlayerVehicleObject;

        foreach (var candidate in hits)
        {
            if (candidate.collider == null)
                continue;
            if (ignoreCollider != null && candidate.collider == ignoreCollider)
                continue;
            if (candidate.distance < MinHitDistanceMeters)
                continue;
            if (playerVehicle != null && candidate.collider.transform.IsChildOf(playerVehicle.transform))
                continue;

            if (candidate.distance < closestDistance)
            {
                closestDistance = candidate.distance;
                closest = candidate;
            }
        }

        if (!closest.HasValue)
            return false;

        var hit = closest.Value;
        hitDistance = hit.distance;

        var hitVehicle = TryApplyNativePenetration(hit, direction, caliberMm, muzzleVelocityMs, out hitTank, out hitVehicleName);

        if (hitVehicle)
        {
            TriggerBlastAtVehicle(hit.point, hit.normal, caliberMm);
            ApplyKineticImpulse(hit, direction, caliberMm, muzzleVelocityMs);
        }

        if (isPrimaryRay)
        {
            DoomPlugin.ModLog.LogDebug(
                $"Выстрел {hit.collider.name}, {hit.distance:0.0} метров, попадание по технике = {hitVehicle}");
        }

        return hitVehicle;
    }

    private static bool TryApplyNativePenetration(
        RaycastHit hit,
        Vector3 direction,
        float caliberMm,
        float muzzleVelocityMs,
        out VehicleBehaviour? vehicle,
        out string vehicleName)
    {
        vehicle = null;
        vehicleName = UnknownTargetName;

        try
        {
            var structure = hit.collider.GetComponentInParent<CompoundStructure>();
            vehicle = hit.collider.GetComponentInParent<VehicleBehaviour>();

            if (vehicle == null && structure != null)
                vehicle = structure.GetComponentInParent<VehicleBehaviour>();

            if (vehicle == null && hit.rigidbody != null)
                vehicle = hit.rigidbody.GetComponentInParent<VehicleBehaviour>();

            if (vehicle != null && DoomController.IsFriendlyTank(vehicle))
            {
                vehicle = null;
                return false;
            }

            if (vehicle?.gameObject != null)
                vehicleName = vehicle.gameObject.name;

            if (structure == null && vehicle != null)
                structure = vehicle.GetComponentInChildren<CompoundStructure>();

            if (structure == null)
                return vehicle != null;

            var diameterMeters = Mathf.Max(ShellMinDiameterMeters, caliberMm * 0.001f);
            var radius = diameterMeters * 0.5f;
            var length = diameterMeters * ShellLengthCalibers;
            var volume = Mathf.PI * radius * radius * length;
            var massKg = Mathf.Max(ShellMinMassKg, volume * ShellDensityKgPerM3);

            var startPosition = hit.point - direction * ShellStartOffsetMeters;
            var velocity = direction * muzzleVelocityMs;

            var penetrator = new PenetratorInfo(
                massKg,
                diameterMeters,
                ShellDensityKgPerM3,
                ShellMaxNormalizationDegrees,
                ShellPenetratorConstant,
                startPosition,
                velocity);

            structure.Penetrate(ref penetrator, default);
            return true;
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogWarning($"Не удалось применить пробитие: {ex.Message}");
            return vehicle != null;
        }
    }

    // подрыв реализуется миной взрывом типо
    private static void TriggerBlastAtVehicle(Vector3 hitPoint, Vector3 hitNormal, float caliberMm)
    {
        try
        {
            var power = Mathf.Max(BlastMinPower, BlastPowerPerCaliberSquared * caliberMm * caliberMm);

            var blastObject = new GameObject("DoomBlast");
            blastObject.transform.position = hitPoint + hitNormal * BlastStandoffMeters;

            var mine = blastObject.AddComponent<ATMine>();
            mine.explosivePower = power;
            mine.piercingDamageModifier = BlastPiercingModifier;
            mine.bluntForceDamageModifier = BlastBluntModifier;
            mine.thermalDamageModifier = BlastThermalModifier;
            mine.pressureDamageModifier = BlastPressureModifier;
            mine.delay = 0f;

            mine.Trigger();
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogWarning($"Не удалось подорвать: {ex.Message}");
        }
    }

    private static void ApplyKineticImpulse(RaycastHit hit, Vector3 direction, float caliberMm, float muzzleVelocityMs)
    {
        try
        {
            var body = hit.rigidbody ?? hit.collider?.GetComponentInParent<Rigidbody>();
            if (body == null || body.isKinematic)
                return;

            var scale = Mathf.Pow(caliberMm / ImpulseReferenceCaliberMm, 2f) *
                        (muzzleVelocityMs / ImpulseReferenceVelocityMs);
            var impulse = ImpulseBase * Mathf.Clamp(scale, ImpulseMinScale, ImpulseMaxScale);
            body.AddForceAtPosition(direction * impulse, hit.point, ForceMode.Impulse);
        }
        catch
        {
            // йоу печенье програмное обеспечениие
        }
    }
}
