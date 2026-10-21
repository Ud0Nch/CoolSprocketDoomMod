using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SprocketDoomMod;

internal static class DoomWeaponView
{
    private const int ShootFrameCount = 23;
    private const int TotalFrameCount = 24;
    private const float FrameDurationSeconds = 0.042f;

    private static readonly Texture2D?[] _frameTextures = new Texture2D?[TotalFrameCount];
    private static readonly Sprite?[] _frameSprites = new Sprite?[TotalFrameCount];
    private static readonly Mesh?[] _frameMeshes = new Mesh?[TotalFrameCount];
    private static readonly Material?[] _frameMaterials = new Material?[TotalFrameCount];
    private static readonly GUIStyle?[] _frameGuiStyles = new GUIStyle?[TotalFrameCount];

    private static bool _assetsLoaded;
    private static bool _useGuiOverlayFallback = true;

    private static GameObject? _weaponRoot;
    private static GameObject? _meshObject;
    private static MeshFilter? _meshFilter;
    private static MeshRenderer? _meshRenderer;

    private static Light? _muzzleLight;
    private static float _muzzleFlashTimer;

    private static bool _isPlayingFireAnim;
    private static float _fireAnimTimer;
    private static int _currentFrameIndex;

    private static float _swayX;
    private static float _swayY;
    private static float _prevYaw;
    private static float _prevPitch;
    private static float _lastBobPhase;
    private static float _lastBobAmplitude;

    private static readonly Vector3 BaseLocalPos = new(0.0f, -0.525f, 0.48f);
    private static readonly Vector3 BaseLocalScale = new(0.84f, 0.64f, 1.0f);

    // анимированные спрайты интерфейса типо cool hot
    private static GUIStyle?[] _amongUsStyles = Array.Empty<GUIStyle?>();
    private static GUIStyle?[] _heartStyles = Array.Empty<GUIStyle?>();
    private static GUIStyle?[] _clippyStyles = Array.Empty<GUIStyle?>();

    private sealed class Font3DGlyph
    {
        public int Width;
        public int Height;
        public GUIStyle?[] ColorStyles = Array.Empty<GUIStyle?>();
    }
    private static readonly Dictionary<char, Font3DGlyph> _font3DGlyphs = new();
    private static int _font3DColorCount = 5;

    // панели интерфейса и оверлеи
    private static bool _uiAssetsLoaded;
    private static Texture2D? _ditherOverlayTex;
    private static readonly List<(GUIStyle Style, Texture2D Texture)> _boundStyleTextures = new(512);
    private static GUIStyle? _styleDitherOverlay;
    private static GUIStyle? _styleDamageOverlay;
    private static GUIStyle? _stylePortraitFallback;
    private static GUIStyle? _styleHeartFallback;
    private static GUIStyle? _styleMascotFallback;
    private static GUIStyle? _styleWalletIcon;
    private static GUIStyle? _styleCandyIcon;
    private static GUIStyle? _stylePanelDialogue;
    private static GUIStyle? _stylePanelWallet;
    private static GUIStyle? _stylePanelCandy;
    private static GUIStyle? _stylePanelAbility;
    private static GUIStyle? _stylePanelTopBlasters;
    private static GUIStyle? _stylePanelBoost;
    private static GUIStyle? _stylePanelDark;
    private static GUIStyle? _stylePanelXpBar;

    // стили текста с покачиванием
    private static readonly GUIStyle?[] _neonLabelStyles = new GUIStyle?[5];
    private static GUIStyle? _textDialogue;
    private static GUIStyle? _textSubCyan;
    private static GUIStyle? _textSubOrange;
    private static GUIStyle? _textCrosshair;
    private static GUIStyle? _textAbilityTitle;
    private static GUIStyle? _textAbilityBadge;
    private static GUIStyle? _textAbilityName;
    private static GUIStyle? _textAbilityBar;
    private static GUIStyle? _textTopCell;
    private static GUIStyle? _textKillFeed;
    private static GUIStyle? _textBoostTitle;
    private static GUIStyle? _textBoostBody;
    private static GUIStyle? _textBoostFooter;
    private static GUIStyle? _textBottomUrl;
    private static GUIStyle? _textBottomXpLabel;
    private static GUIStyle? _textBottomXpValue;
    private static GUIStyle? _textBottomTicker;

    internal static bool IsPlayingFireAnimation => _isPlayingFireAnim;

    internal static float GetReloadProgress01()
    {
        if (!_isPlayingFireAnim)
            return 1.0f;
        float totalDur = ShootFrameCount * FrameDurationSeconds;
        return Mathf.Clamp01(_fireAnimTimer / Mathf.Max(0.01f, totalDur));
    }

    internal static bool CanFireNow()
    {
        if (!_isPlayingFireAnim)
            return true;

        return _currentFrameIndex >= 21;
    }

    internal static void ToggleRenderMode()
    {
        _useGuiOverlayFallback = !_useGuiOverlayFallback;
        if (_weaponRoot != null)
        {
            _weaponRoot.SetActive(!_useGuiOverlayFallback);
        }
    }

    internal static void TriggerFireAnimation()
    {
        _isPlayingFireAnim = true;
        _fireAnimTimer = 0f;
        _currentFrameIndex = 1;
        _muzzleFlashTimer = 0.11f;

        ApplyFrame(1);

        if (_muzzleLight != null)
        {
            _muzzleLight.enabled = true;
        }
    }

