using System.Runtime.InteropServices;

namespace GhostyNav.Services.Assistant;

/// <summary>
/// Default microphone as 16 kHz 16-bit mono PCM in 100 ms chunks, via the classic waveIn API
/// (tiny, no dependencies, and respects the Windows microphone privacy setting).
/// </summary>
public sealed unsafe class Microphone : IDisposable
{
    private const int SampleRate = 16000, ChunkBytes = SampleRate * 2 / 10, BufferCount = 4;
    private const int WAVE_MAPPER = -1, CALLBACK_EVENT = 0x50000;
    private const uint WHDR_DONE = 0x1;

    private nint _device;
    private readonly nint[] _headers = new nint[BufferCount];
    private readonly AutoResetEvent _signal = new(false);
    private Thread? _thread;
    private volatile bool _running;

    /// <summary>(PCM chunk, level 0..1). Raised on the capture thread.</summary>
    public event Action<byte[], double>? Chunk;

    /// <exception cref="InvalidOperationException">No microphone, or access is blocked.</exception>
    public void Start()
    {
        var format = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = 1, nSamplesPerSec = SampleRate, wBitsPerSample = 16,
            nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2
        };
        int result = waveInOpen(out _device, WAVE_MAPPER, ref format, _signal.SafeWaitHandle.DangerousGetHandle(), 0, CALLBACK_EVENT);
        if (result != 0) throw new InvalidOperationException($"No microphone available (error {result}).");

        // Headers and buffers must not move while the driver owns them: unmanaged memory.
        for (int i = 0; i < BufferCount; i++)
        {
            var header = (WAVEHDR*)NativeMemory.AllocZeroed((nuint)sizeof(WAVEHDR));
            header->lpData = (nint)NativeMemory.AllocZeroed(ChunkBytes);
            header->dwBufferLength = ChunkBytes;
            _headers[i] = (nint)header;
            waveInPrepareHeader(_device, header, sizeof(WAVEHDR));
            waveInAddBuffer(_device, header, sizeof(WAVEHDR));
        }

        _running = true;
        _thread = new Thread(Pump) { IsBackground = true, Name = "Microphone" };
        _thread.Start();
        result = waveInStart(_device);
        if (result != 0)
        {
            Dispose();
            throw new InvalidOperationException($"Couldn't start the microphone (error {result}).");
        }
    }

    private void Pump()
    {
        while (_running)
        {
            _signal.WaitOne(500);
            foreach (var h in _headers)
            {
                var header = (WAVEHDR*)h;
                if ((header->dwFlags & WHDR_DONE) == 0 || !_running) continue;
                int count = (int)header->dwBytesRecorded;
                if (count > 0)
                {
                    var pcm = new byte[count];
                    Marshal.Copy(header->lpData, pcm, 0, count);
                    Chunk?.Invoke(pcm, Level(pcm));
                }
                header->dwFlags &= ~WHDR_DONE;
                waveInAddBuffer(_device, header, sizeof(WAVEHDR));
            }
        }
    }

    /// <summary>RMS loudness mapped onto 0..1 over a ~50 dB range, so normal speech fills it.</summary>
    private static double Level(byte[] pcm)
    {
        double sum = 0;
        int samples = pcm.Length / 2;
        for (int i = 0; i < samples; i++)
        {
            double s = BitConverter.ToInt16(pcm, i * 2) / 32768.0;
            sum += s * s;
        }
        double rms = Math.Sqrt(sum / Math.Max(1, samples));
        double db = 20 * Math.Log10(rms + 1e-9);
        return Math.Clamp((db + 55) / 45, 0, 1);
    }

    public void Dispose()
    {
        if (_device == 0) return;
        _running = false;
        _signal.Set();
        _thread?.Join(1000);
        waveInReset(_device); // returns all buffers to us
        foreach (var h in _headers)
        {
            if (h == 0) continue;
            var header = (WAVEHDR*)h;
            waveInUnprepareHeader(_device, header, sizeof(WAVEHDR));
            NativeMemory.Free((void*)header->lpData);
            NativeMemory.Free(header);
        }
        Array.Clear(_headers);
        waveInClose(_device);
        _device = 0;
        _signal.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public int nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public nint lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags, dwLoops;
        public nint lpNext, reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveInOpen(out nint device, int id, ref WAVEFORMATEX format, nint callback, nint instance, int flags);
    [DllImport("winmm.dll")] private static extern int waveInPrepareHeader(nint device, WAVEHDR* header, int size);
    [DllImport("winmm.dll")] private static extern int waveInUnprepareHeader(nint device, WAVEHDR* header, int size);
    [DllImport("winmm.dll")] private static extern int waveInAddBuffer(nint device, WAVEHDR* header, int size);
    [DllImport("winmm.dll")] private static extern int waveInStart(nint device);
    [DllImport("winmm.dll")] private static extern int waveInReset(nint device);
    [DllImport("winmm.dll")] private static extern int waveInClose(nint device);
}
