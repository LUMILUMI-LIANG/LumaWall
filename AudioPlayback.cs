using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace LumaWall;

// The reader and output device belong to this dedicated MTA thread. Only the
// shared clock is authoritative; EOF never restarts or slows that clock.
sealed class AudioPlayback : IDisposable
{
    const long TicksPerSecond = 10000000;
    readonly PlaybackClock clock;
    readonly long requestedDuration;
    readonly CancellationTokenSource stop = new();
    readonly AutoResetEvent changed = new(false);
    readonly Task worker;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int enabled, hasAudio, disposed, outputPaused;
    float volume = 1f;
    Exception? error;
    long playedFrames, submittedFrames, positionTicks, queuedTicks, maximumQueuedTicks, loops, resyncs;

    public AudioPlayback(string path, PlaybackClock clock, long durationTicks)
    {
        this.clock = clock;
        requestedDuration = Math.Max(0, durationTicks);
        worker = Task.Factory.StartNew(() => Run(path), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public Task Ready => ready.Task;
    public bool HasAudio => Volatile.Read(ref hasAudio) != 0;
    public Exception? Error => Volatile.Read(ref error);
    public event Action<Exception>? Failed;
    public bool Enabled
    {
        get => Volatile.Read(ref enabled) != 0;
        set
        {
            if (Volatile.Read(ref disposed) != 0) return;
            Volatile.Write(ref enabled, value ? 1 : 0);
            changed.Set();
        }
    }
    public float Volume
    {
        get => Volatile.Read(ref volume);
        set => Volatile.Write(ref volume, float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 1f);
    }
    internal long DevicePlayedFrames => Interlocked.Read(ref playedFrames);
    internal long SubmittedFrames => Interlocked.Read(ref submittedFrames);
    internal long CurrentPositionTicks => Interlocked.Read(ref positionTicks);
    internal long QueuedTicks => Interlocked.Read(ref queuedTicks);
    internal long MaximumQueuedTicks => Interlocked.Read(ref maximumQueuedTicks);
    internal long LoopCount => Interlocked.Read(ref loops);
    internal long ResyncCount => Interlocked.Read(ref resyncs);
    internal bool OutputPaused => Volatile.Read(ref outputPaused) != 0;

    void Wait(int milliseconds = 5)
    {
        if (WaitHandle.WaitAny([stop.Token.WaitHandle, changed], milliseconds) == 0)
            stop.Token.ThrowIfCancellationRequested();
    }

    void Run(string path)
    {
        WaveOutput? output = null;
        try
        {
            using var decoder = new AudioDecoder(path);
            Volatile.Write(ref hasAudio, decoder.HasAudio ? 1 : 0);
            if (!decoder.HasAudio) { ready.TrySetResult(); return; }
            // Decode once even in a silent preview so a damaged sound track is
            // reported instead of being mistaken for a video without audio.
            _ = decoder.Read(stop.Token) ?? throw new InvalidDataException("音轨没有可解码的声音样本。");
            long duration = requestedDuration > 0 ? requestedDuration : decoder.DurationTicks;
            ready.TrySetResult();
            long cycle = -1, epochTicks = 0, cursorFrame = 0, pastPlayed = 0;
            AudioChunk? pending = null;
            bool seek = true, eof = false, devicePaused = true;
            const int blockFrames = AudioDecoder.SampleRate / 50; // 20 ms
            const int queueFrames = AudioDecoder.SampleRate / 10; // 100 ms
            byte[] silence = new byte[blockFrames * AudioDecoder.BlockAlign];

            void Reset()
            {
                if (output is not null)
                {
                    pastPlayed += output.PositionFrames;
                    output.Reset();
                    Interlocked.Exchange(ref playedFrames, pastPlayed);
                }
                pending = null; eof = false; seek = true; devicePaused = true;
                Volatile.Write(ref outputPaused, 1);
                Interlocked.Exchange(ref queuedTicks, 0);
            }

            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                output?.Reap();
                if (!Enabled || !clock.IsStarted)
                {
                    if (output is not null && !seek) Reset();
                    Wait(10); continue;
                }
                bool paused = clock.IsPaused;
                if (paused)
                {
                    if (output is not null && !devicePaused) { output.Pause(); devicePaused = true; }
                    Volatile.Write(ref outputPaused, 1);
                    if (output is not null) Interlocked.Exchange(ref playedFrames, pastPlayed + output.PositionFrames);
                    Wait(); continue;
                }
                output ??= new WaveOutput(AudioDecoder.SampleRate, AudioDecoder.Channels);
                long now = clock.PositionTicks;
                long nextCycle = duration > 0 ? now / duration : 0;
                if (nextCycle != cycle)
                {
                    if (cycle >= 0) { Reset(); Interlocked.Increment(ref loops); }
                    cycle = nextCycle;
                    seek = true;
                }
                if (!seek)
                {
                    long deviceFrames = output.PositionFrames;
                    long actual = epochTicks + deviceFrames * TicksPerSecond / AudioDecoder.SampleRate;
                    Interlocked.Exchange(ref playedFrames, pastPlayed + deviceFrames);
                    Interlocked.Exchange(ref positionTicks, actual);
                    // Recover from decoder stalls or a clock restart rather
                    // than playing an ever-growing queue of old sound.
                    if (Math.Abs(now - actual) > 1000000) { Reset(); Interlocked.Increment(ref resyncs); }
                }
                if (seek)
                {
                    output.Pause(); devicePaused = true;
                    long relative = duration > 0 ? now % duration : now;
                    decoder.Seek(relative);
                    // Seeking can take time. Trim the samples to the current
                    // clock below, not the position from before the seek.
                    now = clock.PositionTicks;
                    if (duration > 0 && now / duration != cycle) continue;
                    relative = duration > 0 ? now % duration : now;
                    cursorFrame = relative * AudioDecoder.SampleRate / TicksPerSecond;
                    epochTicks = cycle * duration + cursorFrame * TicksPerSecond / AudioDecoder.SampleRate;
                    pending = null; eof = false; seek = false;
                    Interlocked.Exchange(ref positionTicks, epochTicks);
                }
                long endFrame = duration > 0 ? duration * AudioDecoder.SampleRate / TicksPerSecond : long.MaxValue;
                while (output.QueuedFrames < queueFrames && cursorFrame < endFrame && Enabled && !clock.IsPaused)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (pending is null && !eof)
                    {
                        pending = decoder.Read(stop.Token);
                        if (pending is null)
                        {
                            eof = true;
                            if (duration <= 0)
                            {
                                duration = Math.Max(1, decoder.LastEndTicks);
                                endFrame = duration * AudioDecoder.SampleRate / TicksPerSecond;
                            }
                        }
                    }
                    int count = (int)Math.Min(blockFrames, endFrame - cursorFrame);
                    if (count <= 0) break;
                    byte[] data = silence;
                    int sourceByte = 0;
                    if (pending is not null)
                    {
                        long startFrame = (long)Math.Round(pending.Timestamp * (double)AudioDecoder.SampleRate / TicksPerSecond);
                        long offset = cursorFrame - startFrame;
                        if (offset >= pending.Frames) { pending = null; continue; }
                        if (offset < 0) count = (int)Math.Min(count, -offset); // Preserve actual track gaps.
                        else
                        {
                            count = (int)Math.Min(count, pending.Frames - offset);
                            data = pending.Data;
                            sourceByte = checked((int)offset * AudioDecoder.BlockAlign);
                        }
                    }
                    output.Write(data, sourceByte, count, Volume);
                    cursorFrame += count;
                    Interlocked.Add(ref submittedFrames, count);
                    long queued = output.QueuedFrames * TicksPerSecond / AudioDecoder.SampleRate;
                    Interlocked.Exchange(ref queuedTicks, queued);
                    if (queued > Interlocked.Read(ref maximumQueuedTicks)) Interlocked.Exchange(ref maximumQueuedTicks, queued);
                }
                if (devicePaused && !clock.IsPaused && Enabled && output.QueuedFrames > 0)
                {
                    output.Restart(); devicePaused = false;
                    Volatile.Write(ref outputPaused, 0);
                }
                long remaining = Math.Max(0, output.SubmittedFrames - output.PositionFrames);
                Interlocked.Exchange(ref queuedTicks, remaining * TicksPerSecond / AudioDecoder.SampleRate);
                Wait();
            }
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(stop.Token); }
        catch (Exception ex)
        {
            var failure = new InvalidOperationException("原声播放失败：" + ex.Message, ex);
            Volatile.Write(ref error, failure);
            ready.TrySetException(failure);
            try { Failed?.Invoke(failure); } catch { }
        }
        finally
        {
            try { output?.Dispose(); }
            catch (Exception ex)
            {
                if (Error is null)
                {
                    var failure = new InvalidOperationException("声音设备关闭失败：" + ex.Message, ex);
                    Volatile.Write(ref error, failure);
                    try { Failed?.Invoke(failure); } catch { }
                }
            }
            Interlocked.Exchange(ref queuedTicks, 0);
            Volatile.Write(ref outputPaused, 1);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); changed.Set(); ready.TrySetCanceled();
        if (Task.CurrentId == worker.Id)
        { _ = worker.ContinueWith(_ => { changed.Dispose(); stop.Dispose(); }, TaskScheduler.Default); return; }
        if (worker.Wait(TimeSpan.FromSeconds(3))) { changed.Dispose(); stop.Dispose(); }
        else _ = worker.ContinueWith(_ => { changed.Dispose(); stop.Dispose(); }, TaskScheduler.Default);
    }
}