    internal static void AttachToCamera(Camera doomCamera)
    {
        EnsureAssetsLoaded();
        EnsureUiAssetsLoaded();
        Detach();

        _weaponRoot = new GameObject("Doomguy_SuperShotgun_Root");
        _weaponRoot.transform.SetParent(doomCamera.transform, false);
        _weaponRoot.transform.localPosition = BaseLocalPos;
        _weaponRoot.transform.localRotation = Quaternion.identity;
        _weaponRoot.transform.localScale = BaseLocalScale;

        _meshObject = new GameObject("Doomguy_SuperShotgun_Mesh");
        _meshObject.transform.SetParent(_weaponRoot.transform, false);
        _meshObject.transform.localPosition = Vector3.zero;
        _meshObject.transform.localRotation = Quaternion.identity;
        _meshObject.transform.localScale = Vector3.one;

        _meshFilter = _meshObject.AddComponent<MeshFilter>();
        _meshRenderer = _meshObject.AddComponent<MeshRenderer>();
        _meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _meshRenderer.receiveShadows = false;

        _weaponRoot.SetActive(!_useGuiOverlayFallback);

        try
        {
            var flashObj = new GameObject("Doomguy_MuzzleFlashLight");
            flashObj.transform.SetParent(doomCamera.transform, false);
            flashObj.transform.localPosition = new Vector3(0f, 0.0f, 0.85f);

            _muzzleLight = flashObj.AddComponent<Light>();
            _muzzleLight.type = LightType.Point;
            _muzzleLight.range = 18f;
            _muzzleLight.color = new Color(1.0f, 0.68f, 0.22f, 1.0f);
            _muzzleLight.intensity = 800f;
            _muzzleLight.enabled = false;

            var hdFlash = flashObj.AddComponent<HDAdditionalLightData>();
            if (hdFlash != null)
            {
                hdFlash.lightUnit = LightUnit.Lumen;
                hdFlash.intensity = 4500f;
            }
        }
        catch
        {
        }

        _isPlayingFireAnim = false;
        _fireAnimTimer = 0f;
        _currentFrameIndex = 0;
        ApplyFrame(0);
    }

    internal static void Detach()
    {
        if (_weaponRoot != null)
        {
            UnityEngine.Object.Destroy(_weaponRoot);
            _weaponRoot = null;
            _meshObject = null;
            _meshFilter = null;
            _meshRenderer = null;
        }

        if (_muzzleLight != null)
        {
            UnityEngine.Object.Destroy(_muzzleLight.gameObject);
            _muzzleLight = null;
        }
    }

    internal static void UpdateAnimation(float dt, float bobPhase, float bobAmplitude, float currentYaw, float currentPitch)
    {
        _lastBobPhase = bobPhase;
        _lastBobAmplitude = bobAmplitude;

        if (_muzzleFlashTimer > 0f)
        {
            _muzzleFlashTimer -= dt;
            if (_muzzleFlashTimer <= 0f && _muzzleLight != null)
            {
                _muzzleLight.enabled = false;
            }
        }

        if (_isPlayingFireAnim)
        {
            _fireAnimTimer += dt;
            int frameStep = Mathf.FloorToInt(_fireAnimTimer / FrameDurationSeconds);
            int nextFrame = 1 + frameStep;

            if (nextFrame >= TotalFrameCount)
            {
                _isPlayingFireAnim = false;
                _fireAnimTimer = 0f;
                _currentFrameIndex = 0;
                ApplyFrame(0);
            }
            else if (nextFrame != _currentFrameIndex)
            {
                _currentFrameIndex = nextFrame;
                ApplyFrame(_currentFrameIndex);
            }
        }

        float deltaYaw = Mathf.DeltaAngle(_prevYaw, currentYaw);
        float deltaPitch = Mathf.DeltaAngle(_prevPitch, currentPitch);
        _prevYaw = currentYaw;
        _prevPitch = currentPitch;

        float targetSwayX = Mathf.Clamp(-deltaYaw * 0.0032f, -0.045f, 0.045f);
        float targetSwayY = Mathf.Clamp(deltaPitch * 0.0028f, -0.035f, 0.035f);

        _swayX = Mathf.Lerp(_swayX, targetSwayX, Mathf.Clamp01(dt * 12f));
        _swayY = Mathf.Lerp(_swayY, targetSwayY, Mathf.Clamp01(dt * 12f));

        if (_weaponRoot == null)
            return;

        float weaponBobMult = DoomPlugin.ConfigWeaponBobIntensity.Value;
        float bobOffsetX = Mathf.Cos(bobPhase * 0.5f) * 0.034f * bobAmplitude * weaponBobMult;
        float bobOffsetY = -Mathf.Abs(Mathf.Sin(bobPhase)) * 0.028f * bobAmplitude * weaponBobMult;

        float recoilKickZ = 0f;
        float recoilKickY = 0f;
        if (_isPlayingFireAnim && _currentFrameIndex <= 4)
        {
            float t = 1f - ((_currentFrameIndex - 1) / 4f);
            recoilKickZ = -0.055f * t;
            recoilKickY = -0.028f * t;
        }

        _weaponRoot.transform.localPosition = BaseLocalPos + new Vector3(
            bobOffsetX + _swayX,
            bobOffsetY + Mathf.Min(0f, _swayY) + recoilKickY,
            recoilKickZ);

        float rollSway = Mathf.Clamp(-deltaYaw * 0.45f, -4.5f, 4.5f);
        _weaponRoot.transform.localRotation = Quaternion.Euler(0f, 0f, rollSway);
    }

