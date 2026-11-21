using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Tizen.Applications;

namespace Service
{
    // This application is launched as a separate Tizen process, not a thread in the service.
    sealed class ProbeApp : ServiceApplication
    {
        private Thread _worker;

        protected override void OnAppControlReceived(AppControlReceivedEventArgs e)
        {
            base.OnAppControlReceived(e);
            var control = e.ReceivedAppControl;
            if (_worker != null && _worker.IsAlive) return;
            if (control.CallerApplicationId != "fCAYP0k0uD.Service") { Exit(); return; }
            if (!control.ExtraData.TryGet("port", out string portText) ||
                !int.TryParse(portText, out int port) || port < 1 || port > 65535 ||
                !control.ExtraData.TryGet("nonce", out string nonce) || !ValidNonce(nonce))
            {
                Exit();
                return;
            }
            _worker = new Thread(() => Serve(port, nonce)) { IsBackground = true, Name = "Video probe" };
            _worker.Start();
        }

        private static bool ValidNonce(string nonce)
        {
            if (string.IsNullOrEmpty(nonce)) return false;
            try { return Convert.FromBase64String(nonce).Length == 32; }
            catch (FormatException) { return false; }
        }

        private void Serve(int port, string nonce)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 5000;
                using var stream = client.GetStream();
                byte[] token = Encoding.ASCII.GetBytes(nonce);
                stream.Write(token, 0, token.Length);
                using var capture = VideoCapture.TryOpen(out string openError);
                while (true)
                {
                    int command = stream.ReadByte();
                    if (command == -1 || command == ProbeWire.Stop) break;
                    if (command != ProbeWire.Capture) throw new InvalidDataException("invalid probe request");
                    if (capture == null)
                    {
                        ProbeWire.WriteError(stream, openError, true);
                        break;
                    }
                    if (capture.TryCapture(out var frame, out string error)) ProbeWire.WriteFrame(stream, frame);
                    else ProbeWire.WriteError(stream, error, false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is SocketException || ex is InvalidDataException)
            {
                Console.Error.WriteLine($"[VideoProbe] {ex.Message}");
            }
            finally { Exit(); }
        }

        static void Main(string[] args) => new ProbeApp().Run(args);
    }
}
