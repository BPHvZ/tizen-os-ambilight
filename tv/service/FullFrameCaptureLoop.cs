using System;
using System.Threading;

namespace Service
{
    interface IHyperHdrFrameTarget
    {
        bool FullFrameEnabled { get; }
        void Offer(HyperHdrFrame frame);
    }

    readonly struct CapturePoint
    {
        public readonly double X;
        public readonly double Y;

        public CapturePoint(double x, double y) { X = x; Y = y; }
    }

    sealed class FullFrameSelector
    {
        private const int SwitchAfter = 3;
        private int _good;
        private int _bad;
        private bool _usingVideo;

        // Hold the last sent image across brief failures; switch only after a stable streak.
        public FrameChoice Update(bool usable)
        {
            if (usable)
            {
                _bad = 0;
                if (!_usingVideo && ++_good < SwitchAfter) return FrameChoice.Edge;
                _usingVideo = true;
                return FrameChoice.Video;
            }
            _good = 0;
            if (_usingVideo && ++_bad < SwitchAfter) return FrameChoice.Hold;
            _usingVideo = false;
            return FrameChoice.Edge;
        }
    }

    enum FrameChoice { Edge, Video, Hold }

    sealed class FullFrameCaptureLoop : IDisposable
    {
        internal delegate IVideoCaptureSource OpenVideo(out string error);
        private const int CaptureIntervalMs = 150;
        private const int OpenRetryMs = 5000;
        private const int MaximumEdgeAgeMs = 1000;
        private const int WorkerStallMs = 1000;
        private const int StopWaitMs = 1000;
        private const int MaximumProbeFailures = 3;
        // A timed-out stop must not let a replacement worker overlap a still-running native call.
        private static int _captureOwner;
        private readonly Func<IHyperHdrFrameTarget> _target;
        private readonly OpenVideo _open;
        private readonly Action<string, string> _log;
        private readonly CaptureStatus _status;
        private readonly CapturePoint[] _points;
        private readonly int _openRetryMs;
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _cleanupLock = new object();
        private Thread _thread;
        private EdgeObservation _edge;
        private long _lastWorkerStart;
        private int _stalled;
        private bool _disposeRequested;
        private bool _workerExited;
        private bool _handlesDisposed;
        private int _disposeStarted;

        private sealed class EdgeObservation
        {
            public EdgeFrame Frame;
            public bool Reliable;
            public long Ticks;
        }

        public FullFrameCaptureLoop(Func<IHyperHdrFrameTarget> target, CapturePoint[] points,
            CaptureStatus status, Action<string, string> log, OpenVideo open, int openRetryMs = OpenRetryMs)
        {
            _target = target;
            _points = points;
            _status = status;
            _log = log;
            _open = open ?? throw new ArgumentNullException(nameof(open));
            if (openRetryMs < 1) throw new ArgumentOutOfRangeException(nameof(openRetryMs));
            _openRetryMs = openRetryMs;
        }

        public void Start()
        {
            Interlocked.Exchange(ref _lastWorkerStart, Environment.TickCount64);
            _thread = new Thread(Run) { IsBackground = true, Name = "Full-frame capture" };
            _thread.Start();
        }

        public void OfferEdge(EdgeFrame frame, bool reliable)
        {
            long now = Environment.TickCount64;
            Interlocked.Exchange(ref _edge, new EdgeObservation
            {
                Frame = frame,
                Reliable = reliable,
                Ticks = now
            });
            var target = !_stop.IsCancellationRequested ? _target() : null;
            if (target != null && (!target.FullFrameEnabled ||
                now - Interlocked.Read(ref _lastWorkerStart) > WorkerStallMs))
            {
                bool stalled = target.FullFrameEnabled;
                bool firstStall = stalled && Interlocked.Exchange(ref _stalled, 1) == 0;
                try
                {
                    target.Offer(HyperHdrFrame.FromEdge(frame));
                    _status.Backend("ppi-edge", stalled ? "full-frame check is not responding" : null);
                }
                catch (Exception ex)
                {
                    _status.Backend("ppi-edge", $"PPI fallback delivery failed: {ex.Message}");
                    if (firstStall || !stalled) Console.Error.WriteLine($"[HyperHdrFallbackError] {ex.Message}");
                }
            }
            _wake.Set();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
            RequestStop();
            var thread = _thread;
            if (thread != null && thread != Thread.CurrentThread && !thread.Join(StopWaitMs))
                Console.Error.WriteLine("[VideoCapture] worker is still stopping; PPI remains available");
            lock (_cleanupLock)
            {
                _disposeRequested = true;
                if (thread == null || _workerExited) DisposeHandles();
            }
        }