    internal static void DrawOnGUI()
    {
        // на паузе HUD прячется, чтобы не перекрывать меню игры
        if (DoomController.IsPaused)
            return;

        EnsureAssetsLoaded();
        EnsureUiAssetsLoaded();
        VerifyStyleBackgrounds();

        int screenWidth = Screen.width;
        int screenHeight = Screen.height;
        float t = Time.unscaledTime;

        if (DoomPlugin.ConfigEnableDitherShader.Value && _styleDitherOverlay != null)
        {
            float ditherShift = (Mathf.FloorToInt(t * 10f) & 1) * 1.0f;
            GUI.Box(new Rect(-ditherShift, -ditherShift, screenWidth + 2f, screenHeight + 2f), GUIContent.none, _styleDitherOverlay);
        }

        if (DoomController.LastDamageFlashTimer > 0f && _styleDamageOverlay != null)
        {
            GUI.Box(new Rect(0f, 0f, screenWidth, screenHeight), GUIContent.none, _styleDamageOverlay);
        }

        if (_useGuiOverlayFallback)
        {
            GUIStyle? frameStyle = _frameGuiStyles[_currentFrameIndex];
            if (frameStyle != null)
            {
                float aspect = 516f / 390f;
                float h = screenHeight * 0.74f;
                float w = h * aspect;

                float weaponBobMult = DoomPlugin.ConfigWeaponBobIntensity.Value;
                float bobX = Mathf.Cos(_lastBobPhase * 0.5f) * 26f * _lastBobAmplitude * weaponBobMult;
                float bobY = Mathf.Abs(Mathf.Sin(_lastBobPhase)) * 22f * _lastBobAmplitude * weaponBobMult;

                float recoilDown = 0f;
                if (_isPlayingFireAnim && _currentFrameIndex <= 4)
                {
                    recoilDown = (5 - _currentFrameIndex) * 5.5f;
                }

                float swayDown = Mathf.Max(0f, -_swayY * 220f);

                float x = (screenWidth - w) * 0.5f + bobX + (_swayX * 360f);
                float y = screenHeight - h - 20f + bobY + recoilDown + swayDown;

                GUI.Box(new Rect(x, y, w, h), GUIContent.none, frameStyle);
            }
        }

        if (!DoomPlugin.ConfigShowDoomHud.Value)
            return;

        EnsureTextStylesInitialized();

        float speedRatio = Mathf.Clamp01(DoomController.CurrentHorizontalSpeed / 35f);
        float dmgShake = DoomController.LastDamageFlashTimer > 0f ? 5.5f : 0f;
        float uiWobbleAmp = 1.4f + speedRatio * 2.8f + dmgShake;
        float uiX = Mathf.Sin(t * 6.5f) * uiWobbleAmp;
        float uiY = Mathf.Cos(t * 8.2f) * uiWobbleAmp;

        GUIStyle? portraitStyle = _stylePortraitFallback;
        if (_amongUsStyles.Length > 0)
        {
            float animFps = DoomController.TalkingAnimationTimer > 0f ? 15.0f : 3.5f;
            int aIdx = Mathf.Abs(Mathf.FloorToInt(t * animFps)) % _amongUsStyles.Length;
            if (_amongUsStyles[aIdx] != null)
                portraitStyle = _amongUsStyles[aIdx];
        }

        if (portraitStyle != null)
        {
            GUI.Box(new Rect(26f + uiX, 32f + uiY, 185f, 212f), GUIContent.none, portraitStyle);
        }

        DrawWobbly3DText(38f + uiX, 200f + uiY, "SUSSY_OS", 0.44f, t, colorOffset: 0, wobbleAmp: 2.0f);

        if (_stylePanelDialogue != null)
        {
            GUI.Box(new Rect(220f - uiX * 0.6f, 88f + uiY * 0.8f, 282f, 96f), GUIContent.none, _stylePanelDialogue);
        }

        GUIStyle dlgStyle = (DoomController.TalkingAnimationTimer > 0f && _neonLabelStyles[0] != null)
            ? _neonLabelStyles[Mathf.Abs(Mathf.FloorToInt(t * 7f)) % _neonLabelStyles.Length]!
            : _textDialogue!;
        GUI.Label(new Rect(234f - uiX * 0.6f, 102f + uiY * 0.8f, 256f, 72f), DoomController.CurrentDialogueText, dlgStyle);

        // шкала 
        int scrapValue = 4200 + (DoomController.ShotsFired * 200) + (DoomController.TotalKills * 2500);
        if (_stylePanelWallet != null)
        {
            GUI.Box(new Rect(26f + uiX * 0.5f, 254f - uiY * 0.5f, 218f, 44f), GUIContent.none, _stylePanelWallet);
        }
        if (_styleWalletIcon != null)
        {
            GUI.Box(new Rect(36f + uiX * 0.5f, 258f - uiY * 0.5f, 36f, 36f), GUIContent.none, _styleWalletIcon);
        }
        DrawWobbly3DText(80f + uiX * 0.5f, 254f - uiY * 0.5f, $"{scrapValue}+", 0.34f, t + 0.5f, colorOffset: 2, wobbleAmp: 1.4f);
        GUI.Label(new Rect(80f + uiX * 0.5f, 280f - uiY * 0.5f, 155f, 16f), "scrap.bin // CAL=200MM", _textSubCyan!);

        int coresValue = DoomController.TotalKills;
        if (_stylePanelCandy != null)
        {
            GUI.Box(new Rect(26f - uiX * 0.5f, 306f + uiY * 0.5f, 218f, 44f), GUIContent.none, _stylePanelCandy);
        }
        if (_styleCandyIcon != null)
        {
            GUI.Box(new Rect(36f - uiX * 0.5f, 314f + uiY * 0.5f, 36f, 26f), GUIContent.none, _styleCandyIcon);
        }
        DrawWobbly3DText(80f - uiX * 0.5f, 306f + uiY * 0.5f, $"{coresValue} VENTED", 0.34f, t + 1.2f, colorOffset: 4, wobbleAmp: 1.4f);
        GUI.Label(new Rect(80f - uiX * 0.5f, 332f + uiY * 0.5f, 155f, 16f), "cores.bin // SIPHON=+35HP", _textSubOrange!);

        float tbW = 550f;
        float tbH = 72f;
        float tbX = (screenWidth - tbW) * 0.5f + uiX * 0.4f;
        float tbY = 18f + uiY * 0.4f;

        if (_stylePanelTopBlasters != null)
        {
            GUI.Box(new Rect(tbX, tbY, tbW, tbH), GUIContent.none, _stylePanelTopBlasters);
        }

        DrawWobbly3DTextCentered(tbX + tbW * 0.5f, tbY - 1f, "ARMOR BOUNTY // RADAR.SYS", 0.34f, t, colorOffset: 1, wobbleAmp: 1.3f);

        var scores = DoomController.BlasterScores;
        float cellW = (tbW - 16f) / 8f;
        for (int i = 0; i < scores.Count && i < 8; i++)
        {
            var entry = scores[i];
            float cxCell = tbX + 8f + i * cellW;
            float cellBob = Mathf.Sin(t * 6f + i * 0.9f) * 1.5f;
            string statusTag = entry.IsAlly
                ? (entry.Alive ? "ALLY" : "ALLY:OUT")
                : (entry.Alive ? $"{entry.Score}P" : "OUT");
            string cellText = $"#{entry.Rank}\n{entry.Name}\n{statusTag}";
            GUI.Label(new Rect(cxCell, tbY + 23f + cellBob, cellW, 44f), cellText, _textTopCell!);
        }

        float cx = screenWidth * 0.5f;
        float cy = screenHeight * 0.5f;

        string crosshairChar = DoomController.LastHeadshotFlashTimer > 0f ? "X" : "+";
        GUI.Label(new Rect(cx - 20f, cy - 14f, 40f, 28f), crosshairChar, _textCrosshair!);

        string ammoStr = _isPlayingFireAnim ? "0/2" : "2/2";
        DrawWobbly3DTextCentered(cx, cy + 44f, ammoStr, 0.31f, t, colorOffset: 0, wobbleAmp: 1.4f);

        int speedUnitsPerSec = Mathf.RoundToInt(DoomController.CurrentHorizontalSpeed * 32.0f);
        DrawWobbly3DTextCentered(cx, cy + 64f, $"{speedUnitsPerSec} U/S", 0.27f, t + 0.7f, colorOffset: 2, wobbleAmp: 1.4f);

        if (DoomController.LastKillBannerTimer > 0f && !string.IsNullOrEmpty(DoomController.LastKillBannerText))
        {
            DrawWobbly3DTextCentered(cx, tbY + 78f, DoomController.LastKillBannerText, 0.40f, t * 1.4f, colorOffset: 1, wobbleAmp: 2.5f);
        }
        else if (DoomController.LastHeadshotFlashTimer > 0f)
        {
            DrawWobbly3DTextCentered(cx, tbY + 78f, "ARMOR CRACKED! +10 HP", 0.38f, t * 1.5f, colorOffset: 3, wobbleAmp: 2.2f);
        }
        else if (DoomController.IncomingShellWarningTimer > 0f)
        {
            DrawWobbly3DTextCentered(cx, tbY + 78f, "! ENEMY TURRET LOCKED ON !", 0.34f, t * 1.6f, colorOffset: 4, wobbleAmp: 2.2f);
        }
        else if (DoomController.NearestObjectiveDistance > 0f)
        {
            string objBanner = $"OBJECTIVE ZONE [{DoomController.NearestObjectiveName}] // {DoomController.NearestObjectiveDistance:0}M (OR VENT ALL {DoomController.AliveEnemyCount} HOSTILES)";
            DrawWobbly3DTextCentered(cx, tbY + 78f, objBanner, 0.28f, t, colorOffset: 3, wobbleAmp: 1.4f);
        }
        else if (DoomController.TotalEnemyCount > 0)
        {
            string objBanner = $"MISSION: VENT ALL HOSTILES ({DoomController.AliveEnemyCount}/{DoomController.TotalEnemyCount} ALIVE) // ALLIES: {DoomController.AliveAllyCount}";
            DrawWobbly3DTextCentered(cx, tbY + 78f, objBanner, 0.28f, t, colorOffset: 2, wobbleAmp: 1.3f);
        }

        // левый нижний угол: здоровье и сердешко
        GUIStyle? heartStyle = _styleHeartFallback;
        if (_heartStyles.Length > 0)
        {
            float hpFactor = DoomController.PlayerHealth < 45 ? 2.2f : 1.0f;
            float beatFps = (12.0f + speedRatio * 10.0f) * hpFactor;
            int hIdx = Mathf.Abs(Mathf.FloorToInt(t * beatFps)) % _heartStyles.Length;
            if (_heartStyles[hIdx] != null)
                heartStyle = _heartStyles[hIdx];
        }

        float heartX = 168f + uiX * 0.9f;
        float heartY = screenHeight - 236f + uiY * 0.9f;

        if (heartStyle != null)
        {
            GUI.Box(new Rect(heartX, heartY, 162f, 162f), GUIContent.none, heartStyle);
        }

        int hpColorOffset = DoomController.PlayerHealth <= 35 ? 4 : 0;
        DrawWobbly3DTextCentered(heartX + 82f, heartY + 52f, "HEALTH", 0.54f, t, colorOffset: hpColorOffset, wobbleAmp: 3.0f);
        DrawWobbly3DTextCentered(heartX + 84f, heartY + 90f, $"{DoomController.PlayerHealth}", 0.84f, t + 0.4f, colorOffset: hpColorOffset + 1, wobbleAmp: 3.6f);

        // правый нижний угол
        float abW = 242f;
        float abH = 120f;
        float abX = screenWidth - 564f - uiX * 0.7f;
        float abY = screenHeight - 254f + uiY * 0.7f;

        if (_stylePanelAbility != null)
        {
            GUI.Box(new Rect(abX, abY, abW, abH), GUIContent.none, _stylePanelAbility);
        }
        GUI.Label(new Rect(abX + 8f, abY + 4f, 175f, 18f), "DOOM.EXE // ORDNANCE", _textAbilityTitle!);
        GUI.Label(new Rect(abX + 203f, abY + 4f, 30f, 18f), "B", _textAbilityBadge!);

        GUIStyle? clippyStyle = _styleMascotFallback;
        if (_clippyStyles.Length > 0)
        {
            int cIdx = Mathf.Abs(Mathf.FloorToInt(t * 11.0f)) % _clippyStyles.Length;
            if (_clippyStyles[cIdx] != null)
                clippyStyle = _clippyStyles[cIdx];
        }

        if (clippyStyle != null)
        {
            float clippyBob = Mathf.Sin(t * 7.5f) * 2.5f;
            GUI.Box(new Rect(abX + 10f, abY + 32f + clippyBob, 82f, 68f), GUIContent.none, clippyStyle);
        }

        string abilityName = DoomController.BuckshotMode ? "200mm Buckshot" : "200mm APHE Slug";
        string readyStatus = _isPlayingFireAnim ? "RELOAD" : "READY!";
        float progress = GetReloadProgress01();
        int filledBars = Mathf.Clamp(Mathf.RoundToInt(progress * 12f), 1, 12);
        string barString = new string('I', filledBars).PadRight(12, '.');

        GUI.Label(new Rect(abX + 96f, abY + 30f, 138f, 22f), abilityName, _textAbilityName!);
        DrawWobbly3DText(abX + 96f, abY + 52f, readyStatus, 0.44f, t, colorOffset: _isPlayingFireAnim ? 4 : 3, wobbleAmp: 1.8f);
        GUI.Label(new Rect(abX + 96f, abY + 86f, 138f, 20f), $"[{barString}]", _textAbilityBar!);

        var feed = DoomController.KillFeed;
        float kfW = 304f;
        float kfH = 24f;
        float kfX = screenWidth - kfW - 14f + uiX * 0.4f;
        float kfBaseY = screenHeight - 246f;

        for (int i = 0; i < feed.Count && i < 3; i++)
        {
            var item = feed[i];
            float rowY = kfBaseY + i * (kfH + 4f) + Mathf.Sin(t * 7f + i) * 1.2f;
            if (_stylePanelDark != null)
            {
                GUI.Box(new Rect(kfX, rowY, kfW, kfH), GUIContent.none, _stylePanelDark);
            }
            string line = $"{item.Killer} {item.Action} [{item.Weapon}] {item.Victim}";
            GUI.Label(new Rect(kfX + 6f, rowY + 2f, kfW - 12f, kfH - 4f), line, _textKillFeed!);
        }

        float bcW = 112f;
        float bcH = 108f;
        float bcY = screenHeight - 156f - uiY * 0.5f;
        float bc1X = screenWidth - 244f + uiX * 0.5f;
        float bc2X = screenWidth - 124f - uiX * 0.5f;

        int bhopMult = Mathf.Max(1, DoomController.BhopChainCount);

        if (_stylePanelBoost != null)
        {
            GUI.Box(new Rect(bc1X, bcY, bcW, bcH), GUIContent.none, _stylePanelBoost);
            GUI.Box(new Rect(bc2X, bcY, bcW, bcH), GUIContent.none, _stylePanelBoost);
        }

        GUI.Label(new Rect(bc1X + 6f, bcY + 3f, bcW - 12f, 16f), "SHELL.SYS", _textBoostTitle!);
        GUI.Label(new Rect(bc1X + 6f, bcY + 26f, bcW - 12f, 54f), "2000 m/s //\nSIPHON //\n+10 HP HIT\n+35 HP KILL", _textBoostBody!);
        GUI.Label(new Rect(bc1X + 6f, bcY + 87f, bcW - 12f, 18f), $"{DoomPlugin.ConfigCaliberMm.Value:0}MM", _textBoostFooter!);

        GUI.Label(new Rect(bc2X + 6f, bcY + 3f, bcW - 12f, 16f), "BHOP.SYS", _textBoostTitle!);
        GUI.Label(new Rect(bc2X + 6f, bcY + 26f, bcW - 12f, 54f), $"strafe.sh //\nx{bhopMult} CHAIN //\nDODGE\nTANKS", _textBoostBody!);
        GUI.Label(new Rect(bc2X + 6f, bcY + 87f, bcW - 12f, 18f), $"{speedUnitsPerSec} u/s", _textBoostFooter!);

        if (_stylePanelDark != null)
        {
            GUI.Box(new Rect(0f, screenHeight - 38f, screenWidth, 38f), GUIContent.none, _stylePanelDark);
        }

        int volPct = Mathf.RoundToInt(DoomPlugin.ConfigMusicVolume.Value * 100f);
        GUI.Label(
            new Rect(14f, screenHeight - 35f, 540f, 16f),
            $"sprocket://sussy_slayer/v200mm.sh  ::  [F10] DITHER  ::  [-/=] VOL {volPct}%",
            _textBottomUrl!);

        float xpBarW = 320f;
        float xpBarX = (screenWidth - xpBarW) * 0.5f;
        GUI.Label(new Rect(xpBarX - 135f, screenHeight - 35f, 130f, 16f), "siphon.log // 200MM", _textBottomXpLabel!);

        if (_stylePanelXpBar != null)
        {
            GUI.Box(new Rect(xpBarX, screenHeight - 36f, xpBarW, 18f), GUIContent.none, _stylePanelXpBar);
        }
        int expVal = 420 + (DoomController.TotalHits * 25) + (DoomController.TotalKills * 100);
        GUI.Label(new Rect(xpBarX, screenHeight - 36f, xpBarW, 18f), $"{expVal} / 999 OVERDRIVE", _textBottomXpValue!);

        GUI.Label(
            new Rect(0f, screenHeight - 17f, screenWidth, 16f),
            "RIP AND TEAR // 200MM SUPER SHOTGUN OVERDRIVE // ENEMY TURRETS TRACKING // SUSPICIOUS VELOCITY // NO ARMOR IS SAFE",
            _textBottomTicker!);
    }

