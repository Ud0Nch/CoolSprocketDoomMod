using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SprocketDoomMod;

// Звук идёт напрямую через winmm waveOut обходжя Unity AudioSource
internal static class DoomAudio
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private const ushort WAVE_FORMAT_PCM = 1;
    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const uint WHDR_BEGINLOOP = 0x00000004;
    private const uint WHDR_ENDLOOP = 0x00000008;
    private const uint InfiniteLoopCount = 0xFFFFFFFF;

    private const int MinSampleRate = 8000;
    private const int MaxSampleRate = 96000;
    private const int DefaultSampleRate = 24000;
    private const int BitsPerSample = 16;
    private const int BytesPerSample = 2;
    private const float MaxGain = 1.5f;

    // смещения полей в заголовке RIFF/WAVE
    private const int WavChannelsOffset = 22;
    private const int WavSampleRateOffset = 24;
    private const int WavBitsOffset = 34;
    private const int WavFirstChunkOffset = 12;
    private const int WavFallbackDataOffset = 44;
    private const uint WavDataChunkId = 0x61746164;

    private const float MusicGainScale = 0.72f;
    private const float MusicVolumeFallback = 0.35f;
    private const float SfxVolumeFallback = 0.42f;
    private const float MusicGainRefreshThreshold = 0.015f;

    private const float VictoryAnthemWeight = 0.46f;
    private const float ShotgunWeight = 0.62f;
    private const float MlgShotWeight = 0.42f;
    private const float HeadshotWeight = 0.50f;
    private const float BigShotWeight = 0.44f;
    private const float AnnouncerWeight = 0.42f;
    private const float BhopWeight = 0.36f;

    private const int BhopPitchMaxSteps = 8;
    private const float BhopPitchStep = 0.03f;

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutOpen(
        out IntPtr phwo,
        uint uDeviceID,
        ref WAVEFORMATEX pwfx,
        IntPtr dwCallback,
        IntPtr dwInstance,
        uint fdwOpen);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern int waveOutClose(IntPtr hwo);

    internal enum AnnouncerClipType
    {
        FirstBlood = 0,
        DoubleKill = 1,
        TripleKill = 2,
        Rampage = 3,
        MonsterKill = 4,
        BigShot = 5
    }

    private sealed class PcmClip
    {
        public short[] Samples = Array.Empty<short>();
        public ushort Channels = 1;
        public uint SampleRate = DefaultSampleRate;
    }

    private sealed class WaveOutChannel
    {
        private IntPtr _handle = IntPtr.Zero;
        private IntPtr _headerPtr = IntPtr.Zero;
        private GCHandle _bufferPin;
        private short[]? _workingSamples;
        private PcmClip? _activeClip;
        private ushort _openChannels;
        private uint _openSampleRate;
        private bool _isPrepared;

        public void Play(PcmClip clip, float gain, bool loop, float pitchMultiplier = 1.0f)
        {
            if (clip.Samples.Length == 0)
                return;

            gain = Mathf.Clamp(gain, 0f, MaxGain);
            var rate = (uint)Mathf.Clamp(Mathf.RoundToInt(clip.SampleRate * pitchMultiplier), MinSampleRate, MaxSampleRate);

            try
            {
                StopBuffer();

                if (_handle == IntPtr.Zero || _openChannels != clip.Channels || _openSampleRate != rate)
                {
                    CloseDevice();

                    var blockAlign = (ushort)(clip.Channels * BytesPerSample);
                    var format = new WAVEFORMATEX
                    {
                        wFormatTag = WAVE_FORMAT_PCM,
                        nChannels = clip.Channels,
                        nSamplesPerSec = rate,
                        nAvgBytesPerSec = rate * blockAlign,
                        nBlockAlign = blockAlign,
                        wBitsPerSample = BitsPerSample,
                        cbSize = 0
                    };

                    var result = waveOutOpen(out _handle, WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0);
                    if (result != 0 || _handle == IntPtr.Zero)
                    {
                        _handle = IntPtr.Zero;
                        return;
                    }

                    _openChannels = clip.Channels;
                    _openSampleRate = rate;
                }

                var length = clip.Samples.Length;
                if (_workingSamples == null || _workingSamples.Length != length)
                {
                    if (_bufferPin.IsAllocated)
                        _bufferPin.Free();

                    _workingSamples = new short[length];
                    _bufferPin = GCHandle.Alloc(_workingSamples, GCHandleType.Pinned);
                }

                _activeClip = clip;
                FillScaledSamples(clip.Samples, _workingSamples, gain);

                if (_headerPtr == IntPtr.Zero)
                    _headerPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());

                var header = new WAVEHDR
                {
                    lpData = _bufferPin.AddrOfPinnedObject(),
                    dwBufferLength = (uint)(length * BytesPerSample),
                    dwBytesRecorded = 0,
                    dwUser = IntPtr.Zero,
                    dwFlags = loop ? WHDR_BEGINLOOP | WHDR_ENDLOOP : 0u,
                    dwLoops = loop ? InfiniteLoopCount : 1u,
                    lpNext = IntPtr.Zero,
                    reserved = IntPtr.Zero
                };

                Marshal.StructureToPtr(header, _headerPtr, false);

                if (waveOutPrepareHeader(_handle, _headerPtr, Marshal.SizeOf<WAVEHDR>()) == 0)
                {
                    _isPrepared = true;
                    waveOutWrite(_handle, _headerPtr, Marshal.SizeOf<WAVEHDR>());
                }
            }
            catch (Exception ex)
            {
                DoomPlugin.ModLog?.LogWarning($"канал waveOut не смог начать воспроизведение: {ex.Message}");
            }
        }

        public void UpdateLiveGain(float gain)
        {
            if (_activeClip == null || _workingSamples == null || !_isPrepared)
                return;
            if (_activeClip.Samples.Length != _workingSamples.Length)
                return;

            FillScaledSamples(_activeClip.Samples, _workingSamples, Mathf.Clamp(gain, 0f, MaxGain));
        }

        private static void FillScaledSamples(short[] source, short[] destination, float gain)
        {
            var scaleFixed = Mathf.RoundToInt(gain * 4096f);
            for (var i = 0; i < source.Length; i++)
            {
                var value = (source[i] * scaleFixed) >> 12;
                destination[i] = (short)Mathf.Clamp(value, short.MinValue, short.MaxValue);
            }
        }

        public void StopBuffer()
        {
            if (_handle == IntPtr.Zero)
                return;

            try
            {
                waveOutReset(_handle);
                if (_isPrepared && _headerPtr != IntPtr.Zero)
                {
                    waveOutUnprepareHeader(_handle, _headerPtr, Marshal.SizeOf<WAVEHDR>());
                    _isPrepared = false;
                }
            }
            catch
            {
                // тут вообще нихуч не будет
            }
        }

        public void CloseDevice()
        {
            StopBuffer();

            if (_handle != IntPtr.Zero)
            {
                try
                {
                    waveOutClose(_handle);
                }
                catch
                {
                    // ксли закрылось
                }
                _handle = IntPtr.Zero;
            }

            if (_headerPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_headerPtr);
                _headerPtr = IntPtr.Zero;
            }

            if (_bufferPin.IsAllocated)
                _bufferPin.Free();

            _workingSamples = null;
            _activeClip = null;
        }
    }

    private static bool _clipsLoaded;
    private static PcmClip? _music;
    private static PcmClip? _shotgun;
    private static PcmClip? _mlgGunShot;
    private static PcmClip? _headshot;
    private static PcmClip? _headshotAlt;
    private static PcmClip? _firstBlood;
    private static PcmClip? _doubleKill;
    private static PcmClip? _tripleKill;
    private static PcmClip? _rampage;
    private static PcmClip? _monsterKill;
    private static PcmClip? _bigShot;
    private static PcmClip? _winLastKill;
    private static PcmClip? _bhopJump;

    private static readonly WaveOutChannel _musicChannel = new();
    private static readonly WaveOutChannel _shotChannel = new();
    private static readonly WaveOutChannel _mlgChannel = new();
    private static readonly WaveOutChannel _headshotChannel = new();
    private static readonly WaveOutChannel _announcerChannel = new();
    private static readonly WaveOutChannel _bhopChannel = new();

    private static bool _musicActive;
    private static bool _isPaused;
    private static bool _headshotToggle;
    private static float _appliedMusicGain = -1f;

    private static void EnsureClipsLoaded()
    {
        if (_clipsLoaded)
            return;

        _clipsLoaded = true;

        _music = LoadWav("Assets/e1m1.wav", "DoomAssets.e1m1.wav");

        _shotgun =
            LoadWav("sounds/doom_shotgun_firing_sound_effect.wav", "DoomAssets.doom_shotgun_firing_sound_effect.wav") ??
            LoadWav("sounds/shotgun_fire.wav", "DoomAssets.shotgun_fire.wav");

        _mlgGunShot = LoadWav("sounds/mlg-gun-shot-sound-effect.wav", "DoomAssets.mlg-gun-shot-sound-effect.wav");

        _headshot = LoadWav("sounds/cs-go-headshot-sound.wav", "DoomAssets.cs-go-headshot-sound.wav");
        _headshotAlt = LoadWav("sounds/cs_go-headshot-sound-effect.wav", "DoomAssets.cs_go-headshot-sound-effect.wav");

        _firstBlood = LoadWav("sounds/firstblood.wav", "DoomAssets.firstblood.wav");
        _doubleKill = LoadWav("sounds/announcer_kill_double_01.wav", "DoomAssets.announcer_kill_double_01.wav");
        _tripleKill = LoadWav("sounds/announcer_kill_triple_01.wav", "DoomAssets.announcer_kill_triple_01.wav");
        _rampage = LoadWav("sounds/announcer_kill_rampage_01.wav", "DoomAssets.announcer_kill_rampage_01.wav");
        _monsterKill = LoadWav("sounds/monsterkill.wav", "DoomAssets.monsterkill.wav");
        _bigShot = LoadWav("sounds/big-shot_spamton.wav", "DoomAssets.big-shot_spamton.wav");
        _winLastKill = LoadWav("sounds/mlg-win-last-kill.wav", "DoomAssets.mlg-win-last-kill.wav");

        _bhopJump = LoadWav("sounds/bhop_jump.wav", "DoomAssets.bhop_jump.wav");

        DoomPlugin.ModLog?.LogDebug(
            $"звуки загружены: музыка={_music != null}, выстрел={_shotgun != null}, анонсер={_rampage != null}");
    }

    private static PcmClip? LoadWav(string relativePath, string embeddedLogicalName)
    {
        var wav = DoomPlugin.LoadAssetBytes(relativePath, embeddedLogicalName);
        if (wav == null || wav.Length < WavFallbackDataOffset)
            return null;

        try
        {
            ReadOnlySpan<byte> span = wav.AsSpan();
            var channels = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(WavChannelsOffset, 2));
            var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(WavSampleRateOffset, 4));
            var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(WavBitsOffset, 2));

            if (channels == 0) channels = 1;
            if (sampleRate == 0) sampleRate = DefaultSampleRate;

            var offset = WavFirstChunkOffset;
            var dataLength = 0;

            while (offset + 8 <= span.Length)
            {
                var chunkId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
                offset += 8;

                if (chunkId == WavDataChunkId)
                {
                    dataLength = Math.Min(chunkSize, span.Length - offset);
                    break;
                }

                if (chunkSize < 0 || offset + chunkSize > span.Length)
                    break;

                offset += chunkSize;
            }

            if (dataLength <= 0)
            {
                offset = WavFallbackDataOffset;
                dataLength = span.Length - WavFallbackDataOffset;
            }

            if (bitsPerSample != BitsPerSample || dataLength < BytesPerSample)
                return null;

            var sampleCount = dataLength / BytesPerSample;
            var samples = new short[sampleCount];
            Buffer.BlockCopy(wav, offset, samples, 0, sampleCount * BytesPerSample);

            return new PcmClip
            {
                Samples = samples,
                Channels = channels,
                SampleRate = sampleRate
            };
        }
        catch (Exception ex)
        {
            DoomPlugin.ModLog?.LogWarning($"не удалось разобрать wav {relativePath}: {ex.Message}");
            return null;
        }
    }

    private static float GetGameMasterVolume()
    {
        try
        {
            var listenerVolume = AudioListener.volume;
            if (listenerVolume > 0.001f && listenerVolume <= 1.0f)
                return listenerVolume;
        }
        catch
        {
            // ⠀⠀⠀⠀⠀⢀⣤⠖⠒⠒⠒⢒⡒⠒⠒⠒⠒⠒⠲⠦⠤⢤⣤⣄⣀⠀⠀⠀⠀⠀
            // ⠀⠀⠀⠀⣠⠟⠀⢀⠠⣐⢭⡐⠂⠬⠭⡁⠐⠒⠀⠀⣀⣒⣒⠐⠈⠙⢦⠀⠀⠀
            // ⠀⠀⠀⣰⠏⠀⠐⠡⠪⠂⣁⣀⣀⣀⡀⠰⠀⠀⠀⢨⠂⠀⠀⠈⢢⠀⠀⢹⠀⠀
            // ⠀⣠⣾⠿⠤⣤⡀⠤⡢⡾⠿⠿⠿⣬⣉⣷⠀⠀⢀⣨⣶⣾⡿⠿⠆⠤⠤⠌⡳⣄
            // ⣰⢫⢁⡾⠋⢹⡙⠓⠦⠤⠴⠛⠀⠀⠈⠁⠀⠀⠀⢹⡀⠀⢠⣄⣤⢶⠲⠍⡎⣾
            // ⢿⠸⠸⡇⠶⢿⡙⠳⢦⣄⣀⠐⠒⠚⣞⢛⣀⡀⠀⠀⢹⣶⢄⡀⠀⣸⡄⠠⣃⣿
            // ⠈⢷⣕⠋⠀⠘⢿⡶⣤⣧⡉⠙⠓⣶⠿⣬⣀⣀⣐⡶⠋⣀⣀⣬⢾⢻⣿⠀⣼⠃
            // ⠀⠀⠙⣦⠀⠀⠈⠳⣄⡟⠛⠿⣶⣯⣤⣀⣀⣏⣉⣙⣏⣉⣸⣧⣼⣾⣿⠀⡇⠀
            // ⠀⠀⠀⠘⢧⡀⠀⠀⠈⠳⣄⡀⣸⠃⠉⠙⢻⠻⠿⢿⡿⢿⡿⢿⢿⣿⡟⠀⣧⠀
            // ⠀⠀⠀⠀⠀⠙⢦⣐⠤⣒⠄⣉⠓⠶⠤⣤⣼⣀⣀⣼⣀⣼⣥⠿⠾⠛⠁⠀⢿⠀
            // ⠀⠀⠀⠀⠀⠀⠀⠈⠙⠦⣭⣐⠉⠴⢂⡤⠀⠐⠀⠒⠒⢀⡀⠀⠄⠁⡠⠀⢸⠀
            // ⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠈⠙⠲⢤⣀⣀⠉⠁⠀⠀⠀⠒⠒⠒⠉⠀⢀⡾⠀
            // ⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠀⠉⠉⠛⠲⠦⠤⠤⠤⠤⠴⠞⠋⠀⠀
        }

        return 1.0f;
    }

    private static float ComputeMusicGain()
    {
        var configured = Mathf.Clamp01(DoomPlugin.ConfigMusicVolume?.Value ?? MusicVolumeFallback);
        return Mathf.Clamp01(configured * GetGameMasterVolume() * MusicGainScale);
    }

    private static float ComputeSfxGain(float weight)
    {
        var configured = Mathf.Clamp01(DoomPlugin.ConfigSfxVolume?.Value ?? SfxVolumeFallback);
        return Mathf.Clamp01(configured * GetGameMasterVolume() * weight);
    }

    internal static void StartBackgroundMusic()
    {
        EnsureClipsLoaded();
        _isPaused = false;
        if (_music == null)
            return;

        _appliedMusicGain = ComputeMusicGain();
        _musicActive = true;
        _musicChannel.Play(_music, _appliedMusicGain, loop: true);
        DoomPlugin.ModLog?.LogDebug("музыка запущена");
    }

    internal static void StopBackgroundMusic(bool keepVictoryAnthem = false)
    {
        _musicActive = false;
        _isPaused = false;

        if (!keepVictoryAnthem)
            _musicChannel.StopBuffer();

        _shotChannel.StopBuffer();
        _mlgChannel.StopBuffer();
        _headshotChannel.StopBuffer();
        _announcerChannel.StopBuffer();
        _bhopChannel.StopBuffer();
    }

    internal static void SetPaused(bool paused)
    {
        if (_isPaused == paused)
            return;

        _isPaused = paused;

        if (paused)
        {
            _musicChannel.StopBuffer();
            _shotChannel.StopBuffer();
            _mlgChannel.StopBuffer();
            _headshotChannel.StopBuffer();
            _announcerChannel.StopBuffer();
            _bhopChannel.StopBuffer();
        }
        else if (_musicActive && _music != null)
        {
            _appliedMusicGain = ComputeMusicGain();
            _musicChannel.Play(_music, _appliedMusicGain, loop: true);
        }
    }

    internal static void PlayVictoryLastKillSound()
    {
        EnsureClipsLoaded();
        if (_winLastKill == null)
            return;

        _musicActive = false;
        _musicChannel.Play(_winLastKill, ComputeSfxGain(VictoryAnthemWeight), loop: false);
    }

    internal static void Update()
    {
        if (!_musicActive || _isPaused)
            return;

        // громкость музыки следует за настройками игры, но пересчёт не чаще порога
        var gain = ComputeMusicGain();
        if (Mathf.Abs(gain - _appliedMusicGain) <= MusicGainRefreshThreshold)
            return;

        //это костыль по сути дела но ладно

        _appliedMusicGain = gain;
        _musicChannel.UpdateLiveGain(gain);
    }

    internal static void AdjustVolumeInGame(float delta)
    {
        if (DoomPlugin.ConfigMusicVolume != null)
            DoomPlugin.ConfigMusicVolume.Value = Mathf.Clamp01(DoomPlugin.ConfigMusicVolume.Value + delta);

        if (DoomPlugin.ConfigSfxVolume != null)
            DoomPlugin.ConfigSfxVolume.Value = Mathf.Clamp01(DoomPlugin.ConfigSfxVolume.Value + delta);

        if (_musicActive && !_isPaused)
        {
            _appliedMusicGain = ComputeMusicGain();
            _musicChannel.UpdateLiveGain(_appliedMusicGain);
        }
    }

    internal static void PlayShotgunBlast()
    {
        if (_isPaused)
            return;

        EnsureClipsLoaded();
        if (_shotgun == null)
            return;

        _shotChannel.Play(_shotgun, ComputeSfxGain(ShotgunWeight), loop: false);
    }

    internal static void PlayMlgGunShotSound()
    {
        if (_isPaused)
            return;

        EnsureClipsLoaded();
        if (_mlgGunShot == null)
            return;

        _mlgChannel.Play(_mlgGunShot, ComputeSfxGain(MlgShotWeight), loop: false);
    }

    internal static void PlayHeadshotSound()
    {
        if (_isPaused)
            return;

        EnsureClipsLoaded();

        // два варианта хэдшота чередуютс
        _headshotToggle = !_headshotToggle;
        var clip = _headshotToggle && _headshotAlt != null
            ? _headshotAlt
            : _headshot ?? _headshotAlt;

        if (clip == null)
            return;

        _headshotChannel.Play(clip, ComputeSfxGain(HeadshotWeight), loop: false);
    }

    internal static void PlayBigShotSound()
    {
        if (_isPaused)
            return;

        EnsureClipsLoaded();
        if (_bigShot == null)
            return;

        _mlgChannel.Play(_bigShot, ComputeSfxGain(BigShotWeight), loop: false);
    }

    internal static void PlayAnnouncerClip(AnnouncerClipType type)
    {
        if (_isPaused)
            return;

        EnsureClipsLoaded();

        var clip = type switch
        {
            AnnouncerClipType.FirstBlood => _firstBlood ?? _rampage,
            AnnouncerClipType.DoubleKill => _doubleKill ?? _rampage,
            AnnouncerClipType.TripleKill => _tripleKill ?? _rampage,
            AnnouncerClipType.Rampage => _rampage ?? _monsterKill,
            AnnouncerClipType.MonsterKill => _monsterKill ?? _rampage,
            AnnouncerClipType.BigShot => _bigShot ?? _rampage,
            _ => _rampage
        };

        if (clip == null)
            return;

        _announcerChannel.Play(clip, ComputeSfxGain(AnnouncerWeight), loop: false);
    }

    internal static void PlayBhopJumpSound(int bhopCount)
    {
        if (_isPaused)
            return;

        EnsureClipsLoaded();
        if (_bhopJump == null)
            return;

        var pitch = 1.0f + Mathf.Clamp(bhopCount - 1, 0, BhopPitchMaxSteps) * BhopPitchStep;
        _bhopChannel.Play(_bhopJump, ComputeSfxGain(BhopWeight), loop: false, pitchMultiplier: pitch);
    }

    internal static void Cleanup()
    {
        _musicActive = false;
        _isPaused = false;

        _musicChannel.CloseDevice();
        _shotChannel.CloseDevice();
        _mlgChannel.CloseDevice();
        _headshotChannel.CloseDevice();
        _announcerChannel.CloseDevice();
        _bhopChannel.CloseDevice();
    }
}