sealed record AudioChunk(byte[] Data, long Timestamp)
{
    public int Frames => Data.Length / AudioDecoder.BlockAlign;
}

sealed class AudioDecoder : IDisposable
{
    internal const int SampleRate = 48000, Channels = 2, BlockAlign = 4;
    IntPtr reader;
    bool comStarted, mediaStarted;
    public bool HasAudio { get; }
    public long DurationTicks { get; }
    public long LastEndTicks { get; private set; }
    public AudioDecoder(string path)
    {
        IntPtr type = IntPtr.Zero, native = IntPtr.Zero;
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到媒体文件。", path);
            MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, 0)); comStarted = true;
            MfNative.Check(MfNative.MFStartup(0x00020070, 0)); mediaStarted = true;
            MfNative.Check(MfNative.MFCreateSourceReaderFromURL(Path.GetFullPath(path), IntPtr.Zero, out reader));
            MfNative.Check(MfNative.Method<MfNative.SelectStream>(reader, 4)(reader, 0xfffffffe, 0));
            int audioType = MfNative.Method<AudioNative.GetNativeType>(reader, 5)(reader, AudioNative.AudioStream, 0, out native);
            if (audioType == unchecked((int)0xc00d36b3)) return; // MF_E_INVALIDSTREAMNUMBER: no audio stream.
            MfNative.Check(audioType);
            HasAudio = true;
            MfNative.Check(MfNative.MFCreateMediaType(out type));
            AudioNative.PcmFormat(type);
            MfNative.Check(MfNative.Method<MfNative.SetMediaType>(reader, 7)(reader, AudioNative.AudioStream, IntPtr.Zero, type));
            MfNative.Check(MfNative.Method<MfNative.SelectStream>(reader, 4)(reader, AudioNative.AudioStream, 1));
            CheckFormat();
            var durationKey = AudioNative.PresentationDuration;
            if (MfNative.Method<AudioNative.GetPresentation>(reader, 12)(reader, 0xffffffff, ref durationKey, out var duration) >= 0)
            {
                try { if (duration.Type is 20 or 21 && duration.Long > 0) DurationTicks = duration.Long; }
                finally { AudioNative.PropVariantClear(ref duration); }
            }
        }
        catch { Dispose(); throw; }
        finally { MfNative.Release(ref native); MfNative.Release(ref type); }
    }
    void CheckFormat()
    {
        IntPtr type = IntPtr.Zero;
        try
        {
            MfNative.Check(MfNative.Method<MfNative.GetMediaType>(reader, 6)(reader, AudioNative.AudioStream, out type));
            if (AudioNative.GetUInt32(type, AudioNative.AudioBits) != 16 ||
                AudioNative.GetUInt32(type, AudioNative.AudioRate) != SampleRate ||
                AudioNative.GetUInt32(type, AudioNative.AudioChannels) != Channels ||
                AudioNative.GetUInt32(type, AudioNative.AudioBlockAlign) != BlockAlign)
                throw new NotSupportedException("音频解码器没有返回支持的 16 位 PCM 格式。");
        }
        finally { MfNative.Release(ref type); }
    }
    public AudioChunk? Read(CancellationToken token)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            token.ThrowIfCancellationRequested();
            IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
            bool locked = false;
            try
            {
                MfNative.Check(MfNative.Method<MfNative.ReadSample>(reader, 9)(reader, AudioNative.AudioStream, 0,
                    out _, out uint flags, out long timestamp, out sample));
                if ((flags & 1) != 0) throw new InvalidDataException("音轨解码器报告了错误。");
                if ((flags & 0x20) != 0) CheckFormat();
                if (sample == IntPtr.Zero) { if ((flags & 2) != 0) return null; continue; }
                MfNative.Check(MfNative.Method<MfNative.SampleBuffer>(sample, 41)(sample, out buffer));
                MfNative.Check(MfNative.Method<MfNative.BufferLock>(buffer, 3)(buffer, out IntPtr data, out _, out uint length));
                locked = true;
                if (length == 0) continue;
                if (length % BlockAlign != 0 || length > SampleRate * BlockAlign * 10)
                    throw new InvalidDataException("声音样本的 PCM 缓冲区长度无效。");
                var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, bytes.Length);
                LastEndTicks = Math.Max(LastEndTicks, timestamp + bytes.Length / BlockAlign * 10000000L / SampleRate);
                return new(bytes, timestamp);
            }
            finally
            {
                if (locked) MfNative.Method<MfNative.SimpleCall>(buffer, 4)(buffer);
                MfNative.Release(ref buffer); MfNative.Release(ref sample);
            }
        }
        throw new InvalidDataException("音轨解码器没有返回有效声音样本。");
    }
    public void Seek(long positionTicks)
    {
        var format = Guid.Empty;
        var position = new MfNative.PropVariant { Type = 20, Long = Math.Max(0, positionTicks) };
        MfNative.Check(MfNative.Method<MfNative.SetPosition>(reader, 8)(reader, ref format, ref position));
    }
    public void Dispose()
    {
        MfNative.Release(ref reader);
        if (mediaStarted) { MfNative.MFShutdown(); mediaStarted = false; }
        if (comStarted) { MfNative.CoUninitialize(); comStarted = false; }
    }
}

