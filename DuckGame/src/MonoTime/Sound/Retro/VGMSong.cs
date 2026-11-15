using Microsoft.Xna.Framework.Audio;
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;

namespace DuckGame
{
    public class VGMSong
    {
        public DynamicSoundEffectInstance _instance;
        private byte[] _buffer;
        private int[] _intBuffer;
        private YM2612 _chip = new YM2612();
        private SN76489 _psg = new SN76489();
        private bool _iSaidStop;
        private float _volume = 1f;
        private bool _looped = true;
        private float _playbackSpeed = 1f;
        private const uint FCC_VGM = 544040790;
        private uint _VGMDataLen;
        private VGM_HEADER _VGMHead;
        private BinaryReader _vgmReader;
        private byte[] _DACData;
        private byte[] _VGMData;
        private int _DACOffset;
        private int _VGMDataOffset;
        private byte _lastCommand;
        private int _wait;
        private float _waitInc;

        public SoundState state => !_iSaidStop ? _instance.State : SoundState.Stopped;

        public float volume
        {
            get => _volume;
            set
            {
                _volume = MathHelper.Clamp(value, 0f, 1f);
                if (_instance == null || _instance.State != SoundState.Playing)
                    return;
                _instance.Volume = _volume;
            }
        }

        public bool looped
        {
            get => _looped;
            set => _looped = value;
        }

        public bool gameFroze { get; set; }

        public float playbackSpeed
        {
            get => _playbackSpeed;
            set => _playbackSpeed = value;
        }

        public VGMSong(string file)
        {
            _instance = new DynamicSoundEffectInstance(44100, AudioChannels.Stereo);
            _buffer = new byte[_instance.GetSampleSizeInBytes(TimeSpan.FromMilliseconds(150.0))];
            _intBuffer = new int[_buffer.Length / 2];
            _instance.BufferNeeded += new EventHandler<EventArgs>(StreamVGM);
            OpenVGMFile(file);
            _chip.Initialize((int)_VGMHead.lngHzYM2612, 44100);
            _psg.Initialize(_VGMHead.lngHzPSG);
        }

        public void Terminate()
        {
#if SWITCH
            WaitForWorker();
#endif
            _instance.Dispose();
        }

        public void Play()
        {
            _instance.Stop();
            _instance.Play();
            _instance.Volume = _volume;
            _iSaidStop = false;
        }

        public void Pause()
        {
            _instance.Pause();
            _iSaidStop = true;
        }

        public void Resume()
        {
            _instance.Resume();
            _instance.Volume = _volume;
            _iSaidStop = false;
        }

        public void Stop()
        {
#if SWITCH
            // The buffer being made was requested before the stop; the original submitted it and then
            // flushed it with the stop, so it is dropped.
            WaitForWorker();
            _ready = false;
#endif
            _instance.Stop();
            _instance.Volume = 0f;
            _iSaidStop = true;
            _vgmReader.BaseStream.Seek(0L, SeekOrigin.Begin);
        }

