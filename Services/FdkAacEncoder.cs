using System.Runtime.InteropServices;

namespace AdoxicSound.Services;

/// <summary>Chunked AAC-LC + ADTS encoder on Fraunhofer FDK (libfdk-aac-2.dll beside the exe).</summary>
public sealed class FdkAacEncoder : IDisposable
{
    private const string Dll = "libfdk-aac-2.dll";

    private const uint AACENC_AOT = 0x0100;
    private const uint AACENC_BITRATE = 0x0101;
    private const uint AACENC_SAMPLERATE = 0x0103;
    private const uint AACENC_CHANNELMODE = 0x0106;
    private const uint AACENC_CHANNELORDER = 0x0107;
    private const uint AACENC_AFTERBURNER = 0x0200;
    private const uint AACENC_TRANSMUX = 0x0300;
    private const uint AACENC_SIGNALING_MODE = 0x0302;
    private const int AOT_AAC_LC = 2;
    private const int TT_MP4_ADTS = 2;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int aacEncOpen(out IntPtr handle, uint encModules, uint maxChannels);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int aacEncoder_SetParam(IntPtr handle, uint param, uint value);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int aacEncEncode(IntPtr handle, IntPtr inDesc, IntPtr outDesc, IntPtr inArgs, IntPtr outArgs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int aacEncInfo(IntPtr handle, IntPtr info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int aacEncClose(ref IntPtr handle);

    private IntPtr _h;
    private readonly int _channels;
    private readonly IntPtr _inPtrs, _inIds, _inSizes, _inElSizes, _inDesc;
    private readonly IntPtr _outPtrs, _outIds, _outSizes, _outElSizes, _outDesc;
    private readonly IntPtr _inArgs, _outArgs;
    private readonly byte[] _outBuf = new byte[65536];
    private readonly GCHandle _outHandle;
    private bool _disposed;

    public int FrameLength { get; }

    public FdkAacEncoder(int channels, int sampleRate, int bitrate)
    {
        _channels = Math.Max(1, Math.Min(2, channels));
        Check(aacEncOpen(out _h, 0, (uint)_channels), "open");
        try
        {
            Check(aacEncoder_SetParam(_h, AACENC_AOT, AOT_AAC_LC), "AOT");
            Check(aacEncoder_SetParam(_h, AACENC_SAMPLERATE, (uint)sampleRate), "rate");
            Check(aacEncoder_SetParam(_h, AACENC_CHANNELMODE, (uint)_channels), "channels"); // MODE_1=1, MODE_2=2
            Check(aacEncoder_SetParam(_h, AACENC_CHANNELORDER, 1), "order"); // WAV order
            Check(aacEncoder_SetParam(_h, AACENC_BITRATE, (uint)bitrate), "bitrate");
            Check(aacEncoder_SetParam(_h, AACENC_TRANSMUX, TT_MP4_ADTS), "transmux");
            Check(aacEncoder_SetParam(_h, AACENC_SIGNALING_MODE, 0), "signaling");
            Check(aacEncoder_SetParam(_h, AACENC_AFTERBURNER, 1), "afterburner");
            Check(aacEncEncode(_h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "init");
            var info = new byte[128];
            var infoH = GCHandle.Alloc(info, GCHandleType.Pinned);
            try
            {
                Check(aacEncInfo(_h, infoH.AddrOfPinnedObject()), "info");
                FrameLength = BitConverter.ToInt32(info, 16); // frameLength field
                if (FrameLength <= 0) FrameLength = 1024;
            }
            finally { infoH.Free(); }
        }
        catch
        {
            try { var h = _h; aacEncClose(ref h); } catch { }
            _h = IntPtr.Zero;
            throw;
        }

        _inPtrs = AllocPtr(1); _inIds = AllocInt(0); _inSizes = AllocInt(0); _inElSizes = AllocInt(2);
        _outPtrs = AllocPtr(1); _outIds = AllocInt(3); _outSizes = AllocInt(_outBuf.Length); _outElSizes = AllocInt(1);
        _outHandle = GCHandle.Alloc(_outBuf, GCHandleType.Pinned);
        Marshal.WriteIntPtr(_outPtrs, _outHandle.AddrOfPinnedObject());
        _inDesc = AllocDesc(_inPtrs, _inIds, _inSizes, _inElSizes, 1);
        _outDesc = AllocDesc(_outPtrs, _outIds, _outSizes, _outElSizes, 1);
        _inArgs = AllocArgs();
        _outArgs = AllocArgs();
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new InvalidOperationException($"FDK {what} failed (0x{rc:X4})");
    }

    private static IntPtr AllocPtr(int n) { var p = Marshal.AllocHGlobal(IntPtr.Size * n); for (int i = 0; i < n; i++) Marshal.WriteIntPtr(p, i * IntPtr.Size, IntPtr.Zero); return p; }
    private static IntPtr AllocInt(int v) { var p = Marshal.AllocHGlobal(4); Marshal.WriteInt32(p, v); return p; }
    private static IntPtr AllocArgs() { var p = Marshal.AllocHGlobal(8); Marshal.WriteInt32(p, 0, 0); Marshal.WriteInt32(p, 4, 0); return p; }
    private static IntPtr AllocDesc(IntPtr ptrs, IntPtr ids, IntPtr sizes, IntPtr els, int n)
    {
        var p = Marshal.AllocHGlobal(IntPtr.Size + IntPtr.Size * 4);
        Marshal.WriteInt32(p, 0, n);
        Marshal.WriteIntPtr(p, IntPtr.Size, ptrs);
        Marshal.WriteIntPtr(p, IntPtr.Size * 2, ids);
        Marshal.WriteIntPtr(p, IntPtr.Size * 3, sizes);
        Marshal.WriteIntPtr(p, IntPtr.Size * 4, els);
        return p;
    }

    /// <summary>Encode interleaved 16-bit PCM shorts; returns ADTS bytes (may be empty).</summary>
    public byte[] Encode(short[] pcm, int offset, int samplesPerChannel)
    {
        var pin = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        try
        {
            Marshal.WriteIntPtr(_inPtrs, IntPtr.Add(pin.AddrOfPinnedObject(), offset * 2));
            Marshal.WriteInt32(_inSizes, samplesPerChannel * _channels * 2);
            Marshal.WriteInt32(_inArgs, 0, samplesPerChannel * _channels);
            Marshal.WriteInt32(_inArgs, 4, 0);
            Check(aacEncEncode(_h, _inDesc, _outDesc, _inArgs, _outArgs), "encode");
            int n = Marshal.ReadInt32(_outArgs, 0);
            if (n <= 0) return Array.Empty<byte>();
            var out_ = new byte[n];
            Buffer.BlockCopy(_outBuf, 0, out_, 0, n);
            return out_;
        }
        finally { pin.Free(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            // flush: encode with numInSamples = -1
            Marshal.WriteInt32(_inArgs, 0, -1);
            Marshal.WriteInt32(_inArgs, 4, 0);
            aacEncEncode(_h, _inDesc, _outDesc, _inArgs, _outArgs);
        }
        catch { }
        try { if (_outHandle.IsAllocated) _outHandle.Free(); } catch { }
        foreach (var p in new[] { _inPtrs, _inIds, _inSizes, _inElSizes, _outPtrs, _outIds, _outSizes, _outElSizes, _inDesc, _outDesc, _inArgs, _outArgs })
            try { Marshal.FreeHGlobal(p); } catch { }
        try { if (_h != IntPtr.Zero) { var h = _h; aacEncClose(ref h); } } catch { }
        _h = IntPtr.Zero;
    }
}