    private static void DrawWobbly3DText(
        float startX,
        float startY,
        string text,
        float scale,
        float time,
        int colorOffset = 0,
        float wobbleAmp = 2.5f)
    {
        if (string.IsNullOrEmpty(text) || _font3DGlyphs.Count == 0)
            return;

        float curX = startX;
        int colorCycleBase = Mathf.Abs(Mathf.FloorToInt(time * 6.5f)) + colorOffset;

        for (int i = 0; i < text.Length; i++)
        {
            char c = char.ToUpperInvariant(text[i]);
            if (c == ' ')
            {
                curX += 18f * scale;
                continue;
            }

            if (!_font3DGlyphs.TryGetValue(c, out Font3DGlyph? glyph) || glyph == null)
            {
                curX += 14f * scale;
                continue;
            }

            int colIdx = (i + colorCycleBase) % _font3DColorCount;
            GUIStyle? style = glyph.ColorStyles[colIdx];
            float w = glyph.Width * scale;
            float h = glyph.Height * scale;

            if (style != null)
            {
                float dx = Mathf.Sin(time * 9.5f + i * 0.95f) * (wobbleAmp * 0.55f);
                float dy = Mathf.Cos(time * 12.0f + i * 1.15f) * wobbleAmp;
                GUI.Box(new Rect(curX + dx, startY + dy, w, h), GUIContent.none, style);
            }

            curX += (w - 5f * scale);
        }
    }