        private static VGM_HEADER ReadVGMHeader(BinaryReader hFile)
        {
            VGM_HEADER vgmHeader = new VGM_HEADER();
            foreach (FieldInfo field in typeof(VGM_HEADER).GetFields())
            {
                if (field.FieldType == typeof(uint))
                {
                    uint num = hFile.ReadUInt32();
                    field.SetValue(vgmHeader, num);
                }
                else if (field.FieldType == typeof(ushort))
                {
                    ushort num = hFile.ReadUInt16();
                    field.SetValue(vgmHeader, num);
                }
                else if (field.FieldType == typeof(char))
                {
                    char ch = hFile.ReadChar();
                    field.SetValue(vgmHeader, ch);
                }
                else if (field.FieldType == typeof(byte))
                {
                    byte num = hFile.ReadByte();
                    field.SetValue(vgmHeader, num);
                }
            }
            if (vgmHeader.lngVersion < 257U)
                vgmHeader.lngRate = 0U;
            if (vgmHeader.lngVersion < 272U)
            {
                vgmHeader.shtPSG_Feedback = 0;
                vgmHeader.bytPSG_SRWidth = 0;
                vgmHeader.lngHzYM2612 = vgmHeader.lngHzYM2413;
                vgmHeader.lngHzYM2151 = vgmHeader.lngHzYM2413;
            }
            if (vgmHeader.lngHzPSG != 0U)
            {
                if (vgmHeader.shtPSG_Feedback == 0)
                    vgmHeader.shtPSG_Feedback = 9;
                if (vgmHeader.bytPSG_SRWidth == 0)
                    vgmHeader.bytPSG_SRWidth = 16;
            }
            return vgmHeader;
        }
        //stuff -Lucky
        bool OpenVGMFile(string fileName)
        {
            bool zipped = fileName.Contains(".vgz");

            //Read size
            uint FileSize = 0;
            FileStream vgmFile = File.Open(fileName, FileMode.Open);
            if (zipped)
            {
                vgmFile.Position = vgmFile.Length - 4;
                byte[] b = new byte[4];
                vgmFile.Read(b, 0, 4);
                uint fileSize = BitConverter.ToUInt32(b, 0);
                FileSize = fileSize;
                vgmFile.Position = 0;

                GZipStream stream = new GZipStream(vgmFile, CompressionMode.Decompress);
                _vgmReader = new BinaryReader(stream);
            }
            else
            {
                FileSize = (uint)vgmFile.Length;
                _vgmReader = new BinaryReader(vgmFile);
            }

            uint fccHeader;
            fccHeader = (uint)_vgmReader.ReadUInt32();
            if (fccHeader != FCC_VGM)
                return false;

            _VGMDataLen = FileSize;
            _VGMHead = ReadVGMHeader(_vgmReader);

            if (zipped)
            {
                _vgmReader.Close();
                vgmFile = File.Open(fileName, FileMode.Open);
                GZipStream stream = new GZipStream(vgmFile, CompressionMode.Decompress);
                _vgmReader = new BinaryReader(stream);
            }
            else
                _vgmReader.BaseStream.Seek(0, SeekOrigin.Begin);

            //Figure out header offset
            int offset = (int)_VGMHead.lngDataOffset;
            if (offset == 0 || offset == 0x0000000C)
                offset = 0x40;
            _VGMDataOffset = offset;

            _vgmReader.ReadBytes(offset);
            _VGMData = _vgmReader.ReadBytes((int)(FileSize - offset));
            _vgmReader = new BinaryReader(new MemoryStream(_VGMData));

            if ((byte)_vgmReader.PeekChar() == 0x67)
            {
                _vgmReader.ReadByte();
                if ((byte)_vgmReader.PeekChar() == 0x66)
                {
                    _vgmReader.ReadByte();
                    byte type = _vgmReader.ReadByte();
                    uint size = _vgmReader.ReadUInt32();
                    _DACData = _vgmReader.ReadBytes((int)size);
                }
            }

            vgmFile.Close();
            return true;
        }

        private void StreamVGM(object sender, EventArgs e)
        {
            if (_iSaidStop)
                return;
#if SWITCH
            if (StreamFromWorker())
                return;
#endif
            if (StopAtEnd())
                return;
            Submit(Synthesize(_looped));
        }

#if SWITCH
        // Synthesizing a Genesis FM song costs a large part of a frame per 150 ms buffer on the console.
        // A request for a buffer starts its synthesis on a worker thread; the buffer is submitted at the
        // next request, a frame later. The song state still advances when the buffer is requested, as
        // before, so the music comes out sample for sample the same. Only the looping flag can change
        // in the meantime, and the worker gets its value from the moment of the request.
        private static readonly object WorkerLock = new object();
        private static Thread _worker;
        private static VGMSong _job;
        private static bool _jobLooped;
        private bool _busy, _ready;
        private int _readyHalf;

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void switch_pin_worker_thread(int core);

        private const int WorkerCore = 1;

        private static void WorkerLoop()
        {
            try
            {
                switch_pin_worker_thread(WorkerCore);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                // Desktop benchmark build.
            }
            while (true)
            {
                VGMSong song;
                bool looped;
                lock (WorkerLock)
                {
                    while (_job == null)
                        Monitor.Wait(WorkerLock);
                    song = _job;
                    looped = _jobLooped;
                    _job = null;
                }
                int half = song.Synthesize(looped);
                lock (WorkerLock)
                {
                    song._readyHalf = half;
                    song._ready = true;
                    song._busy = false;
                    Monitor.PulseAll(WorkerLock);
                }
            }
        }

        private void WaitForWorker()
        {
            lock (WorkerLock)
            {
                while (_busy)
                    Monitor.Wait(WorkerLock);
            }
        }

        // Returns false when the buffer has to be made here, as before: at the start of the song, after
        // it stopped, or when so little is queued that waiting a frame could run the voice dry.
        private bool StreamFromWorker()
        {
            if (PerfTest.NoMusicWorker)
                return false;
            bool low = _instance.PendingBufferCount <= 1;
            lock (WorkerLock)
            {
                if (_busy)
                {
                    if (!low)
                        return true;
                    while (_busy)
                        Monitor.Wait(WorkerLock);
                }
                if (_ready)
                {
                    _ready = false;
                    Submit(_readyHalf);
                    return true;
                }
            }
            if (low)
                return false;
            if (StopAtEnd())
                return true;
            lock (WorkerLock)
            {
                if (_worker == null)
                {
                    _worker = new Thread(WorkerLoop)
                    {
                        Priority = ThreadPriority.BelowNormal,
                        IsBackground = true
                    };
                    _worker.Start();
                }
                _busy = true;
                _job = this;
                _jobLooped = _looped;
                Monitor.PulseAll(WorkerLock);
            }
            return true;
        }
#endif

