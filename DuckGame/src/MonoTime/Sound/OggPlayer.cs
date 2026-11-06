using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Media;
using NVorbis;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DuckGame
{
    public class OggPlayer
    {
        private DynamicSoundEffectInstance _instance;
        private byte[] _buffer;
        private float[] _floatBuffer;
        private bool _iSaidStop;
        private float _replaygainModifier = 1f;
        private float _volume = 1f;
        private bool _shouldLoop;
        private bool _valid = true;
        private Thread _decoderThread;
        private bool _killDecodingThread;
        private bool _initialized;
        private VorbisReader _activeSong;
        private VorbisReader _decoderSong;
        private VorbisReader _streamerSong;
        private object _decoderDataMutex = new object();
        private object _decoderMutex = new object();
        private object _streamingMutex = new object();
        private float[] _decodedData;
        private int _samplesDecoded;
        private int _totalSamplesToDecode;
        private int _decodedSamplePosition;

        private Microsoft.Xna.Framework.Media.Song _song; // 64-bit only

        // FNA's MediaPlayer opens a second audio device, which SDL2 on Switch refuses.
#if SWITCH
        private static bool UseMediaPlayer => false;
#else
        private static bool UseMediaPlayer => Environment.Is64BitProcess;
#endif

        private float _pitch;
        private float _pan;

        public SoundState state
        {
            get
            {
                if (UseMediaPlayer)
                    return MediaPlayer.State == MediaState.Playing ? SoundState.Playing
                        : MediaPlayer.State == MediaState.Paused ? SoundState.Paused
                        : SoundState.Stopped;
                return _instance == null || !_valid || _iSaidStop ? SoundState.Stopped : _instance.State;
            }
        }

        public DynamicSoundEffectInstance instance
        {
            get { return _instance; }
            set { _instance = value; }
        }

        public float volume
        {
            get => _volume;
            set
            {
                _volume = value;
                if (UseMediaPlayer)
                {
                    MediaPlayer.Volume = MathHelper.Clamp(_volume, 0f, 1f) * _replaygainModifier;
                    return;
                }
                if (_instance == null)
                    return;
                lock (_instance)
                    ApplyVolume();
            }
        }

        private void ApplyVolume()
        {
            if (!_valid || _instance == null || _instance.State != SoundState.Playing)
                return;
            _instance.Volume = MathHelper.Clamp(_volume, 0f, 1f) * _replaygainModifier;
        }

        private void ApplyPitch()
        {
            if (!_valid || _instance == null || _instance.State != SoundState.Playing)
                return;
            _instance.Pitch = _pitch;
        }

        private void ApplyPan()
        {
            if (!_valid || _instance == null || _instance.State != SoundState.Playing)
                return;
            _instance.Pan = _pan;
        }

        public float pitch
        {
            get => _pitch;
            set
            {
                bool changed = _pitch != value;
                _pitch = value;
                if (UseMediaPlayer)
                    return; // MediaPlayer has no pitch support
                if (_instance == null)
                    return;
                if (changed)
                {
                    lock (_instance)
                        ApplyPitch();
                }
            }
        }

        public float pan
        {
            get => _pan;
            set
            {
                bool changed = _pan != value;
                _pan = value;
                if (UseMediaPlayer)
                    return; // MediaPlayer has no pan support
                if (_instance == null)
                    return;
                if (changed)
                {
                    lock (_instance)
                        ApplyPan();
                }
            }
        }

        public bool looped
        {
            get => _shouldLoop;
            set
            {
                _shouldLoop = value;
                if (UseMediaPlayer)
                    MediaPlayer.IsRepeating = value;
            }
        }

        public TimeSpan position
        {
            get
            {
                if (UseMediaPlayer)
                    return MediaPlayer.PlayPosition;
                return _activeSong != null && _valid && _totalSamplesToDecode > 0 && _decodedSamplePosition < _totalSamplesToDecode
                    ? new TimeSpan(0, 0, 0, 0, (int)(_decodedSamplePosition / _totalSamplesToDecode / 44100.0) * 500)
                    : new TimeSpan();
            }
        }

        public void Terminate()
        {
            if (UseMediaPlayer)
            {
                MediaPlayer.Stop();
                if (_song != null)
                    _song.Dispose();
                return;
            }
            if (_valid)
                _instance.Dispose();
            try
            {
                if (_decoderThread != null)
                    _decoderThread.Abort();
            }
            catch (Exception)
            {
            }
            _killDecodingThread = true;
        }

        public void Initialize()
        {
            if (_initialized)
                return;
            if (UseMediaPlayer)
            {
                _initialized = true;
                return;
            }
            try
            {
                _instance = new DynamicSoundEffectInstance(44100, AudioChannels.Stereo);
                _buffer = new byte[_instance.GetSampleSizeInBytes(TimeSpan.FromMilliseconds(StreamBufferMs))];
                _floatBuffer = new float[_buffer.Length / 2];
#if SWITCH
                for (int index = 0; index < _ring.Length; ++index)
                    _ring[index] = new byte[_buffer.Length];
#endif
                _instance.BufferNeeded += new EventHandler<EventArgs>(Thread_Stream);
                _decoderThread = new Thread(new ThreadStart(Thread_Decoder))
                {
                    CurrentCulture = CultureInfo.InvariantCulture,
                    Priority = ThreadPriority.BelowNormal,
                    IsBackground = true
                };
                _decoderThread.Start();
            }
            catch
            {
                DevConsole.Log(DCSection.General, "Music player failed to initialize.");
                _valid = false;
            }
            _initialized = true;
        }

#if SWITCH
        // Music is decoded ahead on its own thread into a ring of short PCM buffers, and BufferNeeded (fired
        // by FNA on the game thread) only hands over what's ready: it never waits for the decoder, which
        // on this CPU takes far longer than a frame, and no song is ever kept in memory uncompressed.
        private const double StreamBufferMs = 100.0;
        private const int DecoderIdleMs = 20;

        private readonly byte[][] _ring = new byte[8][];
        private readonly int[] _ringLengths = new int[8];
        private volatile int _ringRead, _ringWrite;
        private volatile bool _songEnded;
        public static int buffersPlayed, buffersSilent;

        private void Thread_Decoder_LoadNewSong()
        {
            if (_decoderSong == _activeSong)
                return;
            lock (_decoderMutex)
            {
                if (_decoderSong == _activeSong)
                    return;
                if (_decoderSong != null)
                    _decoderSong.Dispose();
                _decoderSong = _activeSong;
                _streamerSong = _activeSong;
                _decodedSamplePosition = 0;
                _ringRead = _ringWrite;
                _songEnded = false;
                _totalSamplesToDecode = _decoderSong == null ? 0 : (int)(_decoderSong.TotalSamples * 2L);
            }
        }

        // Fills one ring slot; false when there's nothing to do for now.
        private bool Thread_Decoder_DecodeChunk()
        {
            lock (_decoderMutex)
            {
                // SoundEffect sets the song before Initialize() creates the buffers.
                if (_floatBuffer == null || _decoderSong == null || _songEnded || volume == 0.0 || _ringWrite - _ringRead >= _ring.Length)
                    return false;
                int count = _decoderSong.ReadSamples(_floatBuffer, 0, _floatBuffer.Length);
                if (count == 0)
                {
                    if (_shouldLoop && _decodedSamplePosition > 0)
                    {
                        _decoderSong.DecodedPosition = 0;
                        _decodedSamplePosition = 0;
                    }
                    else
                        _songEnded = true;
                    return true;
                }
                _decodedSamplePosition += count;
                int slot = _ringWrite % _ring.Length;
                byte[] pcm = _ring[slot];
                for (int index = 0; index < count; ++index)
                {
                    int sample = (int)(short.MaxValue * _floatBuffer[index]);
                    sample = sample > short.MaxValue ? short.MaxValue : sample < short.MinValue ? short.MinValue : sample;
                    pcm[index * 2] = (byte)sample;
                    pcm[index * 2 + 1] = (byte)(sample >> 8);
                }
                _ringLengths[slot] = count * 2;
                ++_ringWrite;
                return true;
            }
        }

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void switch_pin_worker_thread(int core);

        private const int DecoderCore = 1;

        private void Thread_Decoder()
        {
            try
            {
                switch_pin_worker_thread(DecoderCore);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                // Desktop benchmark build.
            }
            while (!_killDecodingThread)
            {
                Thread_Decoder_LoadNewSong();
                if (!Thread_Decoder_DecodeChunk())
                    Thread.Sleep(DecoderIdleMs);
            }
        }

        private void Thread_Stream(object sender, EventArgs e)
        {
            int read = _ringRead;
            if (_valid && volume != 0.0 && read != _ringWrite)
            {
                int slot = read % _ring.Length;
                _instance.SubmitBuffer(_ring[slot], 0, _ringLengths[slot]);
                _ringRead = read + 1;
                ++buffersPlayed;
                return;
            }
            if (_songEnded && read == _ringWrite)
                Stop();
            ++buffersSilent;
            Array.Clear(_buffer, 0, _buffer.Length);
            _instance.SubmitBuffer(_buffer, 0, _buffer.Length);
        }
#else
        private const double StreamBufferMs = 500.0;

        private void Thread_Decoder_LoadNewSong()
        {
            if (_decoderSong == _activeSong)
                return;
            lock (_decoderDataMutex)
            {
                lock (_decoderMutex)
                {
                    if (_decoderSong == _activeSong)
                        return;
                    if (_decoderSong != null)
                        _decoderSong.Dispose();
                    _decoderSong = _activeSong;
                    _streamerSong = _activeSong;
                    _decodedSamplePosition = 0;
                    _samplesDecoded = 0;
                    if (_decoderSong == null)
                        return;
                    _totalSamplesToDecode = (int)(_decoderSong.TotalSamples * 2L);
                    _decodedData = new float[_totalSamplesToDecode];
                }
            }
        }

        private bool Thread_Decoder_DecodeChunk()
        {
            lock (_decoderMutex)
            {
                if (_decoderSong != null)
                {
                    if (volume != 0.0)
                    {
                        int count = Math.Min(176400, _totalSamplesToDecode - _samplesDecoded);
                        if (count > 0)
                        {
                            _decoderSong.ReadSamples(_decodedData, _samplesDecoded, count);
                            _samplesDecoded += count;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private void Thread_Decoder()
        {
            while (!_killDecodingThread)
            {
                Thread_Decoder_LoadNewSong();
                if (!Thread_Decoder_DecodeChunk())
                    Thread.Sleep(200);
                else
                    Thread.Sleep(20);
            }
        }

        private void Thread_Stream(object sender, EventArgs e)
        {
            lock (_streamingMutex)
            {
                int length = 0;
                lock (_decoderDataMutex)
                {
                    Thread_Decoder_LoadNewSong();
                    if (volume == 0.0 || !_valid || _decoderSong == null)
                    {
                        for (int index = 0; index < _buffer.Length; ++index)
                            _buffer[index] = 0;
                        _instance.SubmitBuffer(_buffer, 0, _buffer.Length);
                        return;
                    }
                    do
                        ;
                    while (_samplesDecoded - _decodedSamplePosition < _floatBuffer.Length && Thread_Decoder_DecodeChunk());
                    length = Math.Min(_totalSamplesToDecode - _decodedSamplePosition, _floatBuffer.Length);
                    if (length > 0)
                    {
                        Array.Copy(_decodedData, _decodedSamplePosition, _floatBuffer, 0, length);
                        _decodedSamplePosition += length;
                    }
                    if (length == 0)
                    {
                        if (_shouldLoop)
                        {
                            _decodedSamplePosition = 0;
                            Array.Copy(_decodedData, _decodedSamplePosition, _floatBuffer, 0, _floatBuffer.Length);
                            _decodedSamplePosition += _floatBuffer.Length;
                            length = _floatBuffer.Length;
                        }
                        else
                        {
                            for (int index = 0; index < _floatBuffer.Length / 2; ++index)
                            {
                                _floatBuffer[index * 2] = 0f;
                                _floatBuffer[index * 2 + 1] = 0f;
                            }
                            length = _floatBuffer.Length;
                            Stop();
                        }
                    }
                }
                if (length <= 0)
                    return;
                for (int index = 0; index < length; ++index)
                {
                    short num = (short)Math.Max(Math.Min(short.MaxValue * _floatBuffer[index], short.MaxValue), short.MinValue);
                    _buffer[index * 2] = (byte)((uint)num & byte.MaxValue);
                    _buffer[index * 2 + 1] = (byte)(num >> 8 & byte.MaxValue);
                }
                _instance.SubmitBuffer(_buffer, 0, length * 2);
            }
        }
#endif

        public void SetOgg(MemoryStream ogg, string path = null)
        {
            if (UseMediaPlayer)
            {
                try
                {
                    MediaPlayer.Stop();
                    if (_song != null)
                        _song.Dispose();
                    float num = 0f;
                    try
                    {
                        byte[] numArray = new byte[1000];
                        ogg.Position = 0L;
                        ogg.Read(numArray, 0, 1000);
                        string str1 = Encoding.ASCII.GetString(numArray);
                        int index1 = str1.IndexOf("replaygain_track_gain");
                        if (index1 >= 0)
                        {
                            while (str1[index1] != '=' && index1 < str1.Length)
                                ++index1;
                            int index2 = index1 + 1;
                            string str2 = "";
                            for (; str1[index2] != 'd' && index2 < str1.Length; ++index2)
                                str2 += str1[index2].ToString();
                            num = Convert.ToSingle(str2);
                        }
                    }
                    catch (Exception)
                    {
                        num = 0f;
                    }
                    _replaygainModifier = Math.Max(0f, Math.Min(1f, (float)((100f * (float)Math.Pow(10.0, num / 20.0)) / 100.0 * 1.9f)));
                    _song = Microsoft.Xna.Framework.Media.Song.FromUri(path, new Uri(path, UriKind.RelativeOrAbsolute));
                    MediaPlayer.IsRepeating = _shouldLoop;
                    MediaPlayer.Volume = MathHelper.Clamp(_volume, 0f, 1f) * _replaygainModifier;
                }
                catch (Exception ex)
                {
                    DevConsole.Log(DCSection.General, "OggPlayer.SetOgg failed with exception:");
                    DevConsole.Log(DCSection.General, ex.Message);
                    _song = null;
                }
                return;
            }

            // 32-bit path, original logic unchanged
            if (!_valid)
                return;
            try
            {
                lock (_streamingMutex)
                {
                    Stop();
                    float num = 0f;
                    try
                    {
                        byte[] numArray = new byte[1000];
                        ogg.Position = 0L;
                        ogg.Read(numArray, 0, 1000);
                        string str1 = Encoding.ASCII.GetString(numArray);
                        int index1 = str1.IndexOf("replaygain_track_gain");
                        if (index1 >= 0)
                        {
                            while (str1[index1] != '=' && index1 < str1.Length)
                                ++index1;
                            int index2 = index1 + 1;
                            string str2 = "";
                            for (; str1[index2] != 'd' && index2 < str1.Length; ++index2)
                                str2 += str1[index2].ToString();
                            num = Convert.ToSingle(str2);
                        }
                    }
                    catch (Exception)
                    {
                        num = 0f;
                    }
                    _activeSong = new VorbisReader(ogg, false);
                    _replaygainModifier = Math.Max(0f, Math.Min(1f, (float)((100f * (float)Math.Pow(10.0, num / 20.0)) / 100.0 * 1.9f)));
                    Thread_Decoder_LoadNewSong();
                    Thread_Decoder_DecodeChunk();
                }
            }
            catch (Exception ex)
            {
                DevConsole.Log(DCSection.General, "OggPlayer.SetOgg failed with exception:");
                DevConsole.Log(DCSection.General, ex.Message);
                _activeSong = null;
            }
        }

        public void Play()
        {
            if (UseMediaPlayer)
            {
                if (_song == null)
                    return;
                MediaPlayer.Play(_song);
                MediaPlayer.Volume = MathHelper.Clamp(_volume, 0f, 1f) * _replaygainModifier;
                _iSaidStop = false;
                return;
            }
            if (_instance == null)
                return;
            lock (_instance)
            {
                if (!_valid)
                    return;
                _instance.Play();
                ApplyVolume();
                _iSaidStop = false;
            }
        }

        public void Pause()
        {
            if (UseMediaPlayer)
            {
                MediaPlayer.Pause();
                return;
            }
            if (_instance == null)
                return;
            lock (_instance)
            {
                if (!_valid)
                    return;
                _instance.Pause();
            }
        }

        public void Resume()
        {
            if (UseMediaPlayer)
            {
                MediaPlayer.Resume();
                MediaPlayer.Volume = MathHelper.Clamp(_volume, 0f, 1f) * _replaygainModifier;
                _iSaidStop = false;
                return;
            }
            if (_instance == null)
                return;
            lock (_instance)
            {
                if (!_valid)
                    return;
                _instance.Resume();
                ApplyVolume();
                _iSaidStop = false;
            }
        }

        public void Stop()
        {
            if (UseMediaPlayer)
            {
                MediaPlayer.Stop();
                _iSaidStop = true;
                return;
            }
            if (_instance == null)
                return;
            lock (_instance)
            {
                if (!_valid)
                    return;
                _instance.Stop();
                _iSaidStop = true;
            }
        }

        public void Update()
        {
        }
    }
}