// Small prepared WAVEHDR buffers remain alive until the device returns them.
// There is no system-device volume change: gain is applied to our own PCM.
sealed class WaveOutput : IDisposable
{
    readonly List<WaveBuffer> buffers = [];
    readonly int sampleRate, blockAlign;
    IntPtr handle;
    long submitted, positionWrap;
    uint previousPosition;
    public long SubmittedFrames => submitted;
    public long QueuedFrames => buffers.Sum(buffer => (long)buffer.Frames);
    public WaveOutput(int sampleRate, int channels)
    {
        this.sampleRate = sampleRate; blockAlign = channels * 2;
        var format = new AudioNative.WaveFormat
        {
            FormatTag = 1, Channels = (ushort)channels, SamplesPerSecond = (uint)sampleRate,
            BitsPerSample = 16, BlockAlign = (ushort)blockAlign, AverageBytesPerSecond = (uint)(sampleRate * blockAlign)
        };
        AudioNative.CheckWave(AudioNative.waveOutOpen(out handle, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0));
        try { Pause(); }
        catch { AudioNative.waveOutClose(handle); handle = IntPtr.Zero; throw; }
    }
    public long PositionFrames
    {
        get
        {
            var time = new AudioNative.MmTime { Type = 2 }; // TIME_SAMPLES
            AudioNative.CheckWave(AudioNative.waveOutGetPosition(handle, ref time, (uint)Marshal.SizeOf<AudioNative.MmTime>()));
            uint current = time.Value;
            if (current < previousPosition && previousPosition - current > 0x80000000) positionWrap += 1L << 32;
            previousPosition = current;
            long value = positionWrap + current;
            return time.Type switch { 2 => value, 4 => value / blockAlign, 1 => value * sampleRate / 1000,
                _ => throw new NotSupportedException("声音设备不支持读取播放位置。") };
        }
    }
    public void Write(byte[] source, int offset, int frames, float volume)
    {
        byte[] bytes = new byte[frames * blockAlign];
        Buffer.BlockCopy(source, offset, bytes, 0, bytes.Length);
        if (volume < 1f)
            for (int i = 0; i < bytes.Length; i += 2)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i, 2), (short)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i, 2)) * volume));
        var buffer = new WaveBuffer(handle, bytes, frames);
        try { buffer.Write(); buffers.Add(buffer); submitted += frames; }
        catch { buffer.Dispose(); throw; }
    }
    public void Reap()
    {
        for (int i = buffers.Count - 1; i >= 0; i--)
            if (buffers[i].Done) { buffers[i].Dispose(); buffers.RemoveAt(i); }
    }
    public void Pause() => AudioNative.CheckWave(AudioNative.waveOutPause(handle));
    public void Restart() => AudioNative.CheckWave(AudioNative.waveOutRestart(handle));
    public void Reset()
    {
        AudioNative.CheckWave(AudioNative.waveOutReset(handle));
        Reap();
        if (buffers.Count != 0) throw new IOException("声音设备没有归还停止的播放缓冲区。");
        submitted = 0; previousPosition = 0; positionWrap = 0;
    }
    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        Reset();
        AudioNative.CheckWave(AudioNative.waveOutClose(handle)); handle = IntPtr.Zero;
    }
    sealed class WaveBuffer : IDisposable
    {
        readonly IntPtr output;
        IntPtr data, header;
        bool prepared;
        public int Frames { get; }
        public bool Done => (Marshal.PtrToStructure<AudioNative.WaveHeader>(header).Flags & 1) != 0;
        public WaveBuffer(IntPtr output, byte[] bytes, int frames)
        {
            this.output = output; Frames = frames;
            try
            {
                data = Marshal.AllocHGlobal(bytes.Length); Marshal.Copy(bytes, 0, data, bytes.Length);
                header = Marshal.AllocHGlobal(Marshal.SizeOf<AudioNative.WaveHeader>());
                Marshal.StructureToPtr(new AudioNative.WaveHeader { Data = data, BufferLength = (uint)bytes.Length }, header, false);
                AudioNative.CheckWave(AudioNative.waveOutPrepareHeader(output, header, (uint)Marshal.SizeOf<AudioNative.WaveHeader>()));
                prepared = true;
            }
            catch { Dispose(); throw; }
        }
        public void Write() => AudioNative.CheckWave(AudioNative.waveOutWrite(output, header, (uint)Marshal.SizeOf<AudioNative.WaveHeader>()));
        public void Dispose()
        {
            if (prepared)
            {
                AudioNative.CheckWave(AudioNative.waveOutUnprepareHeader(output, header, (uint)Marshal.SizeOf<AudioNative.WaveHeader>()));
                prepared = false;
            }
            if (header != IntPtr.Zero) { Marshal.FreeHGlobal(header); header = IntPtr.Zero; }
            if (data != IntPtr.Zero) { Marshal.FreeHGlobal(data); data = IntPtr.Zero; }
        }
    }
}