    private static void DrawWobbly3DTextCentered(
        float centerX,
        float startY,
        string text,
        float scale,
        float time,
        int colorOffset = 0,
        float wobbleAmp = 2.5f)
    {
        if (string.IsNullOrEmpty(text) || _font3DGlyphs.Count == 0)
            return;

        float totalW = MeasureWobbly3DTextWidth(text, scale);
        DrawWobbly3DText(centerX - totalW * 0.5f, startY, text, scale, time, colorOffset, wobbleAmp);
    }

    private static float MeasureWobbly3DTextWidth(string text, float scale)
    {
        float totalW = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            char c = char.ToUpperInvariant(text[i]);
            if (c == ' ')
            {
                totalW += 18f * scale;
            }
            else if (_font3DGlyphs.TryGetValue(c, out Font3DGlyph? glyph) && glyph != null)
            {
                totalW += (glyph.Width - 5f) * scale;
            }
            else
            {
                totalW += 14f * scale;
            }
        }
        return totalW;
    }

    private static void ApplyFrame(int index)
    {
        if (index < 0 || index >= TotalFrameCount)
            index = 0;

        Mesh? mesh = _frameMeshes[index];
        Material? mat = _frameMaterials[index];

        if (_meshFilter != null && mesh != null)
        {
            _meshFilter.sharedMesh = mesh;
        }

        if (_meshRenderer != null && mat != null)
        {
            _meshRenderer.sharedMaterial = mat;
        }
    }

    private static Material CreateHdrpUnlitMaterial(Texture2D tex, string name)
    {
        Shader? shader =
            Shader.Find("HDRP/Unlit") ??
            Shader.Find("Unlit/Texture") ??
            Shader.Find("HDRP/Lit") ??
            Shader.Find("Sprites/Default");

        var mat = new Material(shader)
        {
            name = name,
            hideFlags = HideFlags.HideAndDontSave
        };

        mat.mainTexture = tex;
        if (mat.HasProperty("_UnlitColorMap"))
            mat.SetTexture("_UnlitColorMap", tex);
        if (mat.HasProperty("_BaseColorMap"))
            mat.SetTexture("_BaseColorMap", tex);
        if (mat.HasProperty("_MainTex"))
            mat.SetTexture("_MainTex", tex);
        if (mat.HasProperty("_UnlitColor"))
            mat.SetColor("_UnlitColor", Color.white);
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", Color.white);

        try
        {
            HDMaterial.ValidateMaterial(mat);
        }
        catch
        {
        }

        return mat;
    }

    private static void MarkPersistent(UnityEngine.Object? obj)
    {
        if (obj == null)
            return;
        try
        {
            obj.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            UnityEngine.Object.DontDestroyOnLoad(obj);
        }
        catch
        {
        }
    }

    private static void VerifyStyleBackgrounds()
    {
        if (_frameTextures[0] == null)
        {
            _assetsLoaded = false;
            EnsureAssetsLoaded();
        }

        if (_ditherOverlayTex == null || _styleDitherOverlay == null)
        {
            _uiAssetsLoaded = false;
            _font3DGlyphs.Clear();
            _boundStyleTextures.Clear();
            EnsureUiAssetsLoaded();
        }

        for (int i = 0; i < TotalFrameCount; i++)
        {
            GUIStyle? frameStyle = _frameGuiStyles[i];
            Texture2D? tx = _frameTextures[i];
            if (frameStyle != null && tx != null && frameStyle.normal.background == null)
            {
                frameStyle.normal.background = tx;
            }
        }

        for (int i = 0; i < _boundStyleTextures.Count; i++)
        {
            var entry = _boundStyleTextures[i];
            if (entry.Style != null && entry.Texture != null && entry.Style.normal.background == null)
            {
                entry.Style.normal.background = entry.Texture;
            }
        }
    }

    private static GUIStyle? CreateGuiStyleFromPngBytes(byte[]? pngBytes, out Texture2D? createdTex)
    {
        createdTex = null;
        if (pngBytes == null || pngBytes.Length == 0)
            return null;

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        ImageConversion.LoadImage(tex, pngBytes, false);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        MarkPersistent(tex);
        createdTex = tex;

        GUIStyle style = new GUIStyle
        {
            stretchWidth = true,
            stretchHeight = true
        };
        style.normal.background = tex;
        _boundStyleTextures.Add((style, tex));
        return style;
    }

    private static GUIStyle? CreateGuiStyleFromPngBytes(byte[]? pngBytes)
    {
        return CreateGuiStyleFromPngBytes(pngBytes, out _);
    }

    private static GUIStyle? LoadUiBoxStyle(string fileName, string embeddedName, out Texture2D? createdTex)
    {
        byte[]? pngBytes = DoomPlugin.LoadAssetBytes($"Assets/{fileName}", embeddedName);
        return CreateGuiStyleFromPngBytes(pngBytes, out createdTex);
    }

    private static GUIStyle? LoadUiBoxStyle(string fileName, string embeddedName)
    {
        return LoadUiBoxStyle(fileName, embeddedName, out _);
    }

    private static GUIStyle?[] LoadAnimatedPngBundle(string fileName, string embeddedName)
    {
        byte[]? bundle = DoomPlugin.LoadAssetBytes($"Assets/{fileName}", embeddedName);
        if (bundle == null || bundle.Length < 8)
            return Array.Empty<GUIStyle?>();

        try
        {
            ReadOnlySpan<byte> span = bundle.AsSpan();
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0, 4));
            if (count <= 0 || count > 64)
                return Array.Empty<GUIStyle?>();

            var styles = new GUIStyle?[count];
            int offset = 4;
            for (int i = 0; i < count && offset + 4 <= span.Length; i++)
            {
                int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                offset += 4;
                if (len <= 0 || offset + len > span.Length)
                    break;

                byte[] pngBytes = span.Slice(offset, len).ToArray();
                offset += len;
                styles[i] = CreateGuiStyleFromPngBytes(pngBytes);
            }
            return styles;
        }
        catch
        {
            return Array.Empty<GUIStyle?>();
        }
    }

    private static void LoadFont3DAtlas()
    {
        if (_font3DGlyphs.Count > 0)
            return;

        byte[]? data = DoomPlugin.LoadAssetBytes("Assets/font3d_atlas.bin", "DoomAssets.font3d_atlas.bin");
        if (data == null || data.Length < 8)
            return;

        try
        {
            ReadOnlySpan<byte> span = data.AsSpan();
            int numChars = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(0, 2));
            int numCols = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(2, 2));
            _font3DColorCount = Mathf.Max(1, numCols);

            int offset = 4;
            for (int i = 0; i < numChars && offset + 5 <= span.Length; i++)
            {
                char ch = (char)span[offset];
                int w = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 1, 2));
                int h = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 3, 2));
                offset += 5;

                var glyph = new Font3DGlyph
                {
                    Width = w,
                    Height = h,
                    ColorStyles = new GUIStyle?[numCols]
                };

                for (int c = 0; c < numCols && offset + 4 <= span.Length; c++)
                {
                    int pngLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                    offset += 4;
                    if (pngLen <= 0 || offset + pngLen > span.Length)
                        break;

                    byte[] pngBytes = span.Slice(offset, pngLen).ToArray();
                    offset += pngLen;
                    glyph.ColorStyles[c] = CreateGuiStyleFromPngBytes(pngBytes);
                }

                _font3DGlyphs[ch] = glyph;
            }

        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogWarning(ex.Message);
        }
    }

    private static void EnsureUiAssetsLoaded()
    {
        if (_uiAssetsLoaded)
            return;

        _uiAssetsLoaded = true;

        try
        {
            _amongUsStyles = LoadAnimatedPngBundle("amongus_anim.bin", "DoomAssets.amongus_anim.bin");
            _heartStyles = LoadAnimatedPngBundle("heart_anim.bin", "DoomAssets.heart_anim.bin");
            _clippyStyles = LoadAnimatedPngBundle("clippy_anim.bin", "DoomAssets.clippy_anim.bin");
            LoadFont3DAtlas();

            _styleDitherOverlay = LoadUiBoxStyle("ui_dither_overlay.png", "DoomAssets.ui_dither_overlay.png", out _ditherOverlayTex);
            _styleDamageOverlay = LoadUiBoxStyle("ui_damage_overlay.png", "DoomAssets.ui_damage_overlay.png");
            _stylePortraitFallback = LoadUiBoxStyle("ui_portrait.png", "DoomAssets.ui_portrait.png");
            _styleHeartFallback = LoadUiBoxStyle("ui_heart.png", "DoomAssets.ui_heart.png");
            _styleMascotFallback = LoadUiBoxStyle("ui_mascot.png", "DoomAssets.ui_mascot.png");
            _styleWalletIcon = LoadUiBoxStyle("ui_wallet.png", "DoomAssets.ui_wallet.png");
            _styleCandyIcon = LoadUiBoxStyle("ui_candy.png", "DoomAssets.ui_candy.png");

            _stylePanelDialogue = LoadUiBoxStyle("ui_panel_dialogue.png", "DoomAssets.ui_panel_dialogue.png");
            _stylePanelWallet = LoadUiBoxStyle("ui_panel_wallet.png", "DoomAssets.ui_panel_wallet.png");
            _stylePanelCandy = LoadUiBoxStyle("ui_panel_candy.png", "DoomAssets.ui_panel_candy.png");
            _stylePanelAbility = LoadUiBoxStyle("ui_panel_ability.png", "DoomAssets.ui_panel_ability.png");
            _stylePanelTopBlasters = LoadUiBoxStyle("ui_panel_topblasters.png", "DoomAssets.ui_panel_topblasters.png");
            _stylePanelBoost = LoadUiBoxStyle("ui_panel_boost.png", "DoomAssets.ui_panel_boost.png");
            _stylePanelDark = LoadUiBoxStyle("ui_panel_dark.png", "DoomAssets.ui_panel_dark.png");
            _stylePanelXpBar = LoadUiBoxStyle("ui_panel_xpbar.png", "DoomAssets.ui_panel_xpbar.png");
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogWarning($"Интефрейс ошибка: {ex.Message}");
        }
    }

    private static void EnsureTextStylesInitialized()
    {
        if (_textDialogue != null)
            return;

        _textDialogue = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap = true
        };
        _textDialogue.normal.textColor = new Color(0.95f, 0.98f, 0.96f, 1.0f);

        Color[] neonTextCols =
        {
            new Color(1.0f, 0.96f, 0.30f, 1.0f),
            new Color(0.35f, 1.0f, 0.92f, 1.0f),
            new Color(1.0f, 0.45f, 0.88f, 1.0f),
            new Color(0.50f, 1.0f, 0.38f, 1.0f),
            new Color(1.0f, 0.62f, 0.28f, 1.0f)
        };
        for (int i = 0; i < neonTextCols.Length; i++)
        {
            var labelStyle = new GUIStyle(_textDialogue);
            labelStyle.normal.textColor = neonTextCols[i];
            _neonLabelStyles[i] = labelStyle;
        }

        _textSubCyan = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        _textSubCyan.normal.textColor = new Color(0.15f, 0.92f, 0.85f, 1.0f);

        _textSubOrange = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        _textSubOrange.normal.textColor = new Color(0.95f, 0.58f, 0.18f, 1.0f);

        _textCrosshair = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _textCrosshair.normal.textColor = Color.white;

        _textAbilityTitle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        _textAbilityTitle.normal.textColor = Color.white;

        _textAbilityBadge = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _textAbilityBadge.normal.textColor = new Color(0.10f, 0.20f, 0.55f, 1.0f);

        _textAbilityName = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        _textAbilityName.normal.textColor = new Color(0.08f, 0.08f, 0.08f, 1.0f);

        _textAbilityBar = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        _textAbilityBar.normal.textColor = new Color(0.15f, 0.55f, 0.15f, 1.0f);

        _textTopCell = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperCenter
        };
        _textTopCell.normal.textColor = new Color(0.92f, 0.95f, 0.98f, 1.0f);

        _textKillFeed = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleRight,
            wordWrap = false,
            clipping = TextClipping.Clip
        };
        _textKillFeed.normal.textColor = new Color(0.90f, 0.95f, 0.92f, 1.0f);

        _textBoostTitle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        _textBoostTitle.normal.textColor = Color.white;

        _textBoostBody = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft
        };
        _textBoostBody.normal.textColor = new Color(0.25f, 0.95f, 0.35f, 1.0f);

        _textBoostFooter = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _textBoostFooter.normal.textColor = new Color(0.10f, 0.12f, 0.15f, 1.0f);

        _textBottomUrl = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        _textBottomUrl.normal.textColor = new Color(0.55f, 0.72f, 0.75f, 1.0f);

        _textBottomXpLabel = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleRight
        };
        _textBottomXpLabel.normal.textColor = new Color(0.25f, 0.90f, 0.95f, 1.0f);

        _textBottomXpValue = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _textBottomXpValue.normal.textColor = Color.white;

        _textBottomTicker = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        _textBottomTicker.normal.textColor = new Color(0.35f, 0.95f, 0.35f, 1.0f);
    }

    private static void EnsureAssetsLoaded()
    {
        if (_assetsLoaded)
            return;

        _assetsLoaded = true;

        try
        {
            List<DoomAssetBuilder.SpanEntry[]>? allSpans = null;
            int spanW = 516;
            int spanH = 390;

            byte[]? spansBytes = DoomPlugin.LoadAssetBytes("frames/spans.bin", "DoomAssets.spans.bin");
            if (spansBytes != null && spansBytes.Length > 10)
            {
                allSpans = DoomAssetBuilder.ReadSpansBin(spansBytes, out spanW, out spanH);
            }

            for (int i = 0; i < TotalFrameCount; i++)
            {
                string fileName = (i == 0) ? "frames/idle.png" : $"frames/shoot_{i - 1:D2}.png";
                string resourceName = (i == 0) ? "DoomAssets.idle.png" : $"DoomAssets.shoot_{i - 1:D2}.png";

                byte[]? pngBytes = DoomPlugin.LoadAssetBytes(fileName, resourceName);
                if (pngBytes == null && i == 0)
                {
                    pngBytes = DoomPlugin.LoadAssetBytes("doom_shoty.png", "DoomAssets.doom_shoty.png");
                }

                if (pngBytes == null || pngBytes.Length == 0)
                {
                    break;
                }

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                ImageConversion.LoadImage(tex, pngBytes, false);
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                MarkPersistent(tex);

                _frameTextures[i] = tex;

                Sprite spr = Sprite.Create(
                    tex,
                    new Rect(0f, 0f, tex.width, tex.height),
                    new Vector2(0.5f, 0.0f),
                    100f);
                MarkPersistent(spr);
                _frameSprites[i] = spr;

                GUIStyle gStyle = new GUIStyle
                {
                    stretchWidth = true,
                    stretchHeight = true
                };
                gStyle.normal.background = tex;
                _frameGuiStyles[i] = gStyle;

                var spans = (allSpans != null && i < allSpans.Count)
                    ? allSpans[i]
                    : DoomAssetBuilder.BuildDefaultFallbackSpans(spanW, spanH);

                _frameMeshes[i] = DoomAssetBuilder.BuildCutoutMesh(spans, spanW, spanH, $"DoomCutoutMesh_{i:00}");
                MarkPersistent(_frameMeshes[i]);
                _frameMaterials[i] = CreateHdrpUnlitMaterial(tex, $"DoomShotgunMat_{i:00}");
                MarkPersistent(_frameMaterials[i]);
            }

        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog.LogError(ex);
        }
    }
}
