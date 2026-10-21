using System;
using System.Collections.Generic;
using HarmonyLib;
using Sprocket;
using Sprocket.ArtificialIntelligence;
using Sprocket.Gameplay.VehicleControl;
using Sprocket.VehicleControl;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Control;
using Sprocket.Vehicles.CrewSystems;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace SprocketDoomMod;

internal struct KillFeedEntry
{
    public string Killer;
    public string Action;
    public string Weapon;
    public string Victim;
}

internal struct BlasterScoreEntry
{
    public int Rank;
    public string Name;
    public int Score;
    public bool Alive;
    public bool IsAlly;
}

internal sealed class EnemyTankRuntimeState
{
    public int InstanceId;
    public VehicleBehaviour? Tank;
    public bool IsFriendly;
    public GameObject? OuterWireframeObj;
    public GameObject? InnerWireframeObj;
    public MeshRenderer? OuterRenderer;
    public MeshRenderer? InnerRenderer;
    public float AimReadyTimer;
    public float PostFireCooldown;
    public int ColorIndex;
    public int ShapeIndex;
    public bool IsChargingShot;
}

internal sealed class ObjectiveZoneMarkerState
{
    public string Name = "OBJECTIVE ZONE";
    public Vector3 GroundPosition;
    public float Radius = 25f;
    public GameObject? BeaconObj;
    public MeshRenderer? BeaconRenderer;
    public bool TriggeredByPlayer;
}

internal sealed class TrackedRealCannonShot
{
    public Vector3 Position;
    public Vector3 Velocity;
    public string AttackerName = "ENEMY_TANK";
    public float LifeTimer;
}

[HarmonyPatch]
internal static class DoomEnemyAiHooks
{
    [HarmonyPrefix, HarmonyPatch(typeof(GunLayerAI), nameof(GunLayerAI.Step))]
    private static bool PrefixGunLayerStep(GunLayerAI __instance)
    {
        try
        {
            if (__instance != null && DoomController.ShouldOverrideEnemyGunLayer(__instance.Pointer))
                return false;
        }
        catch
        {
        }
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(GunnerAI), nameof(GunnerAI.Step))]
    private static bool PrefixGunnerStep(GunnerAI __instance)
    {
        try
        {
            if (__instance != null && DoomController.ShouldOverrideEnemyGunner(__instance.Pointer))
                return false;
        }
        catch
        {
        }
        return true;
    }
}

internal static class DoomController
{
    internal static bool IsActive { get; private set; }
    internal static bool IsPaused { get; private set; }
    internal static bool BuckshotMode { get; private set; }
    internal static int PlayerHealth { get; private set; } = 100;
    internal const int MaxPlayerHealth = 100;

    internal static int ShotsFired { get; private set; }
    internal static int TotalHits { get; private set; }
    internal static int TotalKills { get; private set; }
    internal static int BhopChainCount { get; private set; }
    internal static float CurrentHorizontalSpeed { get; private set; }
    internal static float PeakHorizontalSpeed { get; private set; }

    internal static int AliveEnemyCount { get; private set; }
    internal static int TotalEnemyCount { get; private set; }
    internal static int AliveAllyCount { get; private set; }
    internal static float NearestObjectiveDistance { get; private set; } = -1f;
    internal static string NearestObjectiveName { get; private set; } = string.Empty;

    internal static string LastKillBannerText { get; private set; } = string.Empty;
    internal static float LastKillBannerTimer { get; private set; }
    internal static float LastHeadshotFlashTimer { get; private set; }
    internal static float LastDamageFlashTimer { get; private set; }
    internal static float IncomingShellWarningTimer { get; private set; }
    internal static float TalkingAnimationTimer { get; private set; }

    internal static string CurrentDialogueText { get; private set; } = "Hell YEAH!!!!!\nKill em boyeee";
    internal static float DialogueOverrideTimer { get; private set; }

    private static readonly string[] IdleChatterLines =
    {
        "Hell YEAH!!!!!\nKill em boyeee",
        "Fucking shotgun loaded\nHeeeelllll YEAAAAAHHH!!11!!",
        "Quake is my favorite gameee\nI fooking love quake!!",
        "Look for red wireframes\nEnemies will shoot you!",
        "Boomer shooters are the BEST!!\nModern games are for wusses!!",
        "No cover system? GOOD.\nReal men bunnyhop!!!1!",
        "Circle strafe around 'em!!!\nDon't stand still or u DEDD!",
        "Badass sunglasses and \n a cool stylish cigar all you need!!",
        "BLAST enemy tanmks\nFresh health for your HEARTH!!"
    };
    private static int _idleChatterIndex;
    private static float _idleChatterTimer;

    private static readonly List<KillFeedEntry> _killFeed = new(8);
    private static readonly List<BlasterScoreEntry> _blasterScores = new(8);
    private static float _nextScoreboardRefreshTime;

    private static GameObject? _playerObject;
    private static Rigidbody? _playerBody;
    private static CapsuleCollider? _playerCollider;

    private static Camera? _doomCamera;
    private static Camera? _originalCamera;
    private static GameObject? _playerVehicle;

    private static readonly List<InputAction> _disabledActions = new();
    private static readonly List<Canvas> _disabledCanvases = new();
    private static readonly Dictionary<int, bool> _tankEliminatedState = new();
    private static readonly HashSet<int> _tanksHitByPlayer = new();
    private static readonly Dictionary<int, EnemyTankRuntimeState> _enemyStates = new();
    private static readonly HashSet<IntPtr> _enemyGunLayerPointers = new();
    private static readonly HashSet<IntPtr> _enemyGunnerPointers = new();
    private static readonly List<ObjectiveZoneMarkerState> _objectiveZones = new();
    private static readonly List<TrackedRealCannonShot> _trackedCannonShots = new();

    private static DeathmatchGameMode? _cachedGameMode;
    private static float _nextGameModeLookupTime = -10f;
    private static float _nextObjectiveScanTime = -10f;
    private static bool _hasSeenEnemiesInBattle;
    private static float _allEnemiesDeadTimer;
    private static bool _triedAdvanceMissionOnClear;
    private static bool _triggeredMissionComplete;

    private static int _lastDamagedTankId = -1;
    private static string _lastDamagedTankName = "ENEMY_TANK";
    private static float _lastDamagedTankTime = -100f;
    private static bool _missionSuccessChecked;
    private static int _announcerKillStep;
    private static bool _manualEscPaused;
    private static Vector3 _savedPauseVelocity;

    private static Mesh?[]? _wireframeMeshes;
    private static Mesh? _allyHaloMesh;
    private static Mesh? _objectiveBeaconMesh;
    private static Material?[]? _wireframeNeonMaterials;
    private static Material? _wireframeAlertMaterial;
    private static Material? _wireframeAllyMaterial;
    private static Material? _wireframeObjectiveMaterial;

    private static float _yaw;
    private static float _pitch;
    private static float _recoilPitchOffset;
    private static float _recoilRollOffset;
    private static float _damageShakeOffset;
    private static float _respawnTimer;

    private static Vector2 _moveInput;
    private static bool _sprintHeld;
    private static bool _jumpHeld;
    private static bool _jumpQueued;
    private static bool _wasGrounded;
    private static float _groundedGraceTimer;
    private static float _jumpCooldownTimer;
    private static bool _mustLeaveGroundBeforeNextJump;
    private static int _airFramesSinceJump;
    private static bool _spaceReleasedSinceLastJump = true;
    private static Vector3 _planarVelocity;

    private static float _bobPhase;
    private static float _bobAmplitude;
    private static Vector3 _cameraLocalBasePos = new(0f, 0.72f, 0f);

    private const float PlayerHeight = 1.82f;
    private const float PlayerRadius = 0.38f;
    private const float PlayerMass = 85f;
    private const float GravityAcceleration = 22.0f;
    private const float GroundFriction = 7.5f;
    private const float GroundAccelerate = 14.0f;

    private const float MissionAdvanceDelay = 1.2f;
    private const float MissionCompleteDelay = 2.4f;

    // найденные цели ближе этого расстояния считаются одной зоной 
    // но оно пока не работает
    private const float ObjectiveMergeDistance = 30f;

    private const float AimFireAngleDegrees = 3.5f;
    private const float AimHoldAngleDegrees = 7.5f;
    private const float AimHoldTime = 2.5f;

    private const int MinAirFramesBeforeBhop = 6;
    private const float BhopApexVerticalSpeed = 3.5f;

    internal static IReadOnlyList<KillFeedEntry> KillFeed => _killFeed;
    internal static IReadOnlyList<BlasterScoreEntry> BlasterScores => _blasterScores;
    internal static Camera? ActiveDoomCamera => _doomCamera;
    internal static GameObject? PlayerVehicleObject => _playerVehicle;

    internal static bool ShouldOverrideEnemyGunLayer(IntPtr layerPtr)
    {
        return IsActive && !IsPaused && DoomPlugin.ConfigEnableEnemyFire.Value && _enemyGunLayerPointers.Contains(layerPtr);
    }

    internal static bool ShouldOverrideEnemyGunner(IntPtr gunnerPtr)
    {
        return IsActive && !IsPaused && DoomPlugin.ConfigEnableEnemyFire.Value && _enemyGunnerPointers.Contains(gunnerPtr);
    }

    internal static DeathmatchGameMode? FindActiveGameMode()
    {
        try
        {
            if (_cachedGameMode != null && _cachedGameMode.Pointer != IntPtr.Zero && !_cachedGameMode.WasCollected)
                return _cachedGameMode;

            if (Time.unscaledTime < _nextGameModeLookupTime)
                return null;

            _nextGameModeLookupTime = Time.unscaledTime + 1.0f;
            _cachedGameMode = UnityEngine.Object.FindObjectOfType<DeathmatchGameMode>();
            return _cachedGameMode;
        }
        catch
        {
            return null;
        }
    }

    internal static byte GetPlayerTeamMask()
    {
        try
        {
            if (_playerVehicle != null)
            {
                var playerTank = _playerVehicle.GetComponent<VehicleBehaviour>() ?? _playerVehicle.GetComponentInChildren<VehicleBehaviour>();
                if (playerTank != null && (byte)playerTank.ID.TeamID != 0)
                    return (byte)playerTank.ID.TeamID;
            }

            var gameMode = FindActiveGameMode();
            if (gameMode != null && (byte)gameMode.playerTeamID != 0)
                return (byte)gameMode.playerTeamID;
        }
        catch
        {
        }

        return 1; // 1 команда это тиммейты
    }