        private bool StopAtEnd()
        {
            if (_lastCommand != 102 || _looped)
                return false;
            _lastCommand = 0;
            _instance.Volume = 0f;
            _iSaidStop = true;
            Stop();
            return true;
        }

        internal byte[] SynthesizedBuffer => _buffer;

        private void Submit(int num12)
        {
#if SWITCH
            PerfTest.MusicSubmitted(_buffer, num12 * 2);
#endif
            _instance.SubmitBuffer(_buffer, 0, num12);
            _instance.SubmitBuffer(_buffer, num12, num12);
        }

        // Fills _buffer with the next 150 ms of the song and returns the size of each of its two halves.
        internal int Synthesize(bool looped)
        {
            int[] buffer = new int[2];
            int num1 = 0;
            int num2 = _intBuffer.Length / 2;
            bool flag1 = false;
            while (num1 != num2)
            {
                bool flag2;
                if (_wait == 0 && !gameFroze)
                {
                    flag2 = false;
                    byte num3 = _vgmReader.ReadByte();
                    _lastCommand = num3;
                    switch (num3)
                    {
                        case 79:
                            int num4 = _vgmReader.ReadByte();
                            break;
                        case 80:
                            _psg.Write(_vgmReader.ReadByte());
                            break;
                        case 82:
                            _chip.WritePort0(_vgmReader.ReadByte(), _vgmReader.ReadByte());
                            break;
                        case 83:
                            _chip.WritePort1(_vgmReader.ReadByte(), _vgmReader.ReadByte());
                            break;
                        case 97:
                            _wait = _vgmReader.ReadUInt16();
                            if (_wait != 0)
                            {
                                flag2 = true;
                                break;
                            }
                            break;
                        case 98:
                            _wait = 735;
                            flag2 = true;
                            break;
                        case 99:
                            _wait = 882;
                            flag2 = true;
                            break;
                        case 102:
                            if (!looped)
                            {
                                _vgmReader.BaseStream.Seek(0L, SeekOrigin.Begin);
                                flag1 = true;
                                break;
                            }
                            if (_VGMHead.lngLoopOffset != 0U)
                            {
                                _vgmReader.BaseStream.Seek(_VGMHead.lngLoopOffset - _VGMDataOffset, SeekOrigin.Begin);
                                break;
                            }
                            _vgmReader.BaseStream.Seek(0L, SeekOrigin.Begin);
                            break;
                        case 103:
                            int num5 = _vgmReader.ReadByte();
                            int num6 = _vgmReader.ReadByte();
                            _vgmReader.BaseStream.Position += _vgmReader.ReadUInt32();
                            break;
                        case 224:
                            _DACOffset = (int)_vgmReader.ReadUInt32();
                            break;
                    }
                    if (num3 >= 112 && num3 <= 127)
                    {
                        _wait = (num3 & 15) + 1;
                        if (_wait != 0)
                            flag2 = true;
                    }
                    else if (num3 >= 128 && num3 <= 143)
                    {
                        _wait = num3 & 15;

                        //DAC DATA IS NULL
                        _chip.WritePort0(42, _DACData[_DACOffset]);
                        ++_DACOffset;
                        if (_wait != 0)
                            flag2 = true;
                    }
                    if (_wait != 0)
                        --_wait;
                }
                else
                {
                    flag2 = true;
                    if (_wait > 0)
                    {
                        for (_waitInc += _playbackSpeed; _wait > 0 && _waitInc >= 1.0; --_wait)
                            --_waitInc;
                    }
                }
                if (!flag1)
                {
                    if (flag2)
                    {
                        _chip.Update(buffer, 1);
                        short num7 = (short)buffer[0];
                        short num8 = (short)buffer[1];
                        _psg.Update(buffer, 1);
                        short num9 = (short)buffer[0];
                        short num10 = (short)buffer[1];
                        _intBuffer[num1 * 2] = Maths.Clamp((num7 + num9) * 2, short.MinValue, short.MaxValue);
                        _intBuffer[num1 * 2 + 1] = Maths.Clamp((num8 + num10) * 2, short.MinValue, short.MaxValue);
                        ++num1;
                        if (num1 == num2)
                            break;
                    }
                }
                else
                    break;
            }
            for (int index = 0; index < _intBuffer.Length; ++index)
            {
                short num11 = (short)_intBuffer[index];
                _buffer[index * 2] = (byte)((uint)num11 & byte.MaxValue);
                _buffer[index * 2 + 1] = (byte)(num11 >> 8 & byte.MaxValue);
            }
            int num12 = num1 * 2;
            if (num12 / 4.0 - (int)(num12 / 4.0) > 0.0)
                num12 -= 2;
            return num12;
        }
    }
}