        public void RequestStop()
        {
            lock (_cleanupLock)
            {
                if (_handlesDisposed) return;
                _stop.Cancel();
                _wake.Set();
            }
        }

        private void Run()
        {
            IVideoCaptureSource source = null;
            string openError = null, activeBackend = null, lastDetail = null;
            long nextOpen = 0;
            bool ownsCapture = false;
            bool fullFrameEnabled = false;
            int probeFailures = 0;
            IHyperHdrFrameTarget previousTarget = null;
            var selector = new FullFrameSelector();
            var wakeOrStop = new WaitHandle[] { _stop.Token.WaitHandle, _wake };
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _lastWorkerStart, Environment.TickCount64);
                    try
                    {
                        var target = _target();
                        if (!ReferenceEquals(previousTarget, target))
                        {
                            CloseSource(ref source, ref ownsCapture);
                            previousTarget = target;
                            openError = null;
                            nextOpen = 0;
                            fullFrameEnabled = false;
                            probeFailures = 0;
                            selector = new FullFrameSelector();
                        }
                        if (target == null || !target.FullFrameEnabled)
                        {
                            CloseSource(ref source, ref ownsCapture);
                            openError = null;
                            nextOpen = 0;
                            fullFrameEnabled = false;
                            probeFailures = 0;
                            selector = new FullFrameSelector();
                            Report("ppi-edge", null, ref activeBackend, ref lastDetail);
                        }
                        else
                        {
                            if (!fullFrameEnabled)
                            {
                                CloseSource(ref source, ref ownsCapture);
                                fullFrameEnabled = true;
                                openError = null;
                                nextOpen = 0;
                                probeFailures = 0;
                                selector = new FullFrameSelector();
                            }
                            long start = Environment.TickCount64;
                            if (source == null && start >= nextOpen)
                            {
                                ownsCapture = Interlocked.CompareExchange(ref _captureOwner, 1, 0) == 0;
                                if (!ownsCapture) openError = "previous full-frame worker is still stopping";
                                else
                                {
                                    try { source = _open(out openError); }
                                    catch (Exception ex) { openError = $"opening video capture failed: {ex.Message}"; }
                                    if (source == null)
                                    {
                                        if (openError == null) openError = "video capture is unavailable";
                                        CloseSource(ref source, ref ownsCapture);
                                        probeFailures++;
                                    }
                                }
                                nextOpen = probeFailures >= MaximumProbeFailures
                                    ? long.MaxValue : Environment.TickCount64 + _openRetryMs;
                                if (probeFailures >= MaximumProbeFailures)
                                    openError = $"{openError}. Turn full-frame off and on to retry.";
                            }

                            PixelFrame pixels = null;
                            string error = openError;
                            bool usable = false;
                            if (_stop.IsCancellationRequested) break;
                            if (!ReferenceEquals(target, _target())) continue;
                            if (source != null)
                            {
                                try { usable = source.TryCapture(out pixels, out error); }
                                catch (Exception ex) { error = $"video capture failed: {ex.Message}"; }
                                if (source.IsBroken)
                                {
                                    CloseSource(ref source, ref ownsCapture);
                                    openError = error ?? "full-frame probe disconnected";
                                    probeFailures++;
                                    nextOpen = probeFailures >= MaximumProbeFailures
                                        ? long.MaxValue : Environment.TickCount64 + _openRetryMs;
                                    if (probeFailures >= MaximumProbeFailures)
                                        openError = $"{openError}. Turn full-frame off and on to retry.";
                                    error = openError;
                                }
                            }
                            if (usable && pixels == null)
                            {
                                usable = false;
                                error = "video capture returned no image";
                            }
                            if (usable) probeFailures = 0;
                            var edge = Volatile.Read(ref _edge);
                            if (usable && edge != null && IsDarkMismatch(pixels, edge.Frame, _points,
                                edge.Reliable, Environment.TickCount64 - edge.Ticks))
                            {
                                usable = false;
                                error = "video plane is dark while PPI samples are bright";
                            }
                            if (_stop.IsCancellationRequested) break;
                            if (!ReferenceEquals(target, _target())) continue;
                            if (Interlocked.Exchange(ref _stalled, 0) != 0)
                                selector = new FullFrameSelector();

                            switch (selector.Update(usable))
                            {
                                case FrameChoice.Video:
                                    target.Offer(HyperHdrFrame.FromPixels(pixels));
                                    _status.FullFrame((int)Math.Min(int.MaxValue, Math.Max(0, Environment.TickCount64 - start)));
                                    Report("secvideo-nv12", null, ref activeBackend, ref lastDetail,
                                        $"{pixels.Width}x{pixels.Height}");
                                    break;
                                case FrameChoice.Edge:
                                    if (edge != null) target.Offer(HyperHdrFrame.FromEdge(edge.Frame));
                                    Report("ppi-edge", error, ref activeBackend, ref lastDetail);
                                    break;
                                case FrameChoice.Hold:
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CloseSource(ref source, ref ownsCapture);
                        openError = $"video worker failed: {ex.Message}";
                        nextOpen = Environment.TickCount64 + _openRetryMs;
                        selector = new FullFrameSelector();
                        Report("ppi-edge", openError, ref activeBackend, ref lastDetail);
                    }

                    WaitHandle.WaitAny(wakeOrStop, CaptureIntervalMs);
                }
            }
            finally
            {
                CloseSource(ref source, ref ownsCapture);
                lock (_cleanupLock)
                {
                    _workerExited = true;
                    if (_disposeRequested) DisposeHandles();
                }
            }
        }

