using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using static SDL2.SDL;

namespace Orbis
{
    internal enum UiSound { Navigate, Confirm, Back, Queued, Installed, Complete = Installed, Error }

    // One producer feeds SDL's native queue; SDL never calls managed Mono code.
    internal static class UiAudio
    {
        const int Rate = 48000, BlockFrames = 512, BlockSamples = BlockFrames * 2;
        const uint TargetBytes = BlockSamples * 2 * 4;
        static readonly object Gate = new object();
        static readonly UiSound[] Pending = new UiSound[16];
        static readonly uint[] LastCue = new uint[6];
        static Thread _thread;
        static volatile bool _stop;
        static bool _music = true, _effects = true, _started;
        static float _musicVolume = .1225f, _effectsVolume = .35f;
        static int _head, _count;
        internal static bool Available { get; private set; }

        internal static void Init(bool musicEnabled = true, bool effectsEnabled = true)
        {
            lock (Gate)
            {
                _music = musicEnabled; _effects = effectsEnabled;
                if (_started) return;
                _started = true; _stop = false;
                try
                {
                    _thread = new Thread(Run) { IsBackground = true, Name = "SSPI UI audio" };
                    _thread.Start();
                }
                catch { _thread = null; _stop = true; }
            }
        }

        internal static void SetEnabled(bool musicEnabled, bool effectsEnabled)
        {
            lock (Gate)
            {
                _music = musicEnabled; _effects = effectsEnabled;
                if (!effectsEnabled) _head = _count = 0;
            }
        }

        internal static void SetVolume(float music, float effects)
        {
            lock (Gate) { _musicVolume = Volume(music); _effectsVolume = Volume(effects); }
        }

        static float Volume(float value) { return float.IsNaN(value) ? 0 : Math.Max(0, Math.Min(1, value)); }
        internal static void Update() { }

        internal static void Play(UiSound cue)
        {
            int index = (int)cue;
            if (index < 0 || index >= LastCue.Length) return;
            uint now = unchecked((uint)Environment.TickCount);
            lock (Gate)
            {
                if (!_started || _stop || !_effects || _effectsVolume <= 0 || !Available) return;
                uint interval = cue == UiSound.Navigate ? 65u : cue == UiSound.Installed ? 750u : cue == UiSound.Error ? 350u : 90u;
                if (LastCue[index] != 0 && unchecked(now - LastCue[index]) < interval) return;
                LastCue[index] = now;
                if (_count == Pending.Length) return;
                Pending[(_head + _count) % Pending.Length] = cue; _count++;
            }
        }

        internal static void Shutdown()
        {
            Thread worker;
            lock (Gate) { _stop = true; _head = _count = 0; worker = _thread; }
            // Native output stalls must never block app teardown indefinitely.
            if (worker != null && worker != Thread.CurrentThread)
            { try { worker.Join(500); } catch (ThreadStateException) { } }
        }

        sealed class Voice { internal short[] Samples; internal int Position; }

        static short[] ReadPcm(string name)
        {
            using (Stream stream = typeof(UiAudio).Assembly.GetManifestResourceStream("Orbis.Audio." + name + ".pcm"))
            {
                if (stream == null || stream.Length < 4 || stream.Length > Rate * 4L * 40 || stream.Length % 4 != 0)
                    throw new InvalidDataException("UI audio asset unavailable");
                byte[] bytes = new byte[(int)stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) throw new EndOfStreamException();
                    read += n;
                }
                short[] pcm = new short[bytes.Length / 2];
                Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
                return pcm;
            }
        }

        static void Run()
        {
            uint device = 0;
            bool initialized = false;
            GCHandle pinned = default(GCHandle);
            try
            {
                if (SDL_InitSubSystem(SDL_INIT_AUDIO) != 0) return;
                initialized = true;
                var desired = new SDL_AudioSpec { freq = Rate, format = AUDIO_S16LSB, channels = 2, samples = 512, callback = null };
                SDL_AudioSpec obtained;
                device = SDL_OpenAudioDevice(null, 0, ref desired, out obtained, 0);
                if (device == 0 || obtained.freq != Rate || obtained.format != AUDIO_S16LSB || obtained.channels != 2) return;
                short[] ambient = ReadPcm("ambient");
                string[] names = { "navigate", "confirm", "back", "queued", "installed", "error" };
                short[][] effects = new short[names.Length][];
                for (int i = 0; i < names.Length; i++) effects[i] = ReadPcm(names[i]);
                short[] output = new short[BlockSamples];
                pinned = GCHandle.Alloc(output, GCHandleType.Pinned);
                IntPtr buffer = pinned.AddrOfPinnedObject();
                var voices = new Voice[8];
                for (int i = 0; i < voices.Length; i++) voices[i] = new Voice();
                int position = 0;
                float musicGain = 0, effectsGain = 0;
                bool playing = false;
                Available = true;
                while (!_stop)
                {
                    uint queued = SDL_GetQueuedAudioSize(device);
                    if (queued >= TargetBytes) { Thread.Sleep(4); continue; }
                    float musicTarget, effectsTarget;
                    lock (Gate)
                    {
                        musicTarget = _music ? _musicVolume : 0;
                        effectsTarget = _effects ? _effectsVolume : 0;
                        while (_count > 0)
                        {
                            UiSound cue = Pending[_head]; _head = (_head + 1) % Pending.Length; _count--;
                            for (int i = 0; i < voices.Length; i++)
                                if (voices[i].Samples == null)
                                { voices[i].Samples = effects[(int)cue]; voices[i].Position = 0; break; }
                        }
                    }
                    // Sample-by-sample ramps make volume changes and toggles click-free.
                    for (int n = 0; n < BlockSamples; n += 2)
                    {
                        musicGain += (musicTarget - musicGain) * .00025f;
                        effectsGain += (effectsTarget - effectsGain) * .002f;
                        float left = ambient[position] * musicGain, right = ambient[position + 1] * musicGain;
                        position += 2; if (position >= ambient.Length) position = 0;
                        for (int i = 0; i < voices.Length; i++)
                        {
                            Voice voice = voices[i];
                            if (voice.Samples == null) continue;
                            left += voice.Samples[voice.Position] * effectsGain;
                            right += voice.Samples[voice.Position + 1] * effectsGain;
                            voice.Position += 2;
                            if (voice.Position >= voice.Samples.Length) voice.Samples = null;
                        }
                        output[n] = (short)Math.Max(-24575, Math.Min(24575, left));
                        output[n + 1] = (short)Math.Max(-24575, Math.Min(24575, right));
                    }
                    if (SDL_QueueAudio(device, buffer, BlockSamples * 2) != 0) break;
                    if (!playing && SDL_GetQueuedAudioSize(device) >= TargetBytes)
                    { SDL_PauseAudioDevice(device, 0); playing = true; }
                }
            }
            catch { /* Audio is optional; missing drivers/exports must not interrupt the UI. */ }
            finally
            {
                Available = false;
                if (device != 0)
                {
                    try { SDL_PauseAudioDevice(device, 1); } catch { }
                    try { SDL_ClearQueuedAudio(device); } catch { }
                    try { SDL_CloseAudioDevice(device); } catch { }
                }
                if (pinned.IsAllocated) pinned.Free();
                if (initialized) { try { SDL_QuitSubSystem(SDL_INIT_AUDIO); } catch { } }
                lock (Gate) { _thread = null; _head = _count = 0; }
            }
        }
    }
}