    internal static bool IsFriendlyTank(VehicleBehaviour? tank)
    {
        if (tank == null || tank.Pointer == IntPtr.Zero || tank.WasCollected || tank.gameObject == null)
            return false;

        try
        {
            if (_playerVehicle != null && tank.gameObject == _playerVehicle)
                return true;

            if (tank.ControlType == ControlType.Player)
                return true;

            byte tankTeam = (byte)tank.ID.TeamID;
            byte playerTeam = GetPlayerTeamMask();

            if (tankTeam != 0 && (tankTeam & playerTeam) != 0)
                return true;
        }
        catch
        {
        }

        return false;
    }

    internal static void OnSceneChanged()
    {
        if (IsActive)
        {
            ExitDoomMode(keepVictoryAnthem: false);
        }
        _cachedGameMode = null;
        _hasSeenEnemiesInBattle = false;
        _allEnemiesDeadTimer = 0f;
        _triedAdvanceMissionOnClear = false;
        _triggeredMissionComplete = false;
        _tankEliminatedState.Clear();
        _tanksHitByPlayer.Clear();
        _killFeed.Clear();
        _blasterScores.Clear();
        _missionSuccessChecked = false;
        CleanupEnemyWireframesAndShells();
    }

    internal static void Shutdown()
    {
        if (IsActive)
        {
            ExitDoomMode(keepVictoryAnthem: false);
        }
        CleanupEnemyWireframesAndShells();
        DoomAudio.Cleanup();
    }

    private static void SpeakDialogue(string text, float duration = 3.2f)
    {
        CurrentDialogueText = text;
        DialogueOverrideTimer = duration;
        TalkingAnimationTimer = Mathf.Max(TalkingAnimationTimer, Mathf.Min(duration, 2.6f));
    }

    internal static bool CheckAndNotifyTankDestroyed(VehicleBehaviour tank)
    {
        if (tank == null || tank.gameObject == null)
            return false;

        int id = tank.gameObject.GetInstanceID();
        bool alreadyOut = _tankEliminatedState.TryGetValue(id, out bool prev) && prev;
        if (alreadyOut)
            return false;

        if (IsTankKnockedOut(tank))
        {
            _tankEliminatedState[id] = true;
            NotifyTankKilled(tank.gameObject.name, id);
            return true;
        }
        return false;
    }

    internal static void NotifyTankHit(float distanceMeters, float caliberMm, string targetName = "ARMOR_UNIT", VehicleBehaviour? hitTank = null)
    {
        TotalHits++;
        LastHeadshotFlashTimer = 0.85f;

        if (hitTank != null && hitTank.gameObject != null)
        {
            int id = hitTank.gameObject.GetInstanceID();
            _tanksHitByPlayer.Add(id);
            _lastDamagedTankId = id;
            _lastDamagedTankName = hitTank.gameObject.name;
            _lastDamagedTankTime = Time.unscaledTime;
        }

        DoomAudio.PlayHeadshotSound();

        string clean = CleanTargetName(targetName);
        bool isBigShot = distanceMeters >= 45f || BhopChainCount >= 2 || CurrentHorizontalSpeed >= 13.5f;

        if (isBigShot)
        {
            DoomAudio.PlayBigShotSound();
            if (PlayerHealth > 0 && PlayerHealth < MaxPlayerHealth)
            {
                PlayerHealth = Mathf.Min(MaxPlayerHealth, PlayerHealth + 15);
            }
            SpeakDialogue($"NOW'S YOUR CHANCE TO BE A\n[[BIG SHOT]]! // {clean} ({distanceMeters:0}m)", 3.2f);
        }
        else
        {
            DoomAudio.PlayMlgGunShotSound();
            if (PlayerHealth > 0 && PlayerHealth < MaxPlayerHealth)
            {
                PlayerHealth = Mathf.Min(MaxPlayerHealth, PlayerHealth + 10);
            }
            SpeakDialogue($"ARMOR CRACKED // {clean}\n+10 HP SIPHONED ({distanceMeters:0}m)!", 3.0f);
        }

        PushKillFeed("sussy_slayer", "cracked", "200mm", clean);
    }

    internal static void NotifyTankKilled(string tankName, int tankInstanceId = -1)
    {
        // хитмаркер показываем, даже если убийство уже засчитано ранее
        if (LastHeadshotFlashTimer <= 0.1f)
        {
            TotalHits++;
            LastHeadshotFlashTimer = 0.85f;
            DoomAudio.PlayHeadshotSound();
        }

        TotalKills++;
        _announcerKillStep++;
        if (_announcerKillStep > 6)
        {
            _announcerKillStep = 2; // First blood -> double -> 3 -> rampage -> Mmonster kill -> big shot -> и по новой...
        }

        LastKillBannerTimer = 3.6f;
        string clean = CleanTargetName(tankName);

        if (PlayerHealth > 0)
        {
            PlayerHealth = Mathf.Min(MaxPlayerHealth, PlayerHealth + 35);
        }

        DoomAudio.AnnouncerClipType clipType;
        switch (_announcerKillStep)
        {
            case 1:
                clipType = DoomAudio.AnnouncerClipType.FirstBlood;
                LastKillBannerText = $"FIRST BLOOD! TANK VENTED // {clean}";
                break;
            case 2:
                clipType = DoomAudio.AnnouncerClipType.DoubleKill;
                LastKillBannerText = $"DOUBLE KILL! ({TotalKills} VENTED) // {clean}";
                break;
            case 3:
                clipType = DoomAudio.AnnouncerClipType.TripleKill;
                LastKillBannerText = $"TRIPLE KILL! ({TotalKills} VENTED) // {clean}";
                break;
            case 4:
                clipType = DoomAudio.AnnouncerClipType.Rampage;
                LastKillBannerText = $"RAMPAGE! ({TotalKills} VENTED) // {clean}";
                break;
            case 5:
                clipType = DoomAudio.AnnouncerClipType.MonsterKill;
                LastKillBannerText = $"MONSTER KILL! ({TotalKills} VENTED) // {clean}";
                break;
            default:
                clipType = DoomAudio.AnnouncerClipType.BigShot;
                LastKillBannerText = $"[[BIG SHOT]] OBLITERATION! // {clean}";
                break;
        }

        SpeakDialogue($"TANK VENTED // {clean}\n+35 HP SIPHONED! KEEP SLAYING!", 4.2f);
        PushKillFeed("sussy_slayer", "vented", "200mm", clean);
        DoomAudio.PlayAnnouncerClip(clipType);
    }

    internal static void DamagePlayer(int damageAmount, string attackerName, Vector3 blastOrigin)
    {
        if (!IsActive || _respawnTimer > 0f)
            return;

        string cleanAttacker = CleanTargetName(attackerName);
        PlayerHealth = Mathf.Max(0, PlayerHealth - damageAmount);
        LastDamageFlashTimer = 0.65f;
        _damageShakeOffset = 6.5f;

        if (_playerBody != null && _playerObject != null)
        {
            Vector3 pushDir = (_playerObject.transform.position - blastOrigin);
            pushDir.y = Mathf.Max(0.45f, pushDir.y);
            if (pushDir.sqrMagnitude < 0.01f)
                pushDir = Vector3.up;
            pushDir.Normalize();
            _playerBody.velocity += pushDir * 10.5f;
        }

        PushKillFeed(cleanAttacker, "hit", "Cannon", $"sussy(-{damageAmount}HP)");

        if (PlayerHealth <= 0)
        {
            _respawnTimer = 2.6f;
            LastKillBannerTimer = 2.6f;
            LastKillBannerText = "CRITICAL HULL FAILURE // REBOOTING...";
            SpeakDialogue($"CRITICAL HIT BY {cleanAttacker}!\nREBOOTING SUSSY_OS...", 2.8f);
        }
        else
        {
            SpeakDialogue($"WARNING! {cleanAttacker} CANNON\nHIT YOU FOR -{damageAmount} HP!", 2.8f);
        }
    }

    private static void PushKillFeed(string killer, string action, string weapon, string victim)
    {
        _killFeed.Insert(0, new KillFeedEntry
        {
            Killer = killer,
            Action = action,
            Weapon = weapon,
            Victim = victim
        });

        if (_killFeed.Count > 3)
        {
            _killFeed.RemoveAt(_killFeed.Count - 1);
        }
    }