        private static void CloseSource(ref IVideoCaptureSource source, ref bool ownsCapture)
        {
            try { source?.Dispose(); }
            catch (Exception ex) { Console.Error.WriteLine($"[VideoCaptureCloseError] {ex.Message}"); }
            finally
            {
                source = null;
                if (ownsCapture) Interlocked.Exchange(ref _captureOwner, 0);
                ownsCapture = false;
            }
        }

        private void DisposeHandles()
        {
            if (_handlesDisposed) return;
            _handlesDisposed = true;
            _wake.Dispose();
            _stop.Dispose();
        }

        private void Report(string backend, string error, ref string activeBackend, ref string lastDetail,
            string info = null)
        {
            _status.Backend(backend, error);
            string detail = info ?? error;
            if (backend != activeBackend || detail != lastDetail)
            {
                try { _log("CaptureBackend", $"source={backend}" + (detail != null ? $" detail={detail}" : "")); }
                catch (Exception ex) { Console.Error.WriteLine($"[CaptureBackend] {ex.Message}"); }
            }
            activeBackend = backend;
            lastDetail = detail;
        }

        internal static bool IsDarkMismatch(PixelFrame video, EdgeFrame edge, CapturePoint[] points,
            bool reliable, long edgeAgeMs)
        {
            if (!reliable || edgeAgeMs < 0 || edgeAgeMs > MaximumEdgeAgeMs ||
                video == null || edge == null || points == null || points.Length * 3 != edge.Rgb.Length)
                return false;
            int brightSamples = 0;
            for (int i = 0; i < points.Length; i++)
            {
                int x = Math.Max(0, Math.Min(video.Width - 1, (int)(points[i].X * video.Width)));
                int y = Math.Max(0, Math.Min(video.Height - 1, (int)(points[i].Y * video.Height)));
                int v = (y * video.Width + x) * 3, p = i * 3;
                if (Math.Max(video.Rgb[v], Math.Max(video.Rgb[v + 1], video.Rgb[v + 2])) > 8)
                    return false;
                if (Math.Max(edge.Rgb[p], Math.Max(edge.Rgb[p + 1], edge.Rgb[p + 2])) >= 48)
                    brightSamples++;
            }
            return brightSamples >= 3;
        }
    }
}
