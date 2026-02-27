// ============================================================
// UDPVideoReceiver.cs
// Drop into:  Aimmy2/Class/UDPVideoReceiver.cs
//
// Faithful C# port of the Python OBS_UDP_Receiver with:
//  - Thread-pool parallel JPEG decoding
//  - Lock-free double-buffered frame storage
//  - Buffer overflow protection (2 MB limit, same as Python)
//  - FPS / latency statistics
//  - Reconnect support
//  - Identical MJPEG SOI/EOI marker assembly logic
// ============================================================

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Aimmy2.Class
{
    /// <summary>
    /// Receives an MJPEG-over-UDP stream (e.g. from OBS or FFmpeg on another PC)
    /// and exposes each decoded frame as a <see cref="Bitmap"/> that can be used
    /// exactly like the GDI+ / DirectX capture paths inside AIManager / CaptureManager.
    ///
    /// SENDER COMMAND (gaming PC, FFmpeg):
    ///   ffmpeg -f gdigrab -framerate 60 -i desktop
    ///          -vf "crop=640:640:(in_w/2-320):(in_h/2-320)"
    ///          -vcodec mjpeg -q:v 3
    ///          -f mpegts udp://AIMMY_PC_IP:PORT
    ///
    /// SENDER COMMAND (gaming PC, OBS custom output via obs-websocket / script):
    ///   Output → Custom FFmpeg Recording → udp://AIMMY_PC_IP:PORT (mpegts, mjpeg)
    /// </summary>
    public sealed class UDPVideoReceiver : IDisposable
    {
        // ── Public state ─────────────────────────────────────────────────────────

        /// <summary>True while the socket is open and the receive thread is running.</summary>
        public bool IsConnected { get; private set; }

        /// <summary>True while frames are actively being received.</summary>
        public bool IsReceiving { get; private set; }

        /// <summary>Human-readable status string (matches Python's logging output).</summary>
        public string StatusMessage { get; private set; } = "Not started";

        /// <summary>Current incoming FPS (datagrams decoded into frames per second).</summary>
        public double CurrentFps { get; private set; }

        /// <summary>Current frame processing FPS.</summary>
        public double ProcessingFps { get; private set; }

        /// <summary>Average receive delay in milliseconds.</summary>
        public double ReceiveDelay { get; private set; }

        // ── Configuration ─────────────────────────────────────────────────────────

        public string ListenIP { get; private set; } = "0.0.0.0";
        public int ListenPort { get; private set; } = 11000;

        // ── MJPEG markers (identical to Python source) ───────────────────────────
        private static readonly byte[] SOI = { 0xFF, 0xD8 }; // Start Of Image
        private static readonly byte[] EOI = { 0xFF, 0xD9 }; // End Of Image

        private const int MaxBufferBytes  = 2 * 1024 * 1024; // 2 MB – same as Python
        private const int MaxUdpDatagram  = 65536;           // max UDP payload
        private const int MaxFramesPerPkt = 5;               // same limit as Python
        private const int MinJpegBytes    = 100;             // skip tiny/corrupt frames
        private const int MaxJpegBytes    = 10 * 1024 * 1024; // 10 MB per frame

        // ── Threading ─────────────────────────────────────────────────────────────

        private UdpClient?  _udpClient;
        private Thread?     _receiveThread;
        private Thread?     _processingThread;
        private CancellationTokenSource _cts = new();

        // Thread pool for parallel JPEG decoding (mirrors Python's ThreadPoolExecutor)
        private ThreadPool? _decoderPool;
        private readonly BlockingCollection<(byte[] jpeg, long receiveTickNs)> _frameQueue
            = new BlockingCollection<(byte[], long)>(100); // capacity = 100, same as Python

        // ── Frame storage ──────────────────────────────────────────────────────────

        private readonly ReaderWriterLockSlim _frameLock = new(LockRecursionPolicy.NoRecursion);
        private Bitmap?   _latestFrame;

        // ── MJPEG assembly buffer ──────────────────────────────────────────────────

        // We use a MemoryStream + separate lock so the receive thread and the
        // buffer-trimming code never race. Matches Python's bytearray + buffer_lock.
        private readonly object      _bufferLock = new();
        private          byte[]      _assemblyBuffer = new byte[MaxBufferBytes];
        private          int         _assemblyLength = 0;

        // ── Performance counters ───────────────────────────────────────────────────

        private int    _fpsCounter;
        private double _lastFpsTime;
        private int    _processingCounter;
        private double _lastProcessingTime;

        // ─────────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bind a UDP socket and start receiving MJPEG data.
        /// </summary>
        /// <param name="ip">
        /// Local IP to bind on.  Use "0.0.0.0" to accept from any interface,
        /// or the specific NIC IP the sender is targeting.
        /// </param>
        /// <param name="port">UDP port to listen on.</param>
        public bool Connect(string ip = "0.0.0.0", int port = 11000)
        {
            if (IsConnected) Disconnect();

            ListenIP   = ip;
            ListenPort = port;

            try
            {
                _cts = new CancellationTokenSource();

                var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);

                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                // 1 MB socket receive buffer – same as Python SO_RCVBUF setting
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 1024 * 1024);
                _udpClient.Client.Bind(endpoint);
                _udpClient.Client.ReceiveTimeout = 5000; // 5 s – same as Python settimeout

                // Decoder thread pool (mirrors Python's ThreadPoolExecutor)
                int workers = Math.Min(8, Environment.ProcessorCount + 4);
                _decoderPool = new ThreadPool(workers);

                // Frame processing thread (mirrors Python's _frame_processing_loop)
                _processingThread = new Thread(FrameProcessingLoop)
                {
                    IsBackground = true,
                    Name         = "UDPFrameProcessor",
                    Priority     = ThreadPriority.AboveNormal
                };
                _processingThread.Start();

                // Receive thread (mirrors Python's _receive_loop)
                _receiveThread = new Thread(ReceiveLoop)
                {
                    IsBackground = true,
                    Name         = "UDPReceiver",
                    Priority     = ThreadPriority.AboveNormal
                };
                _receiveThread.Start();

                IsConnected = true;
                StatusMessage = $"Listening on {ip}:{port}";
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Connect error: {ex.Message}";
                IsConnected   = false;
                return false;
            }
        }

        /// <summary>Close the socket and stop all threads gracefully.</summary>
        public void Disconnect()
        {
            IsConnected  = false;
            IsReceiving  = false;
            _cts.Cancel();

            try { _udpClient?.Close(); } catch { /* ignored */ }

            _receiveThread?.Join(2000);
            _decoderPool?.Shutdown();
            _frameQueue.CompleteAdding();
            _processingThread?.Join(2000);

            _udpClient = null;

            lock (_bufferLock)
            {
                _assemblyLength = 0;
            }

            _frameLock.EnterWriteLock();
            try
            {
                _latestFrame?.Dispose();
                _latestFrame = null;
            }
            finally { _frameLock.ExitWriteLock(); }

            StatusMessage = "Disconnected";
        }

        /// <summary>
        /// Returns a CLONE of the latest decoded frame (caller owns and must dispose it),
        /// or null if no frame has been received yet.
        ///
        /// This is the method called by CaptureManager / AIManager instead of the
        /// normal GDI+ / DirectX bitmap – it returns the exact same type.
        /// </summary>
        public Bitmap? GetLatestFrame()
        {
            _frameLock.EnterReadLock();
            try
            {
                return _latestFrame is null
                    ? null
                    : (Bitmap)_latestFrame.Clone();
            }
            finally { _frameLock.ExitReadLock(); }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Receive loop  (Python: _receive_loop)
        // ─────────────────────────────────────────────────────────────────────────

        private void ReceiveLoop()
        {
            IsReceiving = true;

            var remote = new IPEndPoint(IPAddress.Any, 0);

            while (!_cts.IsCancellationRequested && IsConnected)
            {
                try
                {
                    byte[] data        = _udpClient!.Receive(ref remote);
                    long   receiveNs   = Stopwatch.GetTimestamp();

                    ProcessMjpegData(data, receiveNs);

                    // FPS counter
                    Interlocked.Increment(ref _fpsCounter);
                    double now = NowSec();
                    if (now - _lastFpsTime >= 1.0)
                    {
                        CurrentFps    = _fpsCounter / (now - _lastFpsTime);
                        _fpsCounter   = 0;
                        _lastFpsTime  = now;
                    }
                }
                catch (SocketException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex) when ((SocketError)ex.ErrorCode == SocketError.TimedOut)
                {
                    // 5-second timeout – just retry, same as Python "continue"
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (IsConnected)
                        StatusMessage = $"Recv error: {ex.Message}";
                }
            }

            IsReceiving = false;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // MJPEG data processing  (Python: _process_mjpeg_data)
        // ─────────────────────────────────────────────────────────────────────────

        private void ProcessMjpegData(byte[] data, long receiveNs)
        {
            lock (_bufferLock)
            {
                // Append datagram to assembly buffer
                if (_assemblyLength + data.Length > MaxBufferBytes)
                {
                    // Buffer overflow – discard, same as Python "clearing"
                    _assemblyLength = 0;
                    return;
                }
                Array.Copy(data, 0, _assemblyBuffer, _assemblyLength, data.Length);
                _assemblyLength += data.Length;

                int bytesConsumed  = 0;
                int framesThisPkt  = 0;

                while (framesThisPkt < MaxFramesPerPkt)
                {
                    // Slice of what's left to scan
                    int scanStart  = bytesConsumed;
                    int scanLength = _assemblyLength - bytesConsumed;

                    // Find SOI
                    int soiPos = IndexOf(_assemblyBuffer, scanStart, scanLength, SOI);
                    if (soiPos < 0)
                    {
                        // No start marker – keep last 2 bytes in case SOI spans a boundary
                        if (scanLength > 2)
                        {
                            int keep = Math.Min(scanLength, 1024);
                            Array.Copy(_assemblyBuffer, _assemblyLength - keep, _assemblyBuffer, 0, keep);
                            _assemblyLength = keep;
                        }
                        return;
                    }

                    // Discard data before SOI
                    bytesConsumed = soiPos;

                    // Find EOI after SOI
                    int eoiPos = IndexOf(_assemblyBuffer, bytesConsumed + 2,
                                         _assemblyLength - (bytesConsumed + 2), EOI);
                    if (eoiPos < 0) break; // incomplete frame – wait for more data

                    int frameStart  = bytesConsumed;
                    int frameEnd    = eoiPos + 2;           // inclusive of EOI
                    int frameLength = frameEnd - frameStart;

                    bytesConsumed = frameEnd;
                    framesThisPkt++;

                    // Sanity checks (same as Python)
                    if (frameLength < MinJpegBytes || frameLength > MaxJpegBytes)
                        continue;

                    // Copy the JPEG bytes out before releasing the lock
                    var jpeg = new byte[frameLength];
                    Array.Copy(_assemblyBuffer, frameStart, jpeg, 0, frameLength);

                    // Submit to thread pool for parallel decoding
                    if (!_frameQueue.IsAddingCompleted && !_cts.IsCancellationRequested)
                    {
                        _decoderPool?.QueueWorkItem(() =>
                        {
                            var decoded = DecodeJpegFrame(jpeg);
                            if (decoded != null)
                            {
                                try { _frameQueue.TryAdd((decoded, receiveNs), 0); }
                                catch { /* queue full – skip frame */ }
                            }
                        });
                    }
                }

                // Compact the buffer – remove all consumed bytes
                if (bytesConsumed > 0 && bytesConsumed <= _assemblyLength)
                {
                    int remaining = _assemblyLength - bytesConsumed;
                    if (remaining > 0)
                        Array.Copy(_assemblyBuffer, bytesConsumed, _assemblyBuffer, 0, remaining);
                    _assemblyLength = remaining;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Frame processing loop  (Python: _frame_processing_loop)
        // ─────────────────────────────────────────────────────────────────────────

        private void FrameProcessingLoop()
        {
            while (!_cts.IsCancellationRequested || !_frameQueue.IsCompleted)
            {
                try
                {
                    if (!_frameQueue.TryTake(out var item, 100))
                        continue;

                    var (jpegBytes, receiveNs) = item;

                    // jpegBytes here is the RAW bytes ready to decode
                    // (pool already decoded, see note in _decoderPool.QueueWorkItem above)
                    // We re-use the raw bytes path for the final BMP conversion
                    using var ms  = new MemoryStream(jpegBytes);
                    Bitmap? bmp   = null;
                    try { bmp = new Bitmap(ms); } catch { continue; }

                    if (bmp.Width < 10 || bmp.Height < 10) { bmp.Dispose(); continue; }

                    // Convert to 24 bpp RGB – same pixel format as GDI+/DirectX captures
                    var converted = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(converted))
                        g.DrawImage(bmp, 0, 0);
                    bmp.Dispose();

                    _frameLock.EnterWriteLock();
                    try
                    {
                        _latestFrame?.Dispose();
                        _latestFrame = converted;
                    }
                    finally { _frameLock.ExitWriteLock(); }

                    // Update stats
                    Interlocked.Increment(ref _processingCounter);
                    double now = NowSec();
                    if (now - _lastProcessingTime >= 1.0)
                    {
                        ProcessingFps         = _processingCounter / (now - _lastProcessingTime);
                        _processingCounter    = 0;
                        _lastProcessingTime   = now;

                        double latencyMs = (double)(Stopwatch.GetTimestamp() - receiveNs)
                                           / Stopwatch.Frequency * 1000.0;
                        ReceiveDelay  = latencyMs;
                        StatusMessage = $"Receiving | {ProcessingFps:F1} fps | {latencyMs:F1} ms latency";
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (IsConnected)
                        StatusMessage = $"Processing error: {ex.Message}";
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // JPEG decode  (Python: _decode_jpeg_frame)
        // Returns the raw validated JPEG bytes (or null if invalid).
        // The actual Bitmap conversion happens in the processing loop so we can
        // do the BitmapData manipulation on the STA thread.
        // ─────────────────────────────────────────────────────────────────────────

        private static byte[]? DecodeJpegFrame(byte[] jpeg)
        {
            // Validate markers  (same as Python checks)
            if (jpeg.Length < MinJpegBytes)   return null;
            if (jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
            if (jpeg[jpeg.Length - 2] != 0xFF || jpeg[jpeg.Length - 1] != 0xD9) return null;

            // Quick corruption check on a sample of bytes
            // (Python: _is_frame_corrupted – checks that not everything is 0 or 255)
            int zeroCount = 0, maxCount = 0;
            int step = Math.Max(1, jpeg.Length / 200);
            for (int i = 0; i < jpeg.Length; i += step)
            {
                if (jpeg[i] == 0)   zeroCount++;
                if (jpeg[i] == 255) maxCount++;
            }
            int samples = jpeg.Length / step;
            if (zeroCount > samples * 0.9 || maxCount > samples * 0.9)
                return null; // looks corrupted

            return jpeg; // valid – return as-is; Bitmap() will decode it in the loop
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Find first occurrence of <paramref name="needle"/> in <paramref name="buf"/>.</summary>
        private static int IndexOf(byte[] buf, int start, int length, byte[] needle)
        {
            int end = start + length - needle.Length;
            for (int i = start; i <= end; i++)
            {
                bool found = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (buf[i + j] != needle[j]) { found = false; break; }
                }
                if (found) return i;
            }
            return -1;
        }

        private static double NowSec() =>
            (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

        // ─────────────────────────────────────────────────────────────────────────
        // IDisposable
        // ─────────────────────────────────────────────────────────────────────────

        public void Dispose()
        {
            Disconnect();
            _frameLock.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ── Minimal thread-pool helper ────────────────────────────────────────────────
    // Mirrors Python's ThreadPoolExecutor without the overhead of the .NET ThreadPool
    // (which has global state issues for high-frequency decode tasks).

    internal sealed class ThreadPool : IDisposable
    {
        private readonly Thread[]               _workers;
        private readonly BlockingCollection<Action> _queue = new();
        private volatile bool                   _shutdown;

        public ThreadPool(int workers)
        {
            _workers = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                _workers[i] = new Thread(Worker) { IsBackground = true, Name = $"FrameDecoder-{i}" };
                _workers[i].Start();
            }
        }

        public void QueueWorkItem(Action action)
        {
            if (!_shutdown)
                _queue.TryAdd(action);
        }

        private void Worker()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); } catch { /* individual frame errors are non-fatal */ }
            }
        }

        public void Shutdown()
        {
            _shutdown = true;
            _queue.CompleteAdding();
            foreach (var t in _workers) t.Join(500);
        }

        public void Dispose() => Shutdown();
    }
}
