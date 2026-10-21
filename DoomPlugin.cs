using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

[assembly: AssemblyMetadata("Sprocket.Mod.Id", "ru.doomguy.sprocket")]
[assembly: AssemblyMetadata("Sprocket.Mod.DisplayName", "DOOMGUY 200mm Super Shotgun + Bunnyhop")]
[assembly: AssemblyMetadata("Sprocket.Mod.Description", "Exit your tank with End (or F8), bunnyhop across the battlefield, dodge enemy tank shells, and obliterate enemy armor with a 200mm (2000 m/s) Super Shotgun.")]
[assembly: AssemblyMetadata("Sprocket.Mod.Category", "gameplay")]
[assembly: AssemblyMetadata("Sprocket.Mod.License", "MIT")]

namespace SprocketDoomMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class DoomPlugin : BasePlugin
{
    public const string PluginGuid = "ru.doomguy.sprocket";
    public const string PluginName = "Sprocket DOOMGUY Mod";
    public const string PluginVersion = "1.0.0";

    internal static ManualLogSource ModLog { get; private set; } = null!;

    internal static ConfigEntry<Key> ConfigToggleKey = null!;
    internal static ConfigEntry<Key> ConfigAltToggleKey = null!;
    internal static ConfigEntry<Key> ConfigBuckshotToggleKey = null!;
    internal static ConfigEntry<Key> ConfigRenderModeKey = null!;
    internal static ConfigEntry<Key> ConfigDitherToggleKey = null!;
    internal static ConfigEntry<float> ConfigCaliberMm = null!;
    internal static ConfigEntry<float> ConfigMuzzleVelocityMs = null!;
    internal static ConfigEntry<float> ConfigWalkSpeed = null!;
    internal static ConfigEntry<float> ConfigSprintSpeed = null!;
    internal static ConfigEntry<float> ConfigJumpVelocity = null!;
    internal static ConfigEntry<bool> ConfigEnableBunnyhop = null!;
    internal static ConfigEntry<float> ConfigBhopBoostPerJump = null!;
    internal static ConfigEntry<float> ConfigBhopMaxSpeed = null!;
    internal static ConfigEntry<float> ConfigAirAccelerate = null!;
    internal static ConfigEntry<float> ConfigMouseSensitivity = null!;
    internal static ConfigEntry<float> ConfigCameraBobIntensity = null!;
    internal static ConfigEntry<float> ConfigWeaponBobIntensity = null!;
    internal static ConfigEntry<float> ConfigMusicVolume = null!;
    internal static ConfigEntry<float> ConfigSfxVolume = null!;
    internal static ConfigEntry<bool> ConfigShowDoomHud = null!;
    internal static ConfigEntry<bool> ConfigEnableDitherShader = null!;
    internal static ConfigEntry<bool> ConfigEnableEnemyFire = null!;
    internal static ConfigEntry<bool> ConfigEnableEnemyWireframes = null!;

    private DoomRuntimeBehaviour? _runtime;

