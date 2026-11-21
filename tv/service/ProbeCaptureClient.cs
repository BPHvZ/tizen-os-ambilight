using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Service
{
    interface IVideoCaptureSource : IDisposable
    {
        bool TryCapture(out PixelFrame frame, out string error);
        bool IsBroken { get; }
    }

    // No video is captured in this process. A per-launch nonce authenticates the loopback peer.
    sealed class ProbeCaptureClient : IVideoCaptureSource
    {
        private const int DefaultTimeoutMs = 5000;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        public bool IsBroken { get; private set; }

        private ProbeCaptureClient(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        internal static IVideoCaptureSource Open(Action<int, string> launch, out string error,
            int timeoutMs = DefaultTimeoutMs, int? captureTimeoutMs = null)
        {
            error = null;
            if (timeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            if (captureTimeoutMs.HasValue && captureTimeoutMs.Value < 1)
                throw new ArgumentOutOfRangeException(nameof(captureTimeoutMs));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            TcpClient client = null;
            try
            {
                listener.Start();
                var nonceBytes = new byte[32];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonceBytes);
                string nonce = Convert.ToBase64String(nonceBytes);
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                launch(port, nonce);
                long deadline = Environment.TickCount64 + timeoutMs;
                while (!listener.Pending())
                {
                    if (Environment.TickCount64 >= deadline)
                        throw new TimeoutException("full-frame probe did not start");
                    Thread.Sleep(20);
                }
                client = listener.AcceptTcpClient();
                client.ReceiveTimeout = timeoutMs;
                client.SendTimeout = timeoutMs;
                client.NoDelay = true;
                var stream = client.GetStream();
                byte[] expected = Encoding.ASCII.GetBytes(nonce);
                var actual = new byte[expected.Length];
                int received = 0;
                while (received < actual.Length)
                {
                    int n = stream.Read(actual, received, actual.Length - received);
                    if (n == 0) throw new EndOfStreamException("full-frame probe disconnected during startup");
                    received += n;
                }
                int difference = 0;
                for (int i = 0; i < actual.Length; i++) difference |= actual[i] ^ expected[i];
                if (difference != 0) throw new InvalidDataException("full-frame probe authentication failed");
                client.ReceiveTimeout = captureTimeoutMs ?? timeoutMs;
                client.SendTimeout = captureTimeoutMs ?? timeoutMs;
                var result = new ProbeCaptureClient(client);
                client = null;
                return result;
            }
            catch (Exception ex)
            {
                error = $"Full-frame check could not start: {ex.Message}";
                return null;
            }
            finally
            {
                client?.Dispose();
                listener.Stop();
            }
        }

        public bool TryCapture(out PixelFrame frame, out string error)
        {
            frame = null;
            error = null;
            if (IsBroken) { error = "Full-frame probe is unavailable"; return false; }
            try
            {
                _stream.WriteByte(ProbeWire.Capture);
                bool ok = ProbeWire.ReadReply(_stream, out frame, out error, out bool unavailable);
                IsBroken = unavailable;
                return ok;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                                       ex is SocketException || ex is ObjectDisposedException)
            {
                IsBroken = true;
                error = $"Full-frame probe stopped or timed out: {ex.Message}";
                return false;
            }
        }

        public void Dispose()
        {
            try { if (!IsBroken) _stream.WriteByte(ProbeWire.Stop); }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            _client.Dispose();
        }
    }
}
