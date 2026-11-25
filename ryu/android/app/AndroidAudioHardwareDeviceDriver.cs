using Android.Media;
using Ryujinx.Audio.Backends.Common;
using Ryujinx.Audio.Common;
using Ryujinx.Audio.Integration;
using Ryujinx.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using static Ryujinx.Audio.Integration.IHardwareDeviceDriver;

namespace Ryujinx.Android
{
    /// <summary>Android AudioTrack output backend for the original Ryujinx audio pipeline.</summary>
    internal sealed class AndroidAudioHardwareDeviceDriver : IHardwareDeviceDriver
    {
        private readonly ConcurrentDictionary<AndroidAudioHardwareDeviceSession, byte> _sessions = new();
        private readonly ManualResetEvent _updateRequired = new(false);
        private readonly ManualResetEvent _pause = new(true);
        private float _volume = 1f;

        public static bool IsSupported => true;

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);
                foreach (var session in _sessions.Keys) session.SetVolume(_volume);
            }
        }

        public IHardwareDeviceSession OpenDeviceSession(Direction direction, IVirtualMemoryManager memoryManager,
            SampleFormat sampleFormat, uint sampleRate, uint channelCount)
        {
            if (direction != Direction.Output) throw new ArgumentException(nameof(direction));
            if (sampleFormat != SampleFormat.PcmInt16) throw new ArgumentException(nameof(sampleFormat));
            if (channelCount != 1 && channelCount != 2) throw new ArgumentException(nameof(channelCount));

            var session = new AndroidAudioHardwareDeviceSession(this, memoryManager, sampleRate == 0 ? Constants.TargetSampleRate : sampleRate, channelCount);
            session.SetVolume(_volume);
            _sessions.TryAdd(session, 0);
            return session;
        }

        internal void Remove(AndroidAudioHardwareDeviceSession session) => _sessions.TryRemove(session, out _);
        internal void NotifyQueued() => _updateRequired.Set();
        public ManualResetEvent GetUpdateRequiredEvent() => _updateRequired;
        public ManualResetEvent GetPauseEvent() => _pause;
        public bool SupportsDirection(Direction direction) => direction == Direction.Output;
        public bool SupportsSampleRate(uint sampleRate) => true;
        public bool SupportsSampleFormat(SampleFormat sampleFormat) => sampleFormat == SampleFormat.PcmInt16;
        public bool SupportsChannelCount(uint channelCount) => channelCount == 1 || channelCount == 2;

        public void Dispose()
        {
            foreach (var session in _sessions.Keys) session.Dispose();
            _sessions.Clear();
            _pause.Dispose();
            _updateRequired.Dispose();
        }
    }

    internal sealed class AndroidAudioHardwareDeviceSession : HardwareDeviceSessionOutputBase
    {
        private readonly AndroidAudioHardwareDeviceDriver _owner;
        private readonly AudioTrack _track;
        private readonly object _sync = new();
        private readonly Dictionary<ulong, uint> _bufferEnds = new();
        private ulong _queuedFrames;
        private float _volume = 1f;
        private bool _disposed;

        public AndroidAudioHardwareDeviceSession(AndroidAudioHardwareDeviceDriver owner, IVirtualMemoryManager memoryManager,
            uint sampleRate, uint channels)
            : base(memoryManager, SampleFormat.PcmInt16, sampleRate, channels)
        {
            _owner = owner;
            ChannelOut channelMask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
            int minBytes = AudioTrack.GetMinBufferSize((int)sampleRate, channelMask, Encoding.Pcm16bit);
            if (minBytes <= 0) minBytes = (int)(sampleRate * channels * 2 / 5);
            int bufferBytes = Math.Max(minBytes, (int)(sampleRate * channels * 2 / 5));
            _track = new AudioTrack(Stream.Music, (int)sampleRate, channelMask, Encoding.Pcm16bit, bufferBytes, AudioTrackMode.Stream);
            if (_track.State != AudioTrackState.Initialized)
            {
                _track.Release();
                throw new InvalidOperationException("Android AudioTrack não inicializou.");
            }
        }

        public override bool RegisterBuffer(AudioBuffer buffer, byte[] samples)
        {
            if (samples == null || samples.Length == 0) return false;
            buffer.Data = samples;
            buffer.DataSize = (ulong)samples.Length;
            return true;
        }

        public override void QueueBuffer(AudioBuffer buffer)
        {
            if (buffer.Data == null) RegisterBuffer(buffer);
            if (buffer.Data == null) return;

            int offset = 0;
            while (offset < buffer.Data.Length)
            {
                int written = _track.Write(buffer.Data, offset, buffer.Data.Length - offset, WriteMode.Blocking);
                if (written < 0) throw new InvalidOperationException($"AudioTrack.Write falhou: {written}");
                if (written == 0) break;
                offset += written;
            }

            lock (_sync)
            {
                uint frames = (uint)(offset / (2 * RequestedChannelCount));
                _queuedFrames += frames;
                _bufferEnds[buffer.BufferTag] = unchecked((uint)_queuedFrames);
            }
            _owner.NotifyQueued();
        }

        public override bool WasBufferFullyConsumed(AudioBuffer buffer)
        {
            lock (_sync)
            {
                if (!_bufferEnds.TryGetValue(buffer.BufferTag, out uint end)) return true;
                uint played = unchecked((uint)_track.PlaybackHeadPosition);
                return unchecked(played - end) < 0x80000000u;
            }
        }

        public override ulong GetPlayedSampleCount() => unchecked((uint)_track.PlaybackHeadPosition);

        public override void SetVolume(float volume)
        {
            _volume = Math.Clamp(volume, 0f, 1f);
            _track.SetVolume(_volume);
        }

        public override float GetVolume() => _volume;
        public override void Start() { if (!_disposed && _track.PlayState != PlayState.Playing) _track.Play(); }
        public override void Stop() { if (!_disposed) { _track.Pause(); _track.Flush(); } }
        public override void PrepareToClose() { Stop(); }

        public override void UnregisterBuffer(AudioBuffer buffer)
        {
            lock (_sync) _bufferEnds.Remove(buffer.BufferTag);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Remove(this);
            try { _track.Stop(); } catch { }
            _track.Release();
        }
    }
}