    internal static string PluginDirectory
    {
        get
        {
            var location = typeof(DoomPlugin).Assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var directory = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    return directory;
            }

            return Paths.PluginPath;
        }
    }

    internal static byte[]? LoadAssetBytes(string relativeFileName, string embeddedLogicalName)
    {
        var diskPath = FindDiskAssetPath(relativeFileName);
        if (diskPath != null && File.Exists(diskPath))
        {
            try
            {
                return File.ReadAllBytes(diskPath);
            }
            catch (Exception ex)
            {
                ModLog?.LogWarning($"не удалось прочитать {diskPath}: {ex.Message}");
            }
        }

        try
        {
            var assembly = typeof(DoomPlugin).Assembly;
            using var stream = assembly.GetManifestResourceStream(embeddedLogicalName);
            if (stream != null)
            {
                using var buffer = new MemoryStream((int)stream.Length);
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
        catch (Exception ex)
        {
            ModLog?.LogWarning($"ошибка чтения ресурса {embeddedLogicalName}: {ex.Message}");
        }

        return null;
    }

    private static string? FindDiskAssetPath(string relativeFileName)
    {
        var baseDir = PluginDirectory;
        var justFile = Path.GetFileName(relativeFileName);

        string[] candidates =
        {
            Path.Combine(baseDir, relativeFileName),
            Path.Combine(baseDir, "sounds", justFile),
            Path.Combine(baseDir, "Assets", relativeFileName),
            Path.Combine(baseDir, "Assets", justFile),
            Path.Combine(baseDir, "SprocketDoomMod", "Assets", relativeFileName),
            Path.Combine(baseDir, "SprocketDoomMod", "Assets", justFile),
            Path.Combine(baseDir, "SprocketDoomMod", "sounds", justFile),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public override void Load()
    {
        ModLog = Log;

        BindConfiguration();

        try
        {
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp<DoomRuntimeBehaviour>())
                ClassInjector.RegisterTypeInIl2Cpp<DoomRuntimeBehaviour>();
        }
        catch
        {
            // похуй 
        }

        try
        {
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(DoomEnemyAiHooks));
        }
        catch (Exception ex)
        {
            Log.LogWarning($"не пропатчился ИИ: {ex.Message}");
        }

        _runtime = AddComponent<DoomRuntimeBehaviour>();
        _runtime.Configure(
            DoomController.Update,
            DoomController.FixedUpdate,
            DoomController.LateUpdate,
            DoomController.OnGUI,
            DoomController.OnSceneChanged);
    }

    public override bool Unload()
    {
        try
        {
            DoomController.Shutdown();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"ошибка при выгрузке: {ex.Message}");
        }

        return true;
    }

    // конфиг
    private void BindConfiguration()
    {
        ConfigToggleKey = Config.Bind(
            "Controls", "ToggleDoomguyKey", Key.End,
            "Key to enter or leave Doomguy mode.");

        ConfigAltToggleKey = Config.Bind(
            "Controls", "AltToggleDoomguyKey", Key.F8,
            "Spare key for the same action.");

        ConfigBuckshotToggleKey = Config.Bind(
            "Controls", "ToggleBuckshotKey", Key.B,
            "Switches between the 200mm APHE slug and the 200mm buckshot load.");

        ConfigRenderModeKey = Config.Bind(
            "Controls", "ToggleWeaponRenderModeKey", Key.F9,
            "Switches the weapon between the 3D cutout mesh and the flat 2D overlay.");

        ConfigDitherToggleKey = Config.Bind(
            "Controls", "ToggleDitherShaderKey", Key.F10,
            "Toggles the retro dithering and scanline overlay.");

        ConfigCaliberMm = Config.Bind(
            "Weapon", "ShotgunCaliberMm", 200.0f,
            "Projectile caliber in millimeters.");

        ConfigMuzzleVelocityMs = Config.Bind(
            "Weapon", "MuzzleVelocityMs", 2000.0f,
            "Muzzle velocity in meters per second.");

        ConfigWalkSpeed = Config.Bind(
            "Movement", "WalkSpeed", 11.0f,
            "Walking speed in meters per second.");

        ConfigSprintSpeed = Config.Bind(
            "Movement", "SprintSpeed", 19.0f,
            "Sprinting speed while Left Shift is held.");

        ConfigJumpVelocity = Config.Bind(
            "Movement", "JumpVelocity", 8.2f,
            "Vertical jump velocity in meters per second.");

        ConfigEnableBunnyhop = Config.Bind(
            "Movement", "EnableBunnyhop", true,
            "Keep the speed gained across consecutive jumps.");

        ConfigBhopBoostPerJump = Config.Bind(
            "Movement", "BunnyhopBoostPerJump", 4.2f,
            "Extra horizontal speed added by each jump in a chain, m/s.");

        ConfigBhopMaxSpeed = Config.Bind(
            "Movement", "BunnyhopMaxSpeed", 110.0f,
            "Horizontal speed cap while bunnyhopping, m/s.");

        ConfigAirAccelerate = Config.Bind(
            "Movement", "AirAccelerate", 150.0f,
            "Air strafe acceleration.");

        ConfigMouseSensitivity = Config.Bind(
            "Camera", "MouseSensitivity", 0.18f,
            "Mouse look sensitivity.");

        ConfigCameraBobIntensity = Config.Bind(
            "Camera", "CameraBobIntensity", 1.0f,
            "Camera bob while moving.");

        ConfigWeaponBobIntensity = Config.Bind(
            "Weapon", "WeaponBobIntensity", 1.0f,
            "Weapon bob while moving.");

        ConfigMusicVolume = Config.Bind(
            "Audio", "MusicVolume", 0.42f,
            "Music volume, 0.0 to 1.0. Scaled by the in-game volume.");

        ConfigSfxVolume = Config.Bind(
            "Audio", "SfxVolume", 0.55f,
            "Effects volume, 0.0 to 1.0. Scaled by the in-game volume.");

        ConfigShowDoomHud = Config.Bind(
            "HUD", "ShowDoomHud", true,
            "Draw the arena HUD while in Doomguy mode.");

        ConfigEnableDitherShader = Config.Bind(
            "Visuals", "EnableDitherShader", true,
            "Apply the retro dithering and scanline overlay.");

        ConfigEnableEnemyFire = Config.Bind(
            "Gameplay", "EnableEnemyTankFire", true,
            "Let enemy tanks aim and fire at you.");

        ConfigEnableEnemyWireframes = Config.Bind(
            "Visuals", "EnableEnemyWireframes", true,
            "Mark enemy tanks with rotating wireframe shapes.");
    }
}

internal sealed class DoomRuntimeBehaviour : MonoBehaviour
{
    private Action? _onUpdate;
    private Action? _onFixedUpdate;
    private Action? _onLateUpdate;
    private Action? _onGui;
    private Action? _onSceneChanged;
    private int _lastSceneHandle = int.MinValue;

    public DoomRuntimeBehaviour(IntPtr ptr) : base(ptr)
    {
    }

    [HideFromIl2Cpp]
    public void Configure(
        Action onUpdate,
        Action onFixedUpdate,
        Action onLateUpdate,
        Action onGui,
        Action onSceneChanged)
    {
        _onUpdate = onUpdate;
        _onFixedUpdate = onFixedUpdate;
        _onLateUpdate = onLateUpdate;
        _onGui = onGui;
        _onSceneChanged = onSceneChanged;
    }

    private void Update()
    {
        try
        {
            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.handle != _lastSceneHandle)
            {
                _lastSceneHandle = activeScene.handle;
                _onSceneChanged?.Invoke();
            }

            _onUpdate?.Invoke();
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog?.LogError(ex);
        }
    }

    private void FixedUpdate()
    {
        try
        {
            _onFixedUpdate?.Invoke();
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog?.LogError(ex);
        }
    }

    private void LateUpdate()
    {
        try
        {
            _onLateUpdate?.Invoke();
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog?.LogError(ex);
        }
    }

    private void OnGUI()
    {
        try
        {
            _onGui?.Invoke();
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog?.LogError(ex);
        }
    }
}
