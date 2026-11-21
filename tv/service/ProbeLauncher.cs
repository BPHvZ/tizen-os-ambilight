using System.Globalization;
using Tizen.Applications;

namespace Service
{
    static class ProbeLauncher
    {
        public static IVideoCaptureSource Open(out string error) =>
            ProbeCaptureClient.Open(Launch, out error);

        private static void Launch(int port, string nonce)
        {
            var control = new AppControl
            {
                ApplicationId = "fCAYP0k0uD.VideoProbe",
                Operation = AppControlOperations.Default,
                LaunchMode = AppControlLaunchMode.Group
            };
            control.ExtraData.Add("port", port.ToString(CultureInfo.InvariantCulture));
            control.ExtraData.Add("nonce", nonce);
            AppControl.SendLaunchRequest(control);
        }
    }
}