static class AudioNative
{
    internal const uint AudioStream = 0xfffffffd;
    internal static readonly Guid AudioType = new("73647561-0000-0010-8000-00aa00389b71"), Pcm = new("00000001-0000-0010-8000-00aa00389b71"),
        AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a"), AudioRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba"),
        AudioBits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669"), AudioBlockAlign = new("322de230-9eeb-43bd-ab7a-ff412251541d"),
        AudioAverageBytes = new("1aab75c8-cfef-451c-ab95-ac034b8e1731"), PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    internal static void PcmFormat(IntPtr type)
    {
        MfNative.SetGuid(type, MfNative.MajorType, AudioType); MfNative.SetGuid(type, MfNative.Subtype, Pcm);
        MfNative.SetUInt32(type, AudioChannels, AudioDecoder.Channels); MfNative.SetUInt32(type, AudioRate, AudioDecoder.SampleRate);
        MfNative.SetUInt32(type, AudioBits, 16); MfNative.SetUInt32(type, AudioBlockAlign, AudioDecoder.BlockAlign);
        MfNative.SetUInt32(type, AudioAverageBytes, AudioDecoder.SampleRate * AudioDecoder.BlockAlign);
    }
    internal static uint GetUInt32(IntPtr type, Guid key)
    { MfNative.Check(MfNative.Method<MfNative.GetUInt32Value>(type, 7)(type, ref key, out uint value)); return value; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetNativeType(IntPtr self, uint stream, uint index, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetPresentation(IntPtr self, uint stream, ref Guid key, out MfNative.PropVariant value);
    [DllImport("ole32.dll")] internal static extern int PropVariantClear(ref MfNative.PropVariant value);
    [StructLayout(LayoutKind.Sequential, Pack = 2)] internal struct WaveFormat
    {
        internal ushort FormatTag, Channels;
        internal uint SamplesPerSecond, AverageBytesPerSecond;
        internal ushort BlockAlign, BitsPerSample, ExtraSize;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct WaveHeader
    {
        internal IntPtr Data;
        internal uint BufferLength, BytesRecorded;
        internal UIntPtr User;
        internal uint Flags, Loops;
        internal IntPtr Next;
        internal UIntPtr Reserved;
    }
    [StructLayout(LayoutKind.Explicit, Size = 12)] internal struct MmTime
    { [FieldOffset(0)] internal uint Type; [FieldOffset(4)] internal uint Value; }
    [DllImport("winmm.dll")] internal static extern uint waveOutOpen(out IntPtr output, uint device, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] internal static extern uint waveOutPrepareHeader(IntPtr output, IntPtr header, uint size);
    [DllImport("winmm.dll")] internal static extern uint waveOutUnprepareHeader(IntPtr output, IntPtr header, uint size);
    [DllImport("winmm.dll")] internal static extern uint waveOutWrite(IntPtr output, IntPtr header, uint size);
    [DllImport("winmm.dll")] internal static extern uint waveOutGetPosition(IntPtr output, ref MmTime position, uint size);
    [DllImport("winmm.dll")] internal static extern uint waveOutPause(IntPtr output);
    [DllImport("winmm.dll")] internal static extern uint waveOutRestart(IntPtr output);
    [DllImport("winmm.dll")] internal static extern uint waveOutReset(IntPtr output);
    [DllImport("winmm.dll")] internal static extern uint waveOutClose(IntPtr output);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveOutGetErrorText(uint error, System.Text.StringBuilder text, uint length);
    internal static void CheckWave(uint result)
    {
        if (result == 0) return;
        var message = new System.Text.StringBuilder(256); waveOutGetErrorText(result, message, 256);
        throw new IOException($"声音设备错误 {result}：{message}");
    }
}