    internal static string CleanTargetName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Tank";
        string s = raw.Replace("(Clone)", "").Replace("(", "#").Replace(")", "").Trim();
        if (s.Length > 12)
            s = s.Substring(0, 12).Trim();
        return s;
    }

    private static void InspectGameScenes(
        out bool hasVehicleControlUi,
        out bool hasPauseOverlay,
        out bool hasMissionEndScreen,
        out bool hasMissionSuccess,
        out bool hasEditorOrMenu)
    {
        hasVehicleControlUi = false;
        hasPauseOverlay = false;
        hasMissionEndScreen = false;
        hasMissionSuccess = false;
        hasEditorOrMenu = false;

        try
        {
            int count = SceneManager.sceneCount;
            for (int i = 0; i < count; i++)
            {
                Scene sc = SceneManager.GetSceneAt(i);
                if (!sc.IsValid() || !sc.isLoaded)
                    continue;

                string name = sc.name ?? string.Empty;
                if (name.Equals("VehicleControlUI", StringComparison.OrdinalIgnoreCase))
                {
                    hasVehicleControlUi = true;
                }
                else if (name.Equals("PauseMenu", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("Prompt", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("SelectionPopup", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("PhotomodeOverlay", StringComparison.OrdinalIgnoreCase))
                {
                    hasPauseOverlay = true;
                }
                else if (name.IndexOf("MissionSuccess", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("Victory", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hasMissionEndScreen = true;
                    hasMissionSuccess = true;
                }
                else if (name.IndexOf("MissionFail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("Defeat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         (name.IndexOf("Mission", StringComparison.OrdinalIgnoreCase) >= 0 &&
                          name.IndexOf("Screen", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    hasMissionEndScreen = true;
                }
                else if (name.Equals("VehicleDesignerUI", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("MainMenu", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("ScenarioSelectScreen", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("LoadingScreen", StringComparison.OrdinalIgnoreCase))
                {
                    hasEditorOrMenu = true;
                }
            }
        }
        catch
        {
        }
    }

    internal static void Update()
    {
        Keyboard? kb = Keyboard.current;
        if (kb == null)
            return;

        float dt = Time.unscaledDeltaTime;

        if (LastHeadshotFlashTimer > 0f)
            LastHeadshotFlashTimer -= dt;

        if (LastKillBannerTimer > 0f)
            LastKillBannerTimer -= dt;

        if (LastDamageFlashTimer > 0f)
            LastDamageFlashTimer -= dt;

        if (IncomingShellWarningTimer > 0f)
            IncomingShellWarningTimer -= dt;

        if (TalkingAnimationTimer > 0f)
            TalkingAnimationTimer -= dt;

        if (DialogueOverrideTimer > 0f)
        {
            DialogueOverrideTimer -= dt;
        }
        else
        {
            _idleChatterTimer += dt;
            if (_idleChatterTimer >= 7.5f)
            {
                _idleChatterTimer = 0f;
                _idleChatterIndex = (_idleChatterIndex + 1) % IdleChatterLines.Length;
                CurrentDialogueText = IdleChatterLines[_idleChatterIndex];
                TalkingAnimationTimer = 1.6f;
            }
        }

        InspectGameScenes(
            out bool hasVehicleControlUi,
            out bool hasPauseOverlay,
            out bool hasMissionEndScreen,
            out bool hasMissionSuccess,
            out bool hasEditorOrMenu);

        if (IsActive)
        {
            if (hasMissionEndScreen)
            {
                PollDestroyedTanks();
                if (hasMissionSuccess)
                {
                    DoomAudio.PlayVictoryLastKillSound();
                }
                DoomPlugin.ModLog.LogDebug("экран завершения миссии");
                ExitDoomMode(keepVictoryAnthem: hasMissionSuccess);
                return;
            }

            if (hasEditorOrMenu || !hasVehicleControlUi)
            {
                DoomPlugin.ModLog.LogDebug("сцена сменилась на небоёвую");
                ExitDoomMode(keepVictoryAnthem: false);
                return;
            }
        }

        Key primaryKey = DoomPlugin.ConfigToggleKey.Value;
        Key altKey = DoomPlugin.ConfigAltToggleKey.Value;

        bool togglePressed =
            (primaryKey != Key.None && kb[primaryKey].wasPressedThisFrame) ||
            (altKey != Key.None && kb[altKey].wasPressedThisFrame);

        if (togglePressed)
        {
            if (IsActive)
            {
                ExitDoomMode(keepVictoryAnthem: false);
            }
            else if (hasVehicleControlUi && !hasEditorOrMenu && !hasMissionEndScreen)
            {
                EnterDoomMode();
            }
            return;
        }

        if (!IsActive)
            return;

        if (kb[Key.Escape].wasPressedThisFrame && !hasPauseOverlay)
        {
            _manualEscPaused = !_manualEscPaused;
        }
        else if (hasPauseOverlay)
        {
            _manualEscPaused = false;
        }

        bool shouldPauseNow = hasPauseOverlay || _manualEscPaused || Time.timeScale < 0.01f;
        if (shouldPauseNow)
        {
            if (!IsPaused)
            {
                IsPaused = true;
                DoomAudio.SetPaused(true);
                if (_playerBody != null)
                {
                    _savedPauseVelocity = _playerBody.velocity;
                    _playerBody.velocity = Vector3.zero;
                }
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return;
        }
        else if (IsPaused)
        {
            IsPaused = false;
            DoomAudio.SetPaused(false);
            if (_playerBody != null)
            {
                _playerBody.velocity = _savedPauseVelocity;
            }
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (_respawnTimer > 0f)
        {
            _respawnTimer -= dt;
            if (_respawnTimer <= 0f)
            {
                PlayerHealth = MaxPlayerHealth;
                if (_playerObject != null && _playerVehicle != null)
                {
                    Transform vt = _playerVehicle.transform;
                    Vector3 respawnPos = vt.position + vt.right * 4.5f + Vector3.up * 2.2f;
                    _playerObject.transform.position = respawnPos;
                    if (_playerBody != null)
                        _playerBody.velocity = Vector3.zero;
                }
                SpeakDialogue("SUSSY_OS REBOOT COMPLETE!\nHEALTH RESTORED TO 100!", 3.0f);
            }
        }

        Key buckshotKey = DoomPlugin.ConfigBuckshotToggleKey.Value;
        if (buckshotKey != Key.None && kb[buckshotKey].wasPressedThisFrame)
        {
            BuckshotMode = !BuckshotMode;
            SpeakDialogue(
                BuckshotMode
                    ? "ORDNANCE // 200MM BUCKSHOT\n8x SUB-CALIBER FLECHETTES."
                    : "ORDNANCE // 200MM APHE SLUG\n2000 M/S RAIL-SHOTGUN.",
                2.8f);
        }

        Key renderModeKey = DoomPlugin.ConfigRenderModeKey.Value;
        if (renderModeKey != Key.None && kb[renderModeKey].wasPressedThisFrame)
        {
            DoomWeaponView.ToggleRenderMode();
        }

        Key ditherKey = DoomPlugin.ConfigDitherToggleKey.Value;
        if (ditherKey != Key.None && kb[ditherKey].wasPressedThisFrame)
        {
            DoomPlugin.ConfigEnableDitherShader.Value = !DoomPlugin.ConfigEnableDitherShader.Value;
            SpeakDialogue(
                DoomPlugin.ConfigEnableDitherShader.Value
                    ? "RETRO DITHER SHADER: ON\nBAYER MATRIX ENGAGED."
                    : "RETRO DITHER SHADER: OFF\nCLEAN OPTICS ENGAGED.",
                2.2f);
        }

        if (kb[Key.Minus].wasPressedThisFrame)
        {
            DoomAudio.AdjustVolumeInGame(-0.08f);
            int pct = Mathf.RoundToInt(DoomPlugin.ConfigMusicVolume.Value * 100f);
            SpeakDialogue($"AUDIO GAIN REDUCED // {pct}%\nSYNCED WITH GAME MIXER.", 1.8f);
        }
        else if (kb[Key.Equals].wasPressedThisFrame)
        {
            DoomAudio.AdjustVolumeInGame(+0.08f);
            int pct = Mathf.RoundToInt(DoomPlugin.ConfigMusicVolume.Value * 100f);
            SpeakDialogue($"AUDIO GAIN INCREASED // {pct}%\nSYNCED WITH GAME MIXER.", 1.8f);
        }

        ReadMovementAndLookInput(kb);
        PollDestroyedTanks();
        UpdateEnemyTanksAndWireframes(dt);
        UpdateMissionObjectivesAndAutoVictory(dt);
        UpdateTrackedCannonShots(dt);

        if (Time.unscaledTime >= _nextScoreboardRefreshTime)
        {
            _nextScoreboardRefreshTime = Time.unscaledTime + 1.0f;
            RefreshScoreboard();
        }

        Mouse? mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && _respawnTimer <= 0f)
        {
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            TryFireSuperShotgun();
        }

        DoomWeaponView.UpdateAnimation(dt, _bobPhase, _bobAmplitude, _yaw, _pitch);
        DoomAudio.Update();
    }

    private static void EnsureWireframeResources()
    {
        if (_wireframeMeshes != null && _wireframeNeonMaterials != null && _allyHaloMesh != null && _objectiveBeaconMesh != null)
            return;

        _wireframeMeshes = new Mesh?[4];
        for (int i = 0; i < 4; i++)
        {
            _wireframeMeshes[i] = DoomAssetBuilder.BuildWireframePolyhedronMesh(i, 0.042f);
        }

        _allyHaloMesh = DoomAssetBuilder.BuildWireframePolyhedronMesh(4, 0.045f);
        _objectiveBeaconMesh = DoomAssetBuilder.BuildWireframePolyhedronMesh(5, 0.065f);

        Color[] neonColors =
        {
            new Color(0.10f, 0.98f, 1.00f, 1.0f), // берюзовый
            new Color(1.00f, 0.22f, 0.85f, 1.0f), // маджента
            new Color(0.35f, 1.00f, 0.22f, 1.0f), // лаймовый
            new Color(1.00f, 0.92f, 0.15f, 1.0f), // жёлтый
            new Color(0.65f, 0.32f, 1.00f, 1.0f), // фиолтеовый
        };

        _wireframeNeonMaterials = new Material?[neonColors.Length];
        for (int i = 0; i < neonColors.Length; i++)
        {
            _wireframeNeonMaterials[i] = CreateUnlitColorMaterial(neonColors[i], $"DoomWireframeNeon_{i}");
        }

        _wireframeAlertMaterial = CreateUnlitColorMaterial(new Color(1.0f, 0.12f, 0.12f, 1.0f), "DoomWireframeAlert_Red");
        _wireframeAllyMaterial = CreateUnlitColorMaterial(new Color(0.12f, 1.00f, 0.58f, 1.0f), "DoomWireframeAlly_Emerald");
        _wireframeObjectiveMaterial = CreateUnlitColorMaterial(new Color(1.00f, 0.94f, 0.18f, 1.0f), "DoomWireframeObjective_Gold");
    }

    private static Material CreateUnlitColorMaterial(Color color, string name)
    {
        Shader? shader =
            Shader.Find("HDRP/Unlit") ??
            Shader.Find("Unlit/Color") ??
            Shader.Find("Sprites/Default") ??
            Shader.Find("HDRP/Lit");

        var mat = new Material(shader)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset
        };

        Texture2D tex = Texture2D.whiteTexture;

        mat.mainTexture = tex;
        mat.color = color;
        if (mat.HasProperty("_UnlitColor"))
            mat.SetColor("_UnlitColor", color);
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", color);
        if (mat.HasProperty("_EmissiveColor"))
            mat.SetColor("_EmissiveColor", color * 4.0f);
        if (mat.HasProperty("_UnlitColorMap"))
            mat.SetTexture("_UnlitColorMap", tex);
        if (mat.HasProperty("_BaseColorMap"))
            mat.SetTexture("_BaseColorMap", tex);

        try
        {
            HDMaterial.ValidateMaterial(mat);
        }
        catch
        {
        }

        return mat;
    }

    private static void UpdateEnemyTanksAndWireframes(float dt)
    {
        EnsureWireframeResources();
        _enemyGunLayerPointers.Clear();
        _enemyGunnerPointers.Clear();

        if (_playerObject == null)
            return;

        Vector3 playerPos = _playerObject.transform.position;
        bool showWireframes = DoomPlugin.ConfigEnableEnemyWireframes.Value;
        bool enableEnemyFire = DoomPlugin.ConfigEnableEnemyFire.Value;

        int totalEnemies = 0;
        int aliveEnemies = 0;
        int aliveAllies = 0;

        try
        {
            VehicleBehaviour[] tanks = UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>();
            var seenIds = new HashSet<int>();

            if (tanks != null)
            {
                int idxCounter = 0;
                foreach (VehicleBehaviour tank in tanks)
                {
                    if (tank == null || tank.gameObject == null)
                        continue;
                    if (_playerVehicle != null && tank.gameObject == _playerVehicle)
                        continue;

                    int id = tank.gameObject.GetInstanceID();
                    bool isFriendly = IsFriendlyTank(tank);
                    bool isDead = IsTankKnockedOut(tank);

                    if (!isFriendly)
                    {
                        totalEnemies++;
                        if (!isDead)
                            aliveEnemies++;
                    }
                    else if (!isDead)
                    {
                        aliveAllies++;
                    }

                    if (isDead)
                    {
                        if (_enemyStates.TryGetValue(id, out var deadState))
                        {
                            DestroyEnemyWireframeObjects(deadState);
                            _enemyStates.Remove(id);
                        }
                        continue;
                    }

                    seenIds.Add(id);
                    if (!_enemyStates.TryGetValue(id, out var state) || state.IsFriendly != isFriendly)
                    {
                        if (_enemyStates.TryGetValue(id, out var oldState))
                        {
                            DestroyEnemyWireframeObjects(oldState);
                        }

                        state = new EnemyTankRuntimeState
                        {
                            InstanceId = id,
                            Tank = tank,
                            IsFriendly = isFriendly,
                            AimReadyTimer = 0f,
                            PostFireCooldown = UnityEngine.Random.Range(1.0f, 3.0f),
                            ColorIndex = idxCounter % 5,
                            ShapeIndex = idxCounter % 4
                        };
                        _enemyStates[id] = state;
                    }
                    idxCounter++;

                    if (isFriendly)
                    {
                        if (showWireframes)
                        {
                            EnsureTankWireframeObjects(state);
                            Vector3 tPos = tank.transform.position;
                            if (state.OuterWireframeObj != null)
                            {
                                state.OuterWireframeObj.transform.position = tPos + Vector3.up * 3.2f;
                                state.OuterWireframeObj.transform.localScale = new Vector3(2.4f, 1.6f, 2.4f);
                                state.OuterWireframeObj.transform.Rotate(0f, 40f * dt, 0f, Space.Self);
                            }
                            if (state.OuterRenderer != null && _wireframeAllyMaterial != null)
                            {
                                state.OuterRenderer.sharedMaterial = _wireframeAllyMaterial;
                            }
                        }
                        else
                        {
                            DestroyEnemyWireframeObjects(state);
                        }
                        continue;
                    }

                    if (showWireframes)
                    {
                        EnsureTankWireframeObjects(state);
                        Vector3 tPos = tank.transform.position;
                        float pulse = 1.0f + 0.08f * Mathf.Sin(Time.unscaledTime * 3.8f + state.ShapeIndex);

                        if (state.OuterWireframeObj != null)
                        {
                            state.OuterWireframeObj.transform.position = tPos + Vector3.up * 1.9f;
                            state.OuterWireframeObj.transform.localScale = new Vector3(4.4f * pulse, 3.6f * pulse, 4.4f * pulse);
                            state.OuterWireframeObj.transform.Rotate(28f * dt, 58f * dt, 18f * dt, Space.Self);
                        }

                        if (state.InnerWireframeObj != null)
                        {
                            float bob = Mathf.Sin(Time.unscaledTime * 4.5f + state.ColorIndex) * 0.35f;
                            state.InnerWireframeObj.transform.position = tPos + Vector3.up * (4.6f + bob);
                            state.InnerWireframeObj.transform.localScale = new Vector3(1.85f, 1.85f, 1.85f);
                            state.InnerWireframeObj.transform.Rotate(-45f * dt, -85f * dt, 35f * dt, Space.Self);
                        }

                        if (_wireframeNeonMaterials != null && _wireframeNeonMaterials.Length > 0)
                        {
                            Material? targetMat;
                            if (state.IsChargingShot && ((int)(Time.unscaledTime * 14f) % 2 == 0))
                            {
                                targetMat = _wireframeAlertMaterial;
                            }
                            else
                            {
                                int cIdx = (state.ColorIndex + Mathf.FloorToInt(Time.unscaledTime * 1.8f)) % _wireframeNeonMaterials.Length;
                                targetMat = _wireframeNeonMaterials[cIdx];
                            }

                            if (targetMat != null)
                            {
                                if (state.OuterRenderer != null) state.OuterRenderer.sharedMaterial = targetMat;
                                if (state.InnerRenderer != null) state.InnerRenderer.sharedMaterial = targetMat;
                            }
                        }
                    }
                    else
                    {
                        DestroyEnemyWireframeObjects(state);
                    }

                    if (enableEnemyFire && _respawnTimer <= 0f)
                    {
                        StepEnemyTankRealCannonAi(tank, state, playerPos, dt);
                    }
                }
            }

            var toRemove = new List<int>();
            foreach (var kv in _enemyStates)
            {
                if (!seenIds.Contains(kv.Key))
                {
                    DestroyEnemyWireframeObjects(kv.Value);
                    toRemove.Add(kv.Key);
                }
            }
            foreach (int rId in toRemove)
            {
                _enemyStates.Remove(rId);
            }
        }
        catch
        {
        }

        TotalEnemyCount = totalEnemies;
        AliveEnemyCount = aliveEnemies;
        AliveAllyCount = aliveAllies;
        if (totalEnemies > 0)
        {
            _hasSeenEnemiesInBattle = true;
        }
    }

    private static void TriggerAdvanceMission(DeathmatchGameMode? gameMode)
    {
        if (gameMode == null)
            return;

        try
        {
            if (gameMode.missions != null && gameMode.missions.Length > 0)
            {
                for (int i = 0; i < gameMode.missions.Length; i++)
                {
                    var m = gameMode.missions[i];
                    if (m != null && m.gameObject != null && m.gameObject.activeInHierarchy)
                    {
                        gameMode.AdvanceMission(m);
                        return;
                    }
                }

                if (gameMode.missions[0] != null)
                {
                    gameMode.AdvanceMission(gameMode.missions[0]);
                    return;
                }
            }

            gameMode.AdvanceMission(null!);
        }
        catch
        {
        }
    }

    private static void TriggerCompleteMission(DeathmatchGameMode? gameMode)
    {
        if (gameMode == null)
            return;

        try
        {
            if (gameMode.missions != null && gameMode.missions.Length > 0)
            {
                for (int i = 0; i < gameMode.missions.Length; i++)
                {
                    var m = gameMode.missions[i];
                    if (m != null && m.gameObject != null && m.gameObject.activeInHierarchy)
                    {
                        gameMode.CompleteMission(m);
                        return;
                    }
                }

                var lastMission = gameMode.missions[gameMode.missions.Length - 1];
                if (lastMission != null)
                {
                    gameMode.CompleteMission(lastMission);
                    return;
                }
            }

            gameMode.CompleteMission(null!);
        }
        catch
        {
        }
    }

    // Пеший игрок не может выполнить задание, рассчитанное на танк, поэтому
    //после уничтожения всех противников миссия продвигается или завершается сама
    // а зоны и линии целей помечаются маяками и засчитываются при входе пешком
    private static void UpdateMissionObjectivesAndAutoVictory(float dt)
    {
        var gameMode = FindActiveGameMode();

        if (_hasSeenEnemiesInBattle && AliveEnemyCount == 0 && TotalEnemyCount > 0)
        {
            _allEnemiesDeadTimer += dt;

            if (_allEnemiesDeadTimer >= MissionAdvanceDelay && !_triedAdvanceMissionOnClear)
            {
                _triedAdvanceMissionOnClear = true;
                TriggerAdvanceMission(gameMode);
            }

            if (_allEnemiesDeadTimer >= MissionCompleteDelay && !_triggeredMissionComplete)
            {
                _triggeredMissionComplete = true;
                TriggerCompleteMission(gameMode);
            }
        }
        else if (AliveEnemyCount > 0)
        {
            if (_triedAdvanceMissionOnClear && _allEnemiesDeadTimer > 0f)
            {
                SpeakDialogue("NEW ENEMY WAVE DETECTED!\nRIP AND TEAR!", 3.2f);
            }
            _allEnemiesDeadTimer = 0f;
            _triedAdvanceMissionOnClear = false;
        }

        // периодический поиск зон и линий цели и отрисовка маяков
        if (Time.unscaledTime >= _nextObjectiveScanTime)
        {
            _nextObjectiveScanTime = Time.unscaledTime + 2.0f;
            DiscoverSceneMissionObjectives(gameMode);
        }

        if (_playerObject == null || _objectiveZones.Count == 0)
        {
            NearestObjectiveDistance = -1f;
            NearestObjectiveName = string.Empty;
            return;
        }

        Vector3 playerPos = _playerObject.transform.position;
        float bestDist = float.MaxValue;
        string bestName = string.Empty;
        bool showWireframes = DoomPlugin.ConfigEnableEnemyWireframes.Value;

        for (int i = 0; i < _objectiveZones.Count; i++)
        {
            var zone = _objectiveZones[i];
            if (showWireframes && _objectiveBeaconMesh != null && _wireframeObjectiveMaterial != null)
            {
                if (zone.BeaconObj == null)
                {
                    zone.BeaconObj = new GameObject($"Doom_ObjectiveBeacon_{i}");
                    var meshFilter = zone.BeaconObj.AddComponent<MeshFilter>();
                    meshFilter.sharedMesh = _objectiveBeaconMesh;
                    zone.BeaconRenderer = zone.BeaconObj.AddComponent<MeshRenderer>();
                    zone.BeaconRenderer.shadowCastingMode = ShadowCastingMode.Off;
                    zone.BeaconRenderer.receiveShadows = false;
                    zone.BeaconRenderer.sharedMaterial = _wireframeObjectiveMaterial;
                }

                float pulse = 1.0f + 0.06f * Mathf.Sin(Time.unscaledTime * 3.0f + i);
                zone.BeaconObj.transform.position = zone.GroundPosition;
                zone.BeaconObj.transform.localScale = new Vector3(pulse, 1.0f, pulse);
                zone.BeaconObj.transform.Rotate(0f, 35f * dt, 0f, Space.Self);
            }
            else if (zone.BeaconObj != null)
            {
                UnityEngine.Object.Destroy(zone.BeaconObj);
                zone.BeaconObj = null;
                zone.BeaconRenderer = null;
            }

            float flatDist = Vector2.Distance(
                new Vector2(playerPos.x, playerPos.z),
                new Vector2(zone.GroundPosition.x, zone.GroundPosition.z));

            if (flatDist < bestDist)
            {
                bestDist = flatDist;
                bestName = zone.Name;
            }

            // вход в помеченную зону пешком должен засчитываться как выполнение
            if (!zone.TriggeredByPlayer && flatDist <= zone.Radius)
            {
                zone.TriggeredByPlayer = true;
                SpeakDialogue($"OBJECTIVE ZONE REACHED!\n[{zone.Name}] SECURED!", 3.2f);
                TriggerAdvanceMission(gameMode);
                if (_hasSeenEnemiesInBattle && AliveEnemyCount == 0)
                {
                    TriggerCompleteMission(gameMode);
                }
            }
        }

        NearestObjectiveDistance = bestDist < float.MaxValue ? bestDist : -1f;
        NearestObjectiveName = bestName;
    }

    private static void DiscoverSceneMissionObjectives(DeathmatchGameMode? gameMode)
    {
        try
        {
            var candidatePoints = new List<(Vector3 Pos, string Label)>();

            if (gameMode != null && gameMode.missions != null)
            {
                for (int i = 0; i < gameMode.missions.Length; i++)
                {
                    var m = gameMode.missions[i];
                    if (m == null || m.gameObject == null || !m.gameObject.activeInHierarchy)
                        continue;

                    var transforms = m.GetComponentsInChildren<Transform>(false);
                    if (transforms != null)
                    {
                        foreach (Transform tr in transforms)
                        {
                            if (tr == null) continue;
                            Vector3 p = tr.position;
                            if (Mathf.Abs(p.x) < 2f && Mathf.Abs(p.z) < 2f)
                                continue;
                            if (tr.GetComponentInParent<VehicleBehaviour>() != null)
                                continue;

                            string zoneName = tr.name ?? "OBJECTIVE";
                            if (IsLikelyObjectiveName(zoneName) || tr.GetComponent<Collider>() != null)
                            {
                                candidatePoints.Add((ProjectToTerrainGround(p), CleanObjectiveName(zoneName)));
                            }
                        }
                    }
                }
            }

            foreach (var (pos, label) in candidatePoints)
            {
                bool exists = false;
                for (int i = 0; i < _objectiveZones.Count; i++)
                {
                    if (Vector2.Distance(
                        new Vector2(_objectiveZones[i].GroundPosition.x, _objectiveZones[i].GroundPosition.z),
                        new Vector2(pos.x, pos.z)) < ObjectiveMergeDistance)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists && _objectiveZones.Count < 6)
                {
                    _objectiveZones.Add(new ObjectiveZoneMarkerState
                    {
                        Name = label,
                        GroundPosition = pos,
                        Radius = 26f
                    });
                }
            }
        }
        catch
        {
        }
    }

    private static bool IsLikelyObjectiveName(string name)
    {
        // пока не работает потом доделаю
        return false;
    }

    private static string CleanObjectiveName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "OBJECTIVE ZONE";
        string s = raw.Replace("(Clone)", "").Replace("_", " ").Trim().ToUpperInvariant();
        if (s.Length > 18)
            s = s.Substring(0, 18).Trim();
        return string.IsNullOrEmpty(s) ? "OBJECTIVE ZONE" : s;
    }

    private static Vector3 ProjectToTerrainGround(Vector3 pos)
    {
        var rayOrigin = new Vector3(pos.x, pos.y + 250f, pos.z);
        var hits = Physics.RaycastAll(rayOrigin, Vector3.down, 600f, ~0, QueryTriggerInteraction.Ignore);
        if (hits != null && hits.Length > 0)
        {
            float bestDist = float.MaxValue;
            Vector3 bestPt = pos;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.isTrigger)
                    continue;
                if (h.collider.GetComponentInParent<VehicleBehaviour>() != null)
                    continue;
                if (h.distance < bestDist)
                {
                    bestDist = h.distance;
                    bestPt = h.point;
                }
            }
            return bestPt;
        }
        return pos;
    }

    private static void StepEnemyTankRealCannonAi(
        VehicleBehaviour tank,
        EnemyTankRuntimeState state,
        Vector3 playerPos,
        float dt)
    {
        if (state.PostFireCooldown > 0f)
            state.PostFireCooldown -= dt;

        state.IsChargingShot = false;

        try
        {
            var commander = tank.OrderReciever?.TryCast<CommanderAI>() ?? tank.GetComponentInChildren<CommanderAI>();
            if (commander == null)
                return;

            var gunLayers = commander.gunLayers;
            if (gunLayers != null)
            {
                for (int i = 0; i < gunLayers.Length; i++)
                {
                    if (gunLayers[i] != null)
                        _enemyGunLayerPointers.Add(gunLayers[i].Pointer);
                }
            }

            var gunners = commander.gunners;
            if (gunners != null)
            {
                for (int i = 0; i < gunners.Length; i++)
                {
                    if (gunners[i] != null)
                        _enemyGunnerPointers.Add(gunners[i].Pointer);
                }
            }

            // небольшое упреждение по скорости игрока
            Vector3 playerVel = _playerBody != null ? _playerBody.velocity : Vector3.zero;
            float dist = Vector3.Distance(tank.transform.position, playerPos);
            if (dist < 2.0f || dist > 550.0f)
                return;

            float estimatedShellFlightTime = dist / 650.0f;
            Vector3 aimPoint = playerPos + new Vector3(playerVel.x, 0f, playerVel.z) * (estimatedShellFlightTime * 0.5f) + Vector3.up * 0.1f;

            if (gunLayers != null)
            {
                for (int i = 0; i < gunLayers.Length; i++)
                {
                    var layer = gunLayers[i]?.Layer;
                    if (layer != null)
                    {
                        layer.AimAtPosition(aimPoint, GunLayerAimFlags.None);
                    }
                }
            }

            if (gunners == null || state.PostFireCooldown > 0f)
                return;

            for (int i = 0; i < gunners.Length; i++)
            {
                var gunner = gunners[i]?.Gunner;
                if (gunner == null || !gunner.Functioning || !gunner.CanFire)
                    continue;

                Vector3 firePos = gunner.FirePosition;
                Vector3 toTarget = aimPoint - firePos;
                float shotDist = toTarget.magnitude;
                if (shotDist < 1.5f)
                    continue;

                Vector3 barrelForward = toTarget.normalized;
                try
                {
                    if (gunner.WeaponGameObject != null)
                        barrelForward = gunner.WeaponGameObject.transform.forward;
                }
                catch
                {
                }

                float angleOff = Vector3.Angle(barrelForward, toTarget);

                if (angleOff < 9.0f)
                {
                    state.IsChargingShot = true;
                    state.AimReadyTimer += dt;
                    IncomingShellWarningTimer = Mathf.Max(IncomingShellWarningTimer, 0.25f);
                }
                else
                {
                    state.AimReadyTimer = 0f;
                }

                if (angleOff < AimFireAngleDegrees ||
                    (angleOff < AimHoldAngleDegrees && state.AimReadyTimer >= AimHoldTime))
                {
                    if (Physics.Raycast(firePos + barrelForward * 1.5f, barrelForward, out RaycastHit obstacleHit, Mathf.Min(shotDist * 0.7f, 200f), ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (obstacleHit.collider != _playerCollider && obstacleHit.distance < shotDist * 0.45f)
                            continue;
                    }

                    gunner.Fire();
                    state.AimReadyTimer = 0f;
                    state.PostFireCooldown = 1.5f;
                    state.IsChargingShot = false;

                    _trackedCannonShots.Add(new TrackedRealCannonShot
                    {
                        Position = firePos,
                        Velocity = barrelForward * 650.0f,
                        AttackerName = tank.gameObject.name,
                        LifeTimer = 1.8f
                    });
                }
            }
        }
        catch
        {
        }
    }

    private static void UpdateTrackedCannonShots(float dt)
    {
        if (_trackedCannonShots.Count == 0)
            return;

        Vector3 playerPos = _playerObject != null ? _playerObject.transform.position : Vector3.zero;

        for (int i = _trackedCannonShots.Count - 1; i >= 0; i--)
        {
            TrackedRealCannonShot shot = _trackedCannonShots[i];
            shot.LifeTimer -= dt;
            if (shot.LifeTimer <= 0f)
            {
                _trackedCannonShots.RemoveAt(i);
                continue;
            }

            Vector3 prevPos = shot.Position;
            shot.Velocity += Physics.gravity * dt;
            Vector3 nextPos = prevPos + shot.Velocity * dt;
            shot.Position = nextPos;

            if (_playerObject != null)
            {
                float closestDistToPlayer = DistancePointToSegment(playerPos, prevPos, nextPos);
                if (closestDistToPlayer <= 1.65f)
                {
                    DamagePlayer(28, shot.AttackerName, prevPos);
                    _trackedCannonShots.RemoveAt(i);
                    continue;
                }
            }

            Vector3 seg = nextPos - prevPos;
            float segLen = seg.magnitude;
            if (segLen > 0.001f && Physics.Raycast(prevPos, seg / segLen, out RaycastHit hit, segLen, ~0, QueryTriggerInteraction.Ignore))
            {
                if (_playerObject != null)
                {
                    float splashDist = Vector3.Distance(hit.point, playerPos);
                    if (splashDist <= 6.5f)
                    {
                        int splashDmg = Mathf.Clamp(Mathf.RoundToInt(24f * (1f - splashDist / 7.0f)), 8, 24);
                        DamagePlayer(splashDmg, shot.AttackerName, hit.point);
                    }
                }
                _trackedCannonShots.RemoveAt(i);
            }
        }
    }

    private static float DistancePointToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.00001f)
            return Vector3.Distance(p, a);
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSq);
        Vector3 proj = a + ab * t;
        return Vector3.Distance(p, proj);
    }

    private static void EnsureTankWireframeObjects(EnemyTankRuntimeState state)
    {
        if (_wireframeMeshes == null || _wireframeNeonMaterials == null)
            return;

        if (state.IsFriendly)
        {
            if (state.OuterWireframeObj == null && _allyHaloMesh != null && _wireframeAllyMaterial != null)
            {
                state.OuterWireframeObj = new GameObject($"Doom_AllyHalo_{state.InstanceId}");
                var meshFilter = state.OuterWireframeObj.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = _allyHaloMesh;
                state.OuterRenderer = state.OuterWireframeObj.AddComponent<MeshRenderer>();
                state.OuterRenderer.shadowCastingMode = ShadowCastingMode.Off;
                state.OuterRenderer.receiveShadows = false;
                state.OuterRenderer.sharedMaterial = _wireframeAllyMaterial;
            }
            return;
        }

        if (state.OuterWireframeObj == null)
        {
            state.OuterWireframeObj = new GameObject($"Doom_TankWireframe_Outer_{state.InstanceId}");
            var meshFilter = state.OuterWireframeObj.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = _wireframeMeshes[state.ShapeIndex % _wireframeMeshes.Length];
            state.OuterRenderer = state.OuterWireframeObj.AddComponent<MeshRenderer>();
            state.OuterRenderer.shadowCastingMode = ShadowCastingMode.Off;
            state.OuterRenderer.receiveShadows = false;
            state.OuterRenderer.sharedMaterial = _wireframeNeonMaterials[state.ColorIndex % _wireframeNeonMaterials.Length];
        }

        if (state.InnerWireframeObj == null)
        {
            state.InnerWireframeObj = new GameObject($"Doom_TankWireframe_Inner_{state.InstanceId}");
            var meshFilter = state.InnerWireframeObj.AddComponent<MeshFilter>();
            meshFilter.sharedMesh = _wireframeMeshes[(state.ShapeIndex + 2) % _wireframeMeshes.Length];
            state.InnerRenderer = state.InnerWireframeObj.AddComponent<MeshRenderer>();
            state.InnerRenderer.shadowCastingMode = ShadowCastingMode.Off;
            state.InnerRenderer.receiveShadows = false;
            state.InnerRenderer.sharedMaterial = _wireframeNeonMaterials[(state.ColorIndex + 1) % _wireframeNeonMaterials.Length];
        }
    }

    private static void DestroyEnemyWireframeObjects(EnemyTankRuntimeState state)
    {
        if (state.OuterWireframeObj != null)
        {
            UnityEngine.Object.Destroy(state.OuterWireframeObj);
            state.OuterWireframeObj = null;
            state.OuterRenderer = null;
        }
        if (state.InnerWireframeObj != null)
        {
            UnityEngine.Object.Destroy(state.InnerWireframeObj);
            state.InnerWireframeObj = null;
            state.InnerRenderer = null;
        }
    }

    private static void CleanupEnemyWireframesAndShells()
    {
        foreach (var kv in _enemyStates)
        {
            DestroyEnemyWireframeObjects(kv.Value);
        }
        _enemyStates.Clear();
        _enemyGunLayerPointers.Clear();
        _enemyGunnerPointers.Clear();
        _trackedCannonShots.Clear();

        for (int i = 0; i < _objectiveZones.Count; i++)
        {
            if (_objectiveZones[i].BeaconObj != null)
            {
                UnityEngine.Object.Destroy(_objectiveZones[i].BeaconObj);
                _objectiveZones[i].BeaconObj = null;
                _objectiveZones[i].BeaconRenderer = null;
            }
        }
        _objectiveZones.Clear();
        NearestObjectiveDistance = -1f;
        NearestObjectiveName = string.Empty;
    }

    internal static void FixedUpdate()
    {
        if (!IsActive || _playerBody == null || _playerObject == null)
            return;

        StopPlayerTankDriver();

        if (IsPaused)
        {
            _playerBody.velocity = Vector3.zero;
            return;
        }

        float dt = Time.fixedDeltaTime;

        if (_jumpCooldownTimer > 0f)
        {
            _jumpCooldownTimer -= dt;
        }

        Vector3 currentVel = _playerBody.velocity;
        bool rawGrounded = CheckGrounded(out Vector3 groundNormal);

        // вертикальная составляющая нужная для движения вдоль склона
        float slopeFollowY = 0f;
        if (rawGrounded && groundNormal.y > 0.35f)
        {
            slopeFollowY = -(_planarVelocity.x * groundNormal.x + _planarVelocity.z * groundNormal.z) / groundNormal.y;
            slopeFollowY = Mathf.Clamp(slopeFollowY, -12f, 22f);
        }

        if (!rawGrounded)
        {
            _airFramesSinceJump++;
            if (_airFramesSinceJump >= MinAirFramesBeforeBhop && currentVel.y <= BhopApexVerticalSpeed)
            {
                _mustLeaveGroundBeforeNextJump = false;
            }
        }
        else if (_spaceReleasedSinceLastJump && _jumpCooldownTimer <= 0f)
        {
            _mustLeaveGroundBeforeNextJump = false;
        }

        float velAlongNormal = Vector3.Dot(currentVel, groundNormal);
        bool grounded = rawGrounded && (_jumpCooldownTimer <= 0f) && (velAlongNormal <= 2.0f);

        if (grounded)
        {
            _groundedGraceTimer = 0.08f;
        }
        else if (_groundedGraceTimer > 0f)
        {
            _groundedGraceTimer -= dt;
        }

        Vector3 forward = Quaternion.Euler(0f, _yaw, 0f) * Vector3.forward;
        Vector3 right = Quaternion.Euler(0f, _yaw, 0f) * Vector3.right;
        Vector3 wishDir = (forward * _moveInput.y + right * _moveInput.x);
        if (wishDir.sqrMagnitude > 1f)
            wishDir.Normalize();

        float baseTargetSpeed = _sprintHeld
            ? DoomPlugin.ConfigSprintSpeed.Value
            : DoomPlugin.ConfigWalkSpeed.Value;

        _planarVelocity = new Vector3(currentVel.x, 0f, currentVel.z);
        float verticalVel = currentVel.y;

        bool bhopEnabled = DoomPlugin.ConfigEnableBunnyhop.Value;
        bool canTriggerJump = grounded && !_mustLeaveGroundBeforeNextJump && (_jumpCooldownTimer <= 0f);
        bool wantsBhopNow = canTriggerJump && (_jumpQueued || (bhopEnabled && _jumpHeld));

        if (wantsBhopNow)
        {
            _jumpQueued = false;
            _spaceReleasedSinceLastJump = false;
            _mustLeaveGroundBeforeNextJump = true;
            _airFramesSinceJump = 0;
            _jumpCooldownTimer = 0.42f;
            grounded = false;
            _groundedGraceTimer = 0f;

            verticalVel = Mathf.Max(0f, slopeFollowY * 0.92f) + DoomPlugin.ConfigJumpVelocity.Value;
            _playerObject.transform.position += Vector3.up * 0.14f;

            if (bhopEnabled && wishDir.sqrMagnitude > 0.01f)
            {
                BhopChainCount++;
                float currentSpeed = _planarVelocity.magnitude;
                float boost = DoomPlugin.ConfigBhopBoostPerJump.Value;
                float maxSpeed = DoomPlugin.ConfigBhopMaxSpeed.Value;

                float newSpeed = Mathf.Max(currentSpeed, baseTargetSpeed) + boost;
                newSpeed = Mathf.Min(newSpeed, maxSpeed);

                Vector3 blendedDir = (_planarVelocity.sqrMagnitude > 1f)
                    ? Vector3.Lerp(_planarVelocity.normalized, wishDir, 0.42f).normalized
                    : wishDir;

                _planarVelocity = blendedDir * newSpeed;

                if (BhopChainCount >= 2 && DialogueOverrideTimer <= 0.2f)
                {
                    int uSpeed = Mathf.RoundToInt(newSpeed * 32f);
                    SpeakDialogue($"SUSPICIOUS VELOCITY // BHOP x{BhopChainCount}\nSPEED: {uSpeed} u/s", 1.5f);
                }
            }
            else
            {
                BhopChainCount = 1;
            }

            DoomAudio.PlayBhopJumpSound(BhopChainCount);
        }
        else if (grounded)
        {
            if (!_jumpHeld && _wasGrounded)
            {
                BhopChainCount = 0;
            }

            ApplyGroundFriction(ref _planarVelocity, dt);
            Accelerate(ref _planarVelocity, wishDir, baseTargetSpeed, GroundAccelerate, dt);

            if (groundNormal.y > 0.35f)
            {
                float updatedSlopeY = -(_planarVelocity.x * groundNormal.x + _planarVelocity.z * groundNormal.z) / groundNormal.y;
                verticalVel = Mathf.Clamp(updatedSlopeY, -8f, 18f) - 0.35f;
            }
            else if (verticalVel < -1f)
            {
                verticalVel = -1f;
            }
        }
        else
        {
            float airWishSpeed = Mathf.Max(baseTargetSpeed * 1.15f, 14f);
            AirAccelerate(ref _planarVelocity, wishDir, airWishSpeed, DoomPlugin.ConfigAirAccelerate.Value, dt);
            verticalVel -= GravityAcceleration * dt;
        }

        float maxAllowed = DoomPlugin.ConfigBhopMaxSpeed.Value;
        if (_planarVelocity.magnitude > maxAllowed)
        {
            _planarVelocity = _planarVelocity.normalized * maxAllowed;
        }

        CurrentHorizontalSpeed = _planarVelocity.magnitude;
        if (CurrentHorizontalSpeed > PeakHorizontalSpeed)
            PeakHorizontalSpeed = CurrentHorizontalSpeed;

        _playerBody.velocity = new Vector3(_planarVelocity.x, verticalVel, _planarVelocity.z);
        _wasGrounded = grounded;
    }

    private static void ApplyGroundFriction(ref Vector3 planarVel, float dt)
    {
        float speed = planarVel.magnitude;
        if (speed < 0.05f)
        {
            planarVel = Vector3.zero;
            return;
        }

        float control = Mathf.Max(speed, 5.0f);
        float drop = control * GroundFriction * dt;
        float newSpeed = Mathf.Max(0f, speed - drop);
        planarVel *= (newSpeed / speed);
    }

    private static void Accelerate(ref Vector3 planarVel, Vector3 wishDir, float wishSpeed, float accel, float dt)
    {
        if (wishDir.sqrMagnitude < 0.0001f)
            return;

        float currentSpeed = Vector3.Dot(planarVel, wishDir);
        float addSpeed = wishSpeed - currentSpeed;
        if (addSpeed <= 0f)
            return;

        float accelSpeed = accel * wishSpeed * dt;
        if (accelSpeed > addSpeed)
            accelSpeed = addSpeed;

        planarVel += wishDir * accelSpeed;
    }

    private static void AirAccelerate(ref Vector3 planarVel, Vector3 wishDir, float wishSpeed, float airAccel, float dt)
    {
        if (wishDir.sqrMagnitude < 0.0001f)
            return;

        float cappedWishSpeed = Mathf.Min(wishSpeed, 9.5f);
        float currentSpeed = Vector3.Dot(planarVel, wishDir);
        float addSpeed = cappedWishSpeed - currentSpeed;
        if (addSpeed <= 0f)
            return;

        float accelSpeed = airAccel * wishSpeed * dt;
        if (accelSpeed > addSpeed)
            accelSpeed = addSpeed;

        planarVel += wishDir * accelSpeed;
    }

    internal static void LateUpdate()
    {
        if (!IsActive || _doomCamera == null || _playerObject == null)
            return;

        float dt = Time.unscaledDeltaTime;
        _recoilPitchOffset = Mathf.Lerp(_recoilPitchOffset, 0f, Mathf.Clamp01(dt * 10f));
        _recoilRollOffset = Mathf.Lerp(_recoilRollOffset, 0f, Mathf.Clamp01(dt * 12f));
        _damageShakeOffset = Mathf.Lerp(_damageShakeOffset, 0f, Mathf.Clamp01(dt * 9f));

        float bobIntensity = DoomPlugin.ConfigCameraBobIntensity.Value;
        float camBobX = Mathf.Cos(_bobPhase * 0.5f) * 0.045f * _bobAmplitude * bobIntensity;
        float camBobY = Mathf.Abs(Mathf.Sin(_bobPhase)) * 0.065f * _bobAmplitude * bobIntensity;
        float speedTilt = -_moveInput.x * 1.8f * Mathf.Clamp01(CurrentHorizontalSpeed / 20f);

        float shakePitch = Mathf.Sin(Time.unscaledTime * 55f) * _damageShakeOffset;
        float shakeRoll = Mathf.Cos(Time.unscaledTime * 48f) * _damageShakeOffset;

        _doomCamera.transform.localPosition = _cameraLocalBasePos + new Vector3(camBobX, camBobY, 0f);

        float finalPitch = Mathf.Clamp(_pitch - _recoilPitchOffset + shakePitch, -88f, 88f);
        _doomCamera.transform.rotation = Quaternion.Euler(finalPitch, _yaw, _recoilRollOffset + speedTilt + shakeRoll);
    }

    internal static void OnGUI()
    {
        if (!IsActive)
            return;

        DoomWeaponView.DrawOnGUI();
    }

    private static void EnterDoomMode()
    {
        _originalCamera = Camera.main;
        if (_originalCamera == null)
        {
            Camera[] allCams = UnityEngine.Object.FindObjectsOfType<Camera>();
            foreach (Camera c in allCams)
            {
                if (c != null && c.enabled)
                {
                    _originalCamera = c;
                    break;
                }
            }
        }

        if (_originalCamera == null)
        {
            DoomPlugin.ModLog.LogWarning("в сцене нет активной камеры");
            return;
        }

        _playerVehicle = FindPlayerVehicle();
        SnapshotArenaTanks();

        Vector3 spawnPos;
        Vector3 lookForward;

        if (_playerVehicle != null)
        {
            Transform vt = _playerVehicle.transform;
            spawnPos = vt.position + vt.right * 4.2f + Vector3.up * 1.6f;
            lookForward = vt.forward;

            if (Physics.Raycast(spawnPos + Vector3.up * 6f, Vector3.down, out RaycastHit groundHit, 25f, ~0, QueryTriggerInteraction.Ignore))
            {
                spawnPos = groundHit.point + Vector3.up * (PlayerHeight * 0.55f);
            }
        }
        else
        {
            spawnPos = _originalCamera.transform.position + _originalCamera.transform.forward * 2.0f;
            lookForward = _originalCamera.transform.forward;
        }

        lookForward.y = 0f;
        if (lookForward.sqrMagnitude < 0.001f)
            lookForward = Vector3.forward;
        lookForward.Normalize();

        _yaw = Quaternion.LookRotation(lookForward, Vector3.up).eulerAngles.y;
        _pitch = 0f;
        _recoilPitchOffset = 0f;
        _recoilRollOffset = 0f;
        _damageShakeOffset = 0f;
        _respawnTimer = 0f;
        _jumpCooldownTimer = 0f;
        _mustLeaveGroundBeforeNextJump = false;
        _airFramesSinceJump = 0;
        _spaceReleasedSinceLastJump = true;
        _manualEscPaused = false;
        IsPaused = false;
        _planarVelocity = Vector3.zero;
        CurrentHorizontalSpeed = 0f;
        BhopChainCount = 0;
        PlayerHealth = MaxPlayerHealth;
        _missionSuccessChecked = false;

        DisableVehicleControlsAndHud();

        _playerObject = new GameObject("Doomguy_PlayerBody");
        _playerObject.transform.position = spawnPos;
        _playerObject.transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

        _playerCollider = _playerObject.AddComponent<CapsuleCollider>();
        _playerCollider.height = PlayerHeight;
        _playerCollider.radius = PlayerRadius;
        _playerCollider.center = Vector3.zero;

        _playerBody = _playerObject.AddComponent<Rigidbody>();
        _playerBody.mass = PlayerMass;
        _playerBody.useGravity = false;
        _playerBody.freezeRotation = true;
        _playerBody.interpolation = RigidbodyInterpolation.Interpolate;
        _playerBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        var camObj = new GameObject("Doomguy_FirstPersonCamera");
        camObj.transform.SetParent(_playerObject.transform, false);
        camObj.transform.localPosition = _cameraLocalBasePos;

        _doomCamera = camObj.AddComponent<Camera>();
        _doomCamera.CopyFrom(_originalCamera);
        _doomCamera.nearClipPlane = 0.06f;
        _doomCamera.fieldOfView = 92f;

        try
        {
            var srcHd = _originalCamera.GetComponent<HDAdditionalCameraData>();
            if (srcHd != null)
            {
                var dstHd = camObj.AddComponent<HDAdditionalCameraData>();
                srcHd.CopyTo(dstHd);
            }
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogWarning($"HDAdditionalCameraData: {ex.Message}");
        }

        _originalCamera.enabled = false;

        DoomWeaponView.AttachToCamera(_doomCamera);

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        IsActive = true;

        SpeakDialogue("SUSSY SLAYER DEPLOYED.\nRIP & TEAR EVERY TANK.", 3.5f);
        RefreshScoreboard();

        if (_killFeed.Count == 0)
        {
            PushKillFeed("sussy_slayer", "ejected", "200mm", "tank_hatch");
        }

        try
        {
            DoomAudio.StartBackgroundMusic();
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogWarning(ex.Message);
        }

        DoomPlugin.ModLog.LogDebug($"Вход в режим в {spawnPos}");
    }

    private static void ExitDoomMode(bool keepVictoryAnthem = false)
    {
        IsActive = false;
        IsPaused = false;
        _manualEscPaused = false;

        CleanupEnemyWireframesAndShells();
        DoomWeaponView.Detach();
        DoomAudio.StopBackgroundMusic(keepVictoryAnthem);

        if (_doomCamera != null)
        {
            UnityEngine.Object.Destroy(_doomCamera.gameObject);
            _doomCamera = null;
        }

        if (_playerObject != null)
        {
            UnityEngine.Object.Destroy(_playerObject);
            _playerObject = null;
            _playerBody = null;
            _playerCollider = null;
        }

        if (_originalCamera != null)
        {
            _originalCamera.enabled = true;
            _originalCamera = null;
        }

        RestoreVehicleControlsAndHud();

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        DoomPlugin.ModLog.LogDebug("выход из режима, возврат в танк");
    }

    private static void ReadMovementAndLookInput(Keyboard kb)
    {
        float mx = 0f;
        float my = 0f;

        if (kb[Key.W].isPressed || kb[Key.UpArrow].isPressed) my += 1f;
        if (kb[Key.S].isPressed || kb[Key.DownArrow].isPressed) my -= 1f;
        if (kb[Key.D].isPressed || kb[Key.RightArrow].isPressed) mx += 1f;
        if (kb[Key.A].isPressed || kb[Key.LeftArrow].isPressed) mx -= 1f;

        _moveInput = new Vector2(mx, my);
        if (_moveInput.sqrMagnitude > 1f)
            _moveInput.Normalize();

        _sprintHeld = kb[Key.LeftShift].isPressed || kb[Key.RightShift].isPressed;
        _jumpHeld = kb[Key.Space].isPressed;

        if (!_jumpHeld)
        {
            _spaceReleasedSinceLastJump = true;
        }

        if (kb[Key.Space].wasPressedThisFrame)
        {
            _jumpQueued = true;
        }

        Mouse? mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 delta = mouse.delta.ReadValue();
            float sens = DoomPlugin.ConfigMouseSensitivity.Value;
            _yaw += delta.x * sens;
            _pitch -= delta.y * sens;
            _pitch = Mathf.Clamp(_pitch, -88f, 88f);
        }

        float dt = Time.unscaledDeltaTime;
        bool movingOnGround = (_groundedGraceTimer > 0f) && (_moveInput.sqrMagnitude > 0.02f || CurrentHorizontalSpeed > 1.5f);

        if (movingOnGround)
        {
            float speedFactor = Mathf.Clamp(CurrentHorizontalSpeed / Mathf.Max(1f, DoomPlugin.ConfigWalkSpeed.Value), 0.6f, 2.8f);
            _bobPhase += dt * 9.5f * speedFactor;
            _bobAmplitude = Mathf.Lerp(_bobAmplitude, Mathf.Clamp(speedFactor, 0.7f, 1.8f), Mathf.Clamp01(dt * 10f));
        }
        else
        {
            _bobPhase += dt * 1.8f;
            _bobAmplitude = Mathf.Lerp(_bobAmplitude, 0.08f, Mathf.Clamp01(dt * 8f));
        }
    }

    private static bool CheckGrounded(out Vector3 groundNormal)
    {
        groundNormal = Vector3.up;
        if (_playerObject == null)
            return false;

        Vector3 origin = _playerObject.transform.position;
        float rayLength = (PlayerHeight * 0.5f) + 0.20f;

        if (Physics.SphereCast(origin, PlayerRadius * 0.72f, Vector3.down, out RaycastHit hit, rayLength, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider != _playerCollider && hit.normal.y >= 0.42f)
            {
                groundNormal = hit.normal;
                return true;
            }
        }

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit rayHit, rayLength + 0.08f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (rayHit.collider != _playerCollider && rayHit.normal.y >= 0.42f)
            {
                groundNormal = rayHit.normal;
                return true;
            }
        }

        return false;
    }

    private static void TryFireSuperShotgun()
    {
        if (_doomCamera == null)
            return;

        if (!DoomWeaponView.CanFireNow())
            return;

        ShotsFired++;
        DoomWeaponView.TriggerFireAnimation();
        DoomAudio.PlayShotgunBlast();

        _recoilPitchOffset += 6.8f;
        _recoilRollOffset += UnityEngine.Random.Range(-2.4f, 2.4f);

        if (_playerBody != null && _groundedGraceTimer <= 0f)
        {
            Vector3 knockback = -_doomCamera.transform.forward * 4.5f;
            _playerBody.velocity += new Vector3(knockback.x, Mathf.Clamp(knockback.y * 0.5f, -2f, 4f), knockback.z);
        }

        DoomBallistics.FireSuperShotgun(
            _doomCamera,
            _playerCollider,
            DoomPlugin.ConfigCaliberMm.Value,
            DoomPlugin.ConfigMuzzleVelocityMs.Value,
            BuckshotMode);
    }

    private static void SnapshotArenaTanks()
    {
        _tankEliminatedState.Clear();
        try
        {
            VehicleBehaviour[] tanks = UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>();
            if (tanks == null)
                return;

            foreach (VehicleBehaviour tank in tanks)
            {
                if (tank == null || tank.gameObject == null)
                    continue;

                int id = tank.gameObject.GetInstanceID();
                _tankEliminatedState[id] = IsTankKnockedOut(tank);
            }
        }
        catch
        {
        }
    }

    private static void PollDestroyedTanks()
    {
        try
        {
            VehicleBehaviour[] tanks = UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>();
            if (tanks != null)
            {
                foreach (VehicleBehaviour tank in tanks)
                {
                    if (tank == null || tank.gameObject == null)
                        continue;

                    if (IsFriendlyTank(tank))
                        continue;

                    int id = tank.gameObject.GetInstanceID();
                    bool wasOut = _tankEliminatedState.TryGetValue(id, out bool prev) && prev;
                    bool isOutNow = IsTankKnockedOut(tank);

                    if (!wasOut && isOutNow)
                    {
                        _tankEliminatedState[id] = true;
                        if (_tanksHitByPlayer.Contains(id) || (_lastDamagedTankId == id && (Time.unscaledTime - _lastDamagedTankTime) < 8.0f))
                        {
                            NotifyTankKilled(tank.gameObject.name, id);
                        }
                    }
                    else if (!_tankEliminatedState.ContainsKey(id))
                    {
                        _tankEliminatedState[id] = isOutNow;
                    }
                }
            }

            if (!_missionSuccessChecked)
            {
                Scene winScene = SceneManager.GetSceneByName("MissionSuccessScreen");
                if (winScene.IsValid() && winScene.isLoaded)
                {
                    _missionSuccessChecked = true;
                    if (_lastDamagedTankId != -1 && (Time.unscaledTime - _lastDamagedTankTime) < 6.0f)
                    {
                        bool alreadyCounted = _tankEliminatedState.TryGetValue(_lastDamagedTankId, out bool p) && p;
                        if (!alreadyCounted)
                        {
                            _tankEliminatedState[_lastDamagedTankId] = true;
                            NotifyTankKilled(_lastDamagedTankName, _lastDamagedTankId);
                        }
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static void RefreshScoreboard()
    {
        _blasterScores.Clear();
        _blasterScores.Add(new BlasterScoreEntry
        {
            Rank = 1,
            Name = "sussy_slayer",
            Score = TotalKills * 10 + TotalHits,
            Alive = true
        });

        try
        {
            VehicleBehaviour[] tanks = UnityEngine.Object.FindObjectsOfType<VehicleBehaviour>();
            if (tanks != null)
            {
                int rank = 2;
                foreach (VehicleBehaviour tank in tanks)
                {
                    if (rank > 8)
                        break;
                    if (tank == null || tank.gameObject == null)
                        continue;
                    if (_playerVehicle != null && tank.gameObject == _playerVehicle)
                        continue;

                    bool knockedOut = IsTankKnockedOut(tank);
                    bool isAlly = IsFriendlyTank(tank);
                    _blasterScores.Add(new BlasterScoreEntry
                    {
                        Rank = rank,
                        Name = CleanTargetName(tank.gameObject.name),
                        Score = knockedOut ? 0 : (9 - rank),
                        Alive = !knockedOut,
                        IsAlly = isAlly
                    });
                    rank++;
                }
            }
        }
        catch
        {
        }

        while (_blasterScores.Count < 8)
        {
            int r = _blasterScores.Count + 1;
            _blasterScores.Add(new BlasterScoreEntry
            {
                Rank = r,
                Name = $"bogey_0{r}",
                Score = 0,
                Alive = false
            });
        }
    }

    internal static bool IsTankKnockedOut(VehicleBehaviour? tank)
    {
        if (tank == null || tank.Pointer == IntPtr.Zero || tank.WasCollected)
            return true;

        try
        {
            if (!tank.gameObject.activeInHierarchy || !tank.enabled)
                return true;

            VehicleFlags flags = tank.Flags;
            if ((flags & VehicleFlags.Enabled) == 0)
                return true;
            if ((flags & (VehicleFlags.Mobile | VehicleFlags.Armed)) == 0)
                return true;

            int totalCrew = 0;
            int aliveCrew = 0;
            double currentHp = 0;
            double maxHp = 0;

            var entries = tank.healthRegister?.entries;
            int count = entries?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                var comp = entries![i];
                if (comp == null || comp.Pointer == IntPtr.Zero || comp.WasCollected)
                    continue;

                var durable = comp.HealthPool;
                if (durable != null)
                {
                    var h = durable.HealthInfo;
                    if (float.IsFinite(h.Current) && float.IsFinite(h.Max) && h.Max > 0)
                    {
                        currentHp += Math.Clamp(h.Current, 0f, h.Max);
                        maxHp += h.Max;
                    }
                }

                var owner = comp.Owner;
                if (owner == null || owner.Pointer == IntPtr.Zero || owner.WasCollected)
                    continue;

                CrewSeat? seat = owner.TryCast<CrewSeat>();
                if (seat != null)
                {
                    totalCrew++;
                    float cur = durable?.HealthInfo.Current ?? 1f;
                    if (seat.HealthFraction > 0.001f && cur > 0.001f)
                        aliveCrew++;
                }
                else if (owner.GetIl2CppType()?.Name?.IndexOf("Crew", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    totalCrew++;
                    float cur = durable?.HealthInfo.Current ?? 1f;
                    if (owner.HealthFraction > 0.001f && cur > 0.001f)
                        aliveCrew++;
                }
            }

            if (totalCrew == 0)
            {
                var seats = tank.GetComponentsInChildren<CrewSeat>(true);
                if (seats != null && seats.Length > 0)
                {
                    for (int i = 0; i < seats.Length; i++)
                    {
                        var s = seats[i];
                        if (s != null && s.Pointer != IntPtr.Zero && !s.WasCollected)
                        {
                            totalCrew++;
                            float cur = s.healthPool?.HealthInfo.Current ?? 1f;
                            if (s.HealthFraction > 0.001f && cur > 0.001f)
                                aliveCrew++;
                        }
                    }
                }
            }

            if (totalCrew > 0 && aliveCrew == 0)
                return true;
            if (maxHp > 0 && currentHp <= 0.001)
                return true;
        }
        catch
        {
            try
            {
                return (tank.Flags & (VehicleFlags.Mobile | VehicleFlags.Armed)) == 0;
            }
            catch
            {
                return true;
            }
        }

        return false;
    }

    private static GameObject? FindPlayerVehicle()
    {
        try
        {
            var vehicleController = UnityEngine.Object.FindObjectOfType<VehicleController>();
            if (vehicleController != null && vehicleController.ControlledVehicleGameObject != null)
            {
                return vehicleController.ControlledVehicleGameObject;
            }
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogWarning($"нет VehicleController: {ex.Message}");
        }

        return null;
    }

    private static void StopPlayerTankDriver()
    {
        if (_playerVehicle == null)
            return;

        try
        {
            var commander = _playerVehicle.GetComponentInChildren<CommanderAI>();
            if (commander != null && commander.driver != null && commander.driver.target != null)
            {
                commander.driver.target.Solution = new DriveSolution
                {
                    Steer = 0f,
                    Throttle = 0f
                };
            }
        }
        catch
        {
        }
    }

    private static void DisableVehicleControlsAndHud()
    {
        _disabledActions.Clear();
        _disabledCanvases.Clear();

        try
        {
            var enabledActions = InputSystem.ListEnabledActions();
            if (enabledActions != null)
            {
                foreach (InputAction action in enabledActions)
                {
                    if (action != null && action.enabled)
                    {
                        // действия паузы и меню не отключаем, чтобы ESC открывал штатное меню игры
                        string actName = action.name ?? string.Empty;
                        string mapName = action.actionMap?.name ?? string.Empty;
                        if (actName.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            actName.IndexOf("Escape", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            actName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            mapName.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            mapName.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            continue;
                        }

                        action.Disable();
                        _disabledActions.Add(action);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogWarning(ex.Message);
        }

        try
        {
            Scene uiScene = SceneManager.GetSceneByName("VehicleControlUI");
            if (uiScene.IsValid() && uiScene.isLoaded)
            {
                var roots = uiScene.GetRootGameObjects();
                foreach (GameObject root in roots)
                {
                    var canvases = root.GetComponentsInChildren<Canvas>(false);
                    foreach (Canvas canvas in canvases)
                    {
                        if (canvas != null && canvas.enabled)
                        {
                            canvas.enabled = false;
                            _disabledCanvases.Add(canvas);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogWarning(ex.Message);
        }
    }

    private static void RestoreVehicleControlsAndHud()
    {
        foreach (InputAction action in _disabledActions)
        {
            try
            {
                action?.Enable();
            }
            catch
            {
            }
        }
        _disabledActions.Clear();

        foreach (Canvas canvas in _disabledCanvases)
        {
            try
            {
                if (canvas != null)
                    canvas.enabled = true;
            }
            catch
            {
            }
        }
        _disabledCanvases.Clear();
    }
}